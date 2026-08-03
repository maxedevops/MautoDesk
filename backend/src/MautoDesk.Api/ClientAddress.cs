using System.Net;
using MautoDesk.SharedKernel;
using Microsoft.Extensions.Options;

namespace MautoDesk.Api;

/// <summary>Which upstream hops may be believed about who the caller is.</summary>
public sealed class TrustedProxyOptions
{
    public const string SectionName = "Network";

    /// <summary>
    /// Addresses or CIDR ranges permitted to assert a client address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to loopback and the private ranges, which covers a reverse proxy
    /// on the same host or the same container network. <b>Cloudflare's ranges
    /// are public and must be listed explicitly</b> — otherwise the origin sees
    /// Cloudflare as an untrusted peer and every caller shares one rate-limit
    /// bucket.
    /// </para>
    /// <para>
    /// Emptying this list is the correct setting for an origin with nothing in
    /// front of it: no forwarded header is believed, and the socket address is
    /// the only truth available.
    /// </para>
    /// </remarks>
    public IList<string> TrustedProxies { get; } =
    [
        "127.0.0.1/32",
        "::1/128",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
    ];
}

/// <summary>
/// Resolves the caller's address across whatever is in front of the origin.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a security control, not a logging nicety.</b> The per-IP auth
/// limit is what stops credential stuffing — one password tried against
/// thousands of accounts, never tripping a single account lockout. If every
/// request appears to come from the reverse proxy, that limit becomes one shared
/// bucket and stops working; if a forwarded header is believed without checking
/// who sent it, an attacker rotates the header and gets an unlimited budget.
/// Both failures are silent.
/// </para>
/// <para>
/// So: a forwarded header is read <em>only</em> when the immediate peer is a
/// configured trusted proxy, and the chain is walked from the right, skipping
/// hops that are themselves trusted. The first untrusted address is the furthest
/// point we have any reason to believe.
/// </para>
/// </remarks>
public static class ClientAddressResolver
{
    /// <summary>Cloudflare's own header, which carries a single address rather than a chain.</summary>
    public const string CloudflareHeader = "CF-Connecting-IP";

    public const string ForwardedForHeader = "X-Forwarded-For";

    public static IPAddress? Resolve(
        IPAddress? peer,
        string? cloudflareHeader,
        string? forwardedForHeader,
        IReadOnlyList<IPNetwork> trustedProxies)
    {
        ArgumentNullException.ThrowIfNull(trustedProxies);

        if (peer is null)
        {
            return null;
        }

        // An untrusted peer is the client, whatever it claims about itself.
        if (!IsTrusted(peer, trustedProxies))
        {
            return peer;
        }

        if (!string.IsNullOrWhiteSpace(cloudflareHeader) &&
            IPAddress.TryParse(cloudflareHeader.Trim(), out var cloudflare))
        {
            return cloudflare;
        }

        if (string.IsNullOrWhiteSpace(forwardedForHeader))
        {
            // A trusted proxy that forwarded nothing: the best available answer
            // is the proxy itself. Wrong, but honestly wrong.
            return peer;
        }

        var chain = forwardedForHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Right to left: the right-most entry was written by our own proxy and
        // is therefore trustworthy; entries further left were written by
        // whatever was before it, and become believable only up to the first hop
        // we do not trust. Anything left of that is attacker-controlled.
        for (var i = chain.Length - 1; i >= 0; i--)
        {
            if (!TryParseChainEntry(chain[i], out var hop))
            {
                return peer;
            }

            if (!IsTrusted(hop, trustedProxies))
            {
                return hop;
            }
        }

        return peer;
    }

    public static IReadOnlyList<IPNetwork> ParseNetworks(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var networks = new List<IPNetwork>();

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var candidate = value.Trim();

            if (IPNetwork.TryParse(candidate, out var network))
            {
                networks.Add(network);
                continue;
            }

            // A bare address is a /32 or /128.
            if (IPAddress.TryParse(candidate, out var address))
            {
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32));
                continue;
            }

            // Misconfiguration fails at startup rather than quietly widening
            // who is believed about client addresses.
            throw new InvalidOperationException(
                $"'{value}' in {TrustedProxyOptions.SectionName}:TrustedProxies is not an IP address or CIDR range.");
        }

        return networks;
    }

    private static bool IsTrusted(IPAddress address, IReadOnlyList<IPNetwork> trustedProxies)
    {
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        foreach (var network in trustedProxies)
        {
            if (network.Contains(candidate) || network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses one X-Forwarded-For entry, which may carry a port.</summary>
    private static bool TryParseChainEntry(string entry, out IPAddress address)
    {
        if (IPAddress.TryParse(entry, out var parsed))
        {
            address = parsed;
            return true;
        }

        // "192.0.2.1:53201" or "[2001:db8::1]:53201".
        var separator = entry.LastIndexOf(':');

        if (separator > 0 && IPAddress.TryParse(entry[..separator].Trim('[', ']'), out var withPort))
        {
            address = withPort;
            return true;
        }

        address = IPAddress.None;
        return false;
    }
}

/// <summary>
/// Resolves the client address once per request and puts it where everything can read it.
/// </summary>
/// <remarks>
/// One resolution per request, used by the rate limiter, the security log, and
/// the audit ledger alike. Previously each of those read the header itself,
/// which meant three chances to disagree about who the caller was — and the
/// rate limiter is the one where disagreeing is a vulnerability.
/// </remarks>
public sealed class ClientAddressMiddleware
{
    internal const string ItemKey = "mautodesk.client-address";

    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<IPNetwork> _trustedProxies;

    public ClientAddressMiddleware(RequestDelegate next, IOptions<TrustedProxyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _next = next;
        _trustedProxies = ClientAddressResolver.ParseNetworks(options.Value.TrustedProxies);
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolved = ClientAddressResolver.Resolve(
            context.Connection.RemoteIpAddress,
            context.Request.Headers[ClientAddressResolver.CloudflareHeader].FirstOrDefault(),
            context.Request.Headers[ClientAddressResolver.ForwardedForHeader].FirstOrDefault(),
            _trustedProxies);

        if (resolved is not null)
        {
            context.Items[ItemKey] = resolved.ToString();
        }

        return _next(context);
    }
}

/// <summary>The caller's address, as resolved once at the edge of the request.</summary>
public static class ClientAddress
{
    /// <summary>
    /// Null when there is no address at all, which is a real case in tests and
    /// on unix sockets. Callers record null rather than inventing a value —
    /// the <c>inet</c> column and the audit ledger both prefer an absent
    /// address to a wrong one.
    /// </summary>
    public static string? Of(HttpContext? context)
    {
        if (context is null)
        {
            return null;
        }

        return context.Items.TryGetValue(ClientAddressMiddleware.ItemKey, out var resolved) && resolved is string address
            ? address

            // The middleware has not run — a request short-circuited before it,
            // or a test host without the pipeline. Fall back to the socket,
            // which is never spoofable even if it is sometimes a proxy.
            : context.Connection.RemoteIpAddress?.ToString();
    }
}
