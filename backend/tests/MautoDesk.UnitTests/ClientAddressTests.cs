using System.Net;
using FluentAssertions;
using MautoDesk.Api;
using MautoDesk.Infrastructure;
using MautoDesk.SharedKernel;
using Microsoft.Extensions.Options;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// Who the caller is, when something is in front of the origin.
/// </summary>
/// <remarks>
/// The per-IP auth limit is the control that stops credential stuffing, and it
/// is only as good as this resolution. Two ways to break it, both silent: trust
/// a header from anyone and an attacker rotates it for an unlimited budget;
/// ignore it behind a proxy and every caller in the world shares one bucket.
/// </remarks>
public sealed class ClientAddressResolverTests
{
    private static readonly IReadOnlyList<IPNetwork> TrustedProxies =
        ClientAddressResolver.ParseNetworks(["10.0.0.0/8", "172.16.0.0/12", "127.0.0.1/32"]);

    private static readonly IReadOnlyList<IPNetwork> NothingTrusted = [];

    /// <summary>
    /// The header is worthless from someone who is not a proxy.
    /// </summary>
    /// <remarks>
    /// This is the spoofing case. A directly-exposed origin that believed this
    /// header would let one attacker present a new address per request and never
    /// meet the limiter at all.
    /// </remarks>
    [Fact]
    public void Ignores_a_forwarded_header_from_an_untrusted_peer()
    {
        var resolved = ClientAddressResolver.Resolve(
            IPAddress.Parse("203.0.113.9"),
            cloudflareHeader: "198.51.100.1",
            forwardedForHeader: "198.51.100.2",
            TrustedProxies);

        resolved.Should().Be(IPAddress.Parse("203.0.113.9"), "the peer is the client, whatever it claims");
    }

    [Fact]
    public void Believes_cloudflare_when_the_peer_is_a_trusted_proxy()
    {
        var resolved = ClientAddressResolver.Resolve(
            IPAddress.Parse("10.1.2.3"),
            cloudflareHeader: "198.51.100.1",
            forwardedForHeader: null,
            TrustedProxies);

        resolved.Should().Be(IPAddress.Parse("198.51.100.1"));
    }

    /// <summary>
    /// The reverse-proxy case: Caddy or Traefik sends X-Forwarded-For, not Cloudflare's header.
    /// </summary>
    [Fact]
    public void Reads_x_forwarded_for_from_a_trusted_proxy()
    {
        var resolved = ClientAddressResolver.Resolve(
            IPAddress.Parse("172.18.0.5"),
            cloudflareHeader: null,
            forwardedForHeader: "198.51.100.1",
            TrustedProxies);

        resolved.Should().Be(IPAddress.Parse("198.51.100.1"));
    }

    /// <summary>
    /// The chain is walked from the right, and stops at the first untrusted hop.
    /// </summary>
    /// <remarks>
    /// Everything left of that hop was written by a machine we have no reason to
    /// believe — including, potentially, the attacker's own client.
    /// </remarks>
    [Fact]
    public void Walks_the_chain_from_the_right_and_stops_at_the_first_untrusted_hop()
    {
        var resolved = ClientAddressResolver.Resolve(
            IPAddress.Parse("172.18.0.5"),
            cloudflareHeader: null,
            forwardedForHeader: "1.1.1.1, 198.51.100.1, 10.4.4.4",
            TrustedProxies);

        // 10.4.4.4 is ours, 198.51.100.1 is the furthest we can believe, and
        // 1.1.1.1 is whatever the caller felt like writing.
        resolved.Should().Be(IPAddress.Parse("198.51.100.1"));
    }

    [Fact]
    public void Handles_a_chain_entry_that_carries_a_port()
    {
        var resolved = ClientAddressResolver.Resolve(
            IPAddress.Parse("10.0.0.1"),
            cloudflareHeader: null,
            forwardedForHeader: "198.51.100.1:41234",
            TrustedProxies);

        resolved.Should().Be(IPAddress.Parse("198.51.100.1"));
    }

