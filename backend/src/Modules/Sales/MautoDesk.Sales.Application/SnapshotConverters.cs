using System.Text.Json;
using System.Text.Json.Serialization;
using MautoDesk.Sales.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Application;

/// <summary>
/// Writes <see cref="Money"/> into a snapshot as a decimal string, and reads it back.
/// </summary>
/// <remarks>
/// <para>
/// Without this, two things go wrong and neither is loud. <see cref="Money"/> has
/// a private constructor and get-only properties, so the default serializer emits
/// <c>{"amount":20000.00,"currency":"USD"}</c> and then deserializes it to
/// <b>zero</b> — because there is no constructor or settable member it can use.
/// A snapshot written that way stores the right numbers and reads back as a deal
/// worth nothing, silently.
/// </para>
/// <para>
/// The second problem is the one the whole codebase exists to avoid: the default
/// form puts the amount on the wire as a <b>JSON number</b>. The system's rule is
/// that money is a string everywhere, and it should hold most strictly inside the
/// record an auditor reads. So <c>"20000.00"</c> it is.
/// </para>
/// </remarks>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    /// <inheritdoc />
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        var parsed = Money.TryParse(raw);

        if (parsed.IsFailure)
        {
            throw new JsonException(
                $"'{raw}' is not a money amount. A snapshot stores money as a plain decimal " +
                "string; a value that will not parse means the stored record cannot be trusted.");
        }

        return parsed.Value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Writes <see cref="Percentage"/> into a snapshot as a decimal string in percent.
/// </summary>
/// <remarks>
/// Same failure as <see cref="MoneyJsonConverter"/> without it: a get-only struct
/// round-trips to zero, so a replayed 2026 deal would report a tax rate of 0% and
/// an APR of 0%. The stored form is the percent form — <c>"8.99"</c> is 8.99% —
/// matching the type's own convention so a human reading the jsonb sees what a
/// dealer would say.
/// </remarks>
public sealed class PercentageJsonConverter : JsonConverter<Percentage>
{
    /// <inheritdoc />
    public override Percentage Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        var parsed = Percentage.TryParse(raw);

        if (parsed.IsFailure)
        {
            throw new JsonException($"'{raw}' is not a rate.");
        }

        return parsed.Value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Percentage value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
