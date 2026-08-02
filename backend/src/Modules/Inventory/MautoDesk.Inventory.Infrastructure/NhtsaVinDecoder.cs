using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MautoDesk.Infrastructure.Persistence;
using MautoDesk.Inventory.Application;
using MautoDesk.Inventory.Contracts;
using MautoDesk.Inventory.Domain;
using MautoDesk.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MautoDesk.Inventory.Infrastructure;

/// <summary>
/// Decodes VINs via the NHTSA vPIC API, cached in <c>inventory.vin_decode_cache</c>.
/// </summary>
/// <remarks>
/// <para>
/// vPIC is free, public, and needs no key, which makes it the right launch
/// choice. It is also <b>incomplete</b>: trim and factory options are missing or
/// wrong for many vehicles, which matters the moment pricing depends on trim.
/// The <see cref="IVinDecoder"/> abstraction exists so a paid decoder can
/// replace this without touching a single caller (Phase 1 §8).
/// </para>
/// <para>
/// The cache is deliberately <b>not tenant-scoped</b>. A VIN decodes identically
/// for everyone, the response contains no customer information, and NHTSA's rate
/// limits are real — so one dealer's lookup warms the cache for the next. This is
/// one of the few tables in the system with no <c>tenant_id</c>, and it is listed
/// explicitly in <c>app.rls_exempt_table</c> so the decision is visible in review
/// rather than implied by absence.
/// </para>
/// <para>
/// <b>This class never throws on an upstream failure.</b> A dealer standing next
/// to a car must be able to book it whether or not a government API is up, so a
/// timeout returns an Unavailable result and the caller proceeds with manual
/// entry.
/// </para>
/// </remarks>
public sealed partial class NhtsaVinDecoder : IVinDecoder
{
    /// <summary>How long a cached decode is considered fresh.</summary>
    /// <remarks>
    /// Long, because a VIN's decoded identity is a fact about a manufactured
    /// object and does not change. The expiry exists only so provider
    /// corrections eventually propagate.
    /// </remarks>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(180);

    private const string ProviderName = "nhtsa_vpic";

    private readonly HttpClient _http;
    private readonly MautoDeskDbContext _db;
    private readonly IClock _clock;
    private readonly ILogger<NhtsaVinDecoder> _logger;

