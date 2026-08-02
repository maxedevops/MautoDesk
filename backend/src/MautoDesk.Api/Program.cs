using System.Diagnostics.CodeAnalysis;
using System.Text;
using MautoDesk.Api;
using MautoDesk.Identity.Application;
using MautoDesk.Identity.Infrastructure;
using MautoDesk.Infrastructure;
using MautoDesk.Infrastructure.Persistence;
using MautoDesk.Infrastructure.Tenancy;
using MautoDesk.Inventory.Application;
using MautoDesk.Inventory.Infrastructure;
using MautoDesk.SharedKernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Logging first, so anything the rest of startup writes is already redacted.
builder.Host.UseSerilog((context, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ForMautoDesk());

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? throw new InvalidOperationException(
        "No database connection string. Set ConnectionStrings:Default or DATABASE_URL. " +
        "The application connects as mautodesk_app, which has no BYPASSRLS — using a " +
        "superuser connection here would disable every tenant isolation policy.");

var vinDecoderBaseUrl = builder.Configuration["VinDecoder:BaseUrl"] ?? "https://vpic.nhtsa.dot.gov/api/";

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection("Encryption"));
builder.Services.Configure<ObjectStorageOptions>(
    builder.Configuration.GetSection(ObjectStorageOptions.SectionName));
builder.Services.Configure<MalwareScanningOptions>(
    builder.Configuration.GetSection(MalwareScanningOptions.SectionName));

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

if (string.IsNullOrWhiteSpace(jwt.SigningKey))
{
    // Refusing to start beats generating a key at runtime. A per-process key
    // would appear to work, then invalidate every token on restart and behave
    // unpredictably behind more than one instance.
    throw new InvalidOperationException(
        "Jwt:SigningKey is not configured. Generate one with `openssl rand -base64 32` and " +
        "supply it through configuration or the JWT_SIGNING_KEY environment variable.");
}

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IClock, SystemClock>();

builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<ITenantScopeSetter>(sp => sp.GetRequiredService<TenantContext>());

builder.Services.AddScoped<TenantConnectionInterceptor>();
builder.Services.AddSingleton<IModuleSchema, InventorySchema>();
builder.Services.AddSingleton<IModuleSchema, IdentitySchema>();

builder.Services.AddDbContext<MautoDeskDbContext>((sp, options) =>
{
    options.UseNpgsql(connectionString);
    options.AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>());
});

// The audit ledger. Scoped, because an entry joins the request's unit of work
// and commits with the change it describes.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
builder.Services.AddScoped<IAuditLog, AuditLog>();

// Inventory
builder.Services.AddScoped<MautoDesk.Inventory.Application.IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IVehicleRepository, VehicleRepository>();
builder.Services.AddScoped<IVehicleReadStore, VehicleReadStore>();
builder.Services.AddScoped<IStockNumberGenerator, StockNumberGenerator>();
builder.Services.AddScoped<VehicleCommandHandler>();
builder.Services.AddScoped<VehicleQueryHandler>();
builder.Services.AddScoped<IPhotoRepository, PhotoRepository>();
builder.Services.AddScoped<PhotoCommandHandler>();
builder.Services.AddScoped<PhotoQueryHandler>();
builder.Services.AddSingleton<IImageProcessor, SkiaImageProcessor>();

// Storage and upload scanning. Singletons: both hold a client or socket
// configuration, neither holds request state.
builder.Services.AddSingleton<IObjectStore, S3ObjectStore>();
builder.Services.AddSingleton<IMalwareScanner, ClamAvScanner>();

// Identity
builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddSingleton<ITotpService, TotpService>();
builder.Services.AddSingleton<ITokenIssuer, TokenIssuer>();
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
builder.Services.AddSingleton<IRecoveryCodeService, RecoveryCodeService>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ISessionRepository, SessionRepository>();
builder.Services.AddScoped<MautoDesk.Identity.Application.IUnitOfWork, IdentityUnitOfWork>();
builder.Services.AddScoped<AuthenticationService>();

builder.Services.AddHttpClient<IVinDecoder, NhtsaVinDecoder>(client =>
{
    client.BaseAddress = new Uri(vinDecoderBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
})
.AddStandardResilienceHandler();

// ---------------------------------------------------------------------------
// Authentication
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(DecodeSigningKey(jwt.SigningKey)),
            ValidateLifetime = true,

            // No clock skew. The default five minutes would extend a 15-minute
            // access token to 20, and a revoked session would stay usable a third
            // longer than intended.
            ClockSkew = TimeSpan.Zero,

            // Only HMAC-SHA256 is accepted. Without this an attacker could
            // present a token signed with "alg": "none" or a weaker algorithm and
            // some validators would honour it.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };

        options.MapInboundClaims = false;

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                // Never echo why validation failed. "Signature invalid" versus
                // "token expired" is useful feedback to an attacker probing a
                // forged token.
                context.NoResult();
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

builder.Services.AddProblemDetails();
builder.Services.AddMautoDeskRateLimiting(builder.Configuration);

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<MautoDeskDocumentTransformer>();
    options.AddOperationTransformer<MautoDeskOperationTransformer>();
    options.AddSchemaTransformer<SensitiveSchemaTransformer>();
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
app.UseExceptionHandler();

// One line per request, with the query string scrubbed. Placed before
// authentication so a request refused at the edge is still accounted for.
app.UseSerilogRequestLogging(options =>
    options.EnrichDiagnosticContext = LoggingConfiguration.EnrichRequest);

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Frame-Options"] = "DENY";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["Cross-Origin-Resource-Policy"] = "same-origin";
    headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

    // Tokens and problem details must never sit in a shared cache.
    headers["Cache-Control"] = "no-store";

    await next().ConfigureAwait(false);
});

// Rate limiting sits AFTER authentication so per-user and per-tenant
// partitions can see who is calling; the auth policy keys on address only and
// works either way.
app.UseAuthentication();

// Must sit between authentication and authorization: it reads the validated
// principal and establishes the tenant scope that RLS depends on.
app.UseMiddleware<TenantClaimMiddleware>();

app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapAuthEndpoints();
app.MapInventoryEndpoints();
app.MapPhotoEndpoints();

await app.RunAsync().ConfigureAwait(false);

static byte[] DecodeSigningKey(string configured)
{
    try
    {
        return Convert.FromBase64String(configured);
    }
    catch (FormatException)
    {
        return Encoding.UTF8.GetBytes(configured);
    }
}

/// <summary>Exposed so the integration tests can drive the real host.</summary>
[SuppressMessage(
    "Design",
    "CA1052:Static holder types should be static",
    Justification = "WebApplicationFactory<TEntryPoint> requires a non-static entry point type.")]
public partial class Program
{
    protected Program()
    {
    }
}
