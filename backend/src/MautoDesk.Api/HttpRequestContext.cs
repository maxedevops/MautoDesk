using System.Diagnostics;
using System.Security.Claims;
using MautoDesk.SharedKernel;

namespace MautoDesk.Api;

/// <summary>
/// The caller, as the audit ledger records them.
/// </summary>
/// <remarks>
/// Lives in the host rather than in Infrastructure because it is the only layer
/// entitled to know an HTTP request exists. Outside a request — a job, a test
/// harness — <see cref="MautoDesk.Infrastructure.Persistence.NullRequestContext"/>
/// answers instead, with nulls rather than invented values.
/// </remarks>
public sealed class HttpRequestContext : IRequestContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpRequestContext(IHttpContextAccessor accessor) => _accessor = accessor;

    /// <inheritdoc />
    /// <remarks>
    /// Trusts <c>CF-Connecting-IP</c> only because the origin is locked to
    /// Cloudflare, and parses it rather than passing it through: the column is
    /// <c>inet</c>, and a junk header would turn an audited action into a 500.
    /// </remarks>
    public string? IpAddress
    {
        get
        {
            var context = _accessor.HttpContext;

            if (context is null)
            {
                return null;
            }

            var forwarded = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();

            return !string.IsNullOrWhiteSpace(forwarded) &&
                System.Net.IPAddress.TryParse(forwarded, out var parsed)
                    ? parsed.ToString()
                    : context.Connection.RemoteIpAddress?.ToString();
        }
    }

    public string? UserAgent => _accessor.HttpContext?.Request.Headers.UserAgent.FirstOrDefault();

    /// <summary>
    /// The current trace id, so a ledger entry and the logs point at each other.
    /// </summary>
    /// <remarks>
    /// The column is a uuid and W3C trace ids are 16 bytes of hex, which is
    /// exactly a uuid's worth — so the id is preserved rather than hashed into
    /// something that cannot be searched for.
    /// </remarks>
    public Guid? CorrelationId
    {
        get
        {
            var traceId = Activity.Current?.TraceId.ToHexString();

            return traceId is not null && Guid.TryParseExact(traceId, "N", out var parsed)
                ? parsed
                : null;
        }
    }

    /// <summary>
    /// Who they were at the time.
    /// </summary>
    /// <remarks>
    /// Denormalized deliberately. A ledger that stores only a user id becomes
    /// unreadable the day that user is deleted, which is precisely when someone
    /// is most likely to be reading it.
    /// </remarks>
    public string? ActorDisplay =>
        _accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Email)
        ?? _accessor.HttpContext?.User.FindFirstValue("email");
}