    public NhtsaVinDecoder(
        HttpClient http,
        MautoDeskDbContext db,
        IClock clock,
        ILogger<NhtsaVinDecoder> logger)
    {
        _http = http;
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<VinDecodeDto>> DecodeAsync(
        Vin vin,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        if (!bypassCache)
        {
            var cached = await ReadCacheAsync(vin, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return Result<VinDecodeDto>.Success(cached);
            }
        }

        VpicResponse? response;
        try
        {
            response = await _http
                .GetFromJsonAsync<VpicResponse>(
                    $"vehicles/decodevin/{vin.Value}?format=json",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            LogDecoderUnavailable(_logger, vin.Value, ex);
            return Unavailable();
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout, not a caller cancellation. Distinguishing the two matters:
            // one is an upstream problem worth reporting, the other is the user
            // navigating away and is not worth a log line.
            LogDecoderUnavailable(_logger, vin.Value, ex);
            return Unavailable();
        }

        if (response?.Results is null || response.Results.Count == 0)
        {
            return Unavailable();
        }

        var fields = response.Results.ToDictionary(
            r => r.Variable ?? string.Empty,
            r => string.IsNullOrWhiteSpace(r.Value) || r.Value == "Not Applicable" ? null : r.Value,
            StringComparer.OrdinalIgnoreCase);

        var dto = new VinDecodeDto(
            vin.Value,
            ProviderName,
            FromCache: false,
            CheckDigitValid: vin.HasValidCheckDigit,
            ModelYear: ParseYear(Get(fields, "Model Year")),
            Make: Titleize(Get(fields, "Make")),
            Model: Get(fields, "Model"),
            Trim: Get(fields, "Trim"),
            BodyStyle: Get(fields, "Body Class"),
            DriveType: Get(fields, "Drive Type"),
            Engine: BuildEngine(fields),
            FuelType: Get(fields, "Fuel Type - Primary"),
            Transmission: Get(fields, "Transmission Style"),
            Manufacturer: Get(fields, "Manufacturer Name"),
            ErrorText: Get(fields, "Error Text"));

        await WriteCacheAsync(dto, cancellationToken).ConfigureAwait(false);
        return Result<VinDecodeDto>.Success(dto);
    }

    private static Result<VinDecodeDto> Unavailable() => Error.Unavailable(
        "vin.decoder.unavailable",
        "The VIN decoder is not responding. You can still enter the vehicle details by hand — " +
        "we will fill in the rest automatically once the service is back.");

    private async Task<VinDecodeDto?> ReadCacheAsync(Vin vin, CancellationToken cancellationToken)
    {
        var rows = await _db.Database
            .SqlQueryRaw<CacheRow>(
                """
                select vin as "Vin", decoded_at as "DecodedAt", model_year as "ModelYear",
                       make as "Make", model as "Model", trim as "Trim", body_class as "BodyClass",
                       drive_type as "DriveType", engine_model as "EngineModel",
                       engine_cylinders as "EngineCylinders", engine_displacement_l as "Displacement",
                       fuel_type as "FuelType", transmission_style as "TransmissionStyle",
                       manufacturer as "Manufacturer", error_text as "ErrorText"
                  from inventory.vin_decode_cache
                 where vin = {0}
                """,
                vin.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = rows.FirstOrDefault();
        if (row is null || _clock.UtcNow - row.DecodedAt > CacheLifetime)
        {
            return null;
        }

        return new VinDecodeDto(
            row.Vin,
            ProviderName,
            FromCache: true,
            CheckDigitValid: vin.HasValidCheckDigit,
            row.ModelYear,
            row.Make,
            row.Model,
            row.Trim,
            row.BodyClass,
            row.DriveType,
            ComposeEngine(row.EngineModel, row.EngineCylinders, row.Displacement),
            row.FuelType,
            row.TransmissionStyle,
            row.Manufacturer,
            row.ErrorText);
    }

    private async Task WriteCacheAsync(VinDecodeDto dto, CancellationToken cancellationToken)
    {
        // Upsert. Two dealers scanning the same VIN at the same moment is
        // ordinary, and losing that race should be a no-op, not an exception.
        await _db.Database.ExecuteSqlRawAsync(
            """
            insert into inventory.vin_decode_cache
                (vin, provider, decoded_at, raw_response, model_year, make, model, trim,
                 body_class, drive_type, fuel_type, transmission_style, manufacturer, error_text)
            values (@vin, @provider, now(), '{}'::jsonb, @year, @make, @model, @trim, @body, @drive, @fuel, @trans, @mfr, @err)
            on conflict (vin) do update set
                decoded_at = excluded.decoded_at,
                model_year = excluded.model_year,
                make = excluded.make,
                model = excluded.model,
                trim = excluded.trim,
                body_class = excluded.body_class,
                drive_type = excluded.drive_type,
                fuel_type = excluded.fuel_type,
                transmission_style = excluded.transmission_style,
                manufacturer = excluded.manufacturer,
                error_text = excluded.error_text
            """,
            // Explicit DbParameter instances rather than a plain object array:
            // EF's raw-SQL overload takes IEnumerable<object>, which cannot carry
            // a null, and DBNull.Value in that array makes EF throw. Named
            // parameters also mean an inserted column cannot silently shift the
            // ones after it.
            new[]
            {
                Db("vin", dto.Vin),
                Db("provider", ProviderName),
                Db("year", dto.ModelYear),
                Db("make", dto.Make),
                Db("model", dto.Model),
                Db("trim", dto.Trim),
                Db("body", dto.BodyStyle),
                Db("drive", dto.DriveType),
                Db("fuel", dto.FuelType),
                Db("trans", dto.Transmission),
                Db("mfr", dto.Manufacturer),
                Db("err", dto.ErrorText),
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A named parameter, mapping null onto SQL NULL.</summary>
    private static Npgsql.NpgsqlParameter Db(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    private static string? Get(Dictionary<string, string?> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : null;

    private static int? ParseYear(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;

    /// <summary>vPIC returns makes in caps ("FORD"); dealers write "Ford".</summary>
    private static string? Titleize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());

    private static string? BuildEngine(Dictionary<string, string?> fields) => ComposeEngine(
        Get(fields, "Engine Model"),
        int.TryParse(Get(fields, "Engine Number of Cylinders"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var cylinders) ? cylinders : null,
        decimal.TryParse(Get(fields, "Displacement (L)"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var litres) ? litres : null);

    private static string? ComposeEngine(string? model, int? cylinders, decimal? litres)
    {
        var parts = new List<string>();

        if (litres is { } l)
        {
            parts.Add(l.ToString("0.0", CultureInfo.InvariantCulture) + "L");
        }

        if (cylinders is { } c)
        {
            parts.Add("V" + c.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            parts.Add(model);
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "VIN decode for {Vin} failed; continuing without decoded fields.")]
    private static partial void LogDecoderUnavailable(ILogger logger, string vin, Exception exception);

    private sealed record CacheRow(
        string Vin,
        DateTimeOffset DecodedAt,
        int? ModelYear,
        string? Make,
        string? Model,
        string? Trim,
        string? BodyClass,
        string? DriveType,
        string? EngineModel,
        int? EngineCylinders,
        decimal? Displacement,
        string? FuelType,
        string? TransmissionStyle,
        string? Manufacturer,
        string? ErrorText);

    private sealed record VpicResponse
    {
        [JsonPropertyName("Results")]
        public IReadOnlyList<VpicResult>? Results { get; init; }
    }

    private sealed record VpicResult
    {
        [JsonPropertyName("Variable")]
        public string? Variable { get; init; }

        [JsonPropertyName("Value")]
        public string? Value { get; init; }
    }
}