    /// <summary>
    /// An empty trust list is the setting for a directly-exposed origin.
    /// </summary>
    [Fact]
    public void Trusts_nothing_when_no_proxy_is_configured()
    {
        var resolved = ClientAddressResolver.Resolve(
            IPAddress.Parse("127.0.0.1"),
            cloudflareHeader: "198.51.100.1",
            forwardedForHeader: "198.51.100.2",
            NothingTrusted);

        resolved.Should().Be(IPAddress.Parse("127.0.0.1"));
    }

    /// <summary>
    /// A trusted proxy that forwarded nothing gets recorded as itself.
    /// </summary>
    /// <remarks>
    /// Wrong, but honestly wrong — and it collapses partitions rather than
    /// opening them, which is the safer direction to fail.
    /// </remarks>
    [Fact]
    public void Falls_back_to_the_proxy_when_it_forwarded_no_address()
    {
        var resolved = ClientAddressResolver.Resolve(
            IPAddress.Parse("10.0.0.1"),
            cloudflareHeader: null,
            forwardedForHeader: null,
            TrustedProxies);

        resolved.Should().Be(IPAddress.Parse("10.0.0.1"));
    }

    [Fact]
    public void Falls_back_to_the_proxy_when_the_chain_is_unparseable()
    {
        var resolved = ClientAddressResolver.Resolve(
            IPAddress.Parse("10.0.0.1"),
            cloudflareHeader: null,
            forwardedForHeader: "not-an-address",
            TrustedProxies);

        resolved.Should().Be(IPAddress.Parse("10.0.0.1"));
    }

    [Fact]
    public void Accepts_a_bare_address_as_a_trusted_proxy()
    {
        var networks = ClientAddressResolver.ParseNetworks(["198.51.100.7"]);

        ClientAddressResolver.Resolve(
            IPAddress.Parse("198.51.100.7"),
            cloudflareHeader: "203.0.113.4",
            forwardedForHeader: null,
            networks).Should().Be(IPAddress.Parse("203.0.113.4"));
    }

    /// <summary>
    /// A typo widens who is believed, so it fails at startup instead.
    /// </summary>
    [Fact]
    public void Refuses_to_start_on_a_malformed_trusted_proxy_entry()
    {
        var parse = () => ClientAddressResolver.ParseNetworks(["10.0.0.0/8", "not-a-network"]);

        parse.Should().Throw<InvalidOperationException>().WithMessage("*not-a-network*");
    }
}

/// <summary>
/// Which endpoint a presigned URL names.
/// </summary>
/// <remarks>
/// A presigned URL is handed to a browser and its signature covers the host in
/// it, so the name has to be decided at signing time. Getting this wrong is not
/// subtle — every photo 403s — but it only shows up once something is deployed
/// behind a proxy, which is exactly when it is expensive to find.
/// </remarks>
public sealed class ObjectStorageSigningTests
{
    [Fact]
    public async Task Signs_for_the_public_endpoint_when_one_is_configured()
    {
        using var store = Build("http://minio:9000", "https://media.example.com");

        var url = await store.CreateUploadUrlAsync(
            StorageBucket.Media, "t/photo.jpg", "image/jpeg", 1024, TimeSpan.FromMinutes(5), default);

        url.Host.Should().Be("media.example.com", "the browser cannot resolve the internal name");
        url.Scheme.Should().Be("https");
    }

    [Fact]
    public async Task Falls_back_to_the_service_endpoint_when_no_public_one_is_set()
    {
        using var store = Build("http://localhost:9000", publicUrl: string.Empty);

        var url = await store.CreateDownloadUrlAsync(
            StorageBucket.Media, "t/photo.jpg", TimeSpan.FromMinutes(5), default);

        url.Host.Should().Be("localhost");

        // Plain http, not the SDK's https default — a local MinIO would
        // otherwise be handed a URL nothing can connect to.
        url.Scheme.Should().Be("http");
    }

    private static S3ObjectStore Build(string serviceUrl, string publicUrl) =>
        new(Options.Create(new ObjectStorageOptions
        {
            ServiceUrl = serviceUrl,
            PublicUrl = publicUrl,
            AccessKey = "key",
            SecretKey = "secret",
        }));
}
