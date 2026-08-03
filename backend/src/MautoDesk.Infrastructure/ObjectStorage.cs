using Amazon.S3;
using Amazon.S3.Model;
using MautoDesk.SharedKernel;
using Microsoft.Extensions.Options;

namespace MautoDesk.Infrastructure;

/// <summary>Where the buckets are and how to reach them.</summary>
public sealed class ObjectStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>The endpoint this process uses to talk to storage.</summary>
    /// <remarks>
    /// An internal name is correct here — <c>http://minio:9000</c> on a compose
    /// network. Nothing a browser sees comes from this setting.
    /// </remarks>
    public string ServiceUrl { get; set; } = "http://localhost:9000";

    /// <summary>
    /// The endpoint presigned URLs are signed for, when it differs from <see cref="ServiceUrl"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A presigned URL is handed to a browser, so it has to carry a name the
    /// browser can reach — and the signature covers that name, so it must be
    /// decided at signing time rather than rewritten later.
    /// </para>
    /// <para>
    /// Splitting the two is what stops the API from having to reach its own
    /// public hostname. Signing for the public name while talking to storage
    /// directly means no hairpin through the edge proxy, and it is the only
    /// arrangement that works at all when the public name resolves to something
    /// the API cannot route to — a <c>.localhost</c> name, or a split-horizon
    /// DNS setup.
    /// </para>
    /// <para>
    /// Empty means "same as <see cref="ServiceUrl"/>", which is right when
    /// storage is directly reachable by both.
    /// </para>
    /// </remarks>
    public string PublicUrl { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Path-style addressing, which MinIO needs and R2 accepts.
    /// </summary>
    /// <remarks>
    /// Virtual-host style would require wildcard DNS for a local container,
    /// which is a lot of yak-shaving for a development dependency.
    /// </remarks>
    public bool ForcePathStyle { get; set; } = true;

    public string QuarantineBucket { get; set; } = "mautodesk-uploads";

    public string MediaBucket { get; set; } = "mautodesk-media";

    public string DocumentsBucket { get; set; } = "mautodesk-docs";

    public string VaultBucket { get; set; } = "mautodesk-vault";
}

/// <summary>
/// The S3-compatible object store.
/// </summary>
/// <remarks>
/// One implementation for MinIO and R2 (ADR-0005). Nothing above this class
/// knows which is answering, which is what makes the provider a configuration
/// decision rather than a rewrite.
/// </remarks>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly AmazonS3Client _s3;

    /// <summary>
    /// A second client that exists only to sign URLs for the public endpoint.
    /// </summary>
    /// <remarks>
    /// Presigning is local computation — no request is made — so this costs a
    /// configuration object and buys URLs that name a host the browser can
    /// reach while the API keeps talking to storage directly.
    /// </remarks>
    private readonly AmazonS3Client _signer;

    private readonly ObjectStorageOptions _options;

    public S3ObjectStore(IOptions<ObjectStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _s3 = CreateClient(_options, _options.ServiceUrl);

        _signer = string.IsNullOrWhiteSpace(_options.PublicUrl)
            || string.Equals(_options.PublicUrl, _options.ServiceUrl, StringComparison.OrdinalIgnoreCase)
                ? _s3
                : CreateClient(_options, _options.PublicUrl);
    }

    private static AmazonS3Client CreateClient(ObjectStorageOptions options, string serviceUrl) =>
        new(options.AccessKey,
            options.SecretKey,
            new AmazonS3Config
            {
                ServiceURL = serviceUrl,
                ForcePathStyle = options.ForcePathStyle,
                UseHttp = UsesPlainHttp(serviceUrl),
                // R2 ignores the region but the SDK insists on one being set.
                AuthenticationRegion = "auto",
            });

    public Task<Uri> CreateUploadUrlAsync(
        StorageBucket bucket,
        string key,
        string contentType,
        long byteSize,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Resolve(bucket),
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(lifetime),
            ContentType = contentType,

            // Presigned URLs default to https no matter what ServiceURL says,
            // which hands a local MinIO a URL nothing can connect to. R2 is
            // https and stays https; this only follows the configured endpoint.
            Protocol = UsesPlainHttp(SigningUrl) ? Protocol.HTTP : Protocol.HTTPS,
        };

        // Signed into the URL, so a client that received a URL for a 2 MB JPEG
        // cannot use it to upload a 2 GB anything. The server re-verifies both
        // after the fact regardless — this only stops the waste.
        request.Headers.ContentLength = byteSize;

        return Task.FromResult(new Uri(_signer.GetPreSignedURL(request)));
    }

    public Task<Uri> CreateDownloadUrlAsync(
        StorageBucket bucket,
        string key,
        TimeSpan lifetime,
        CancellationToken cancellationToken) =>
        Task.FromResult(new Uri(_signer.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = Resolve(bucket),
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = UsesPlainHttp(SigningUrl) ? Protocol.HTTP : Protocol.HTTPS,
        })));

    public async Task<StoredObject?> StatAsync(
        StorageBucket bucket,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _s3.GetObjectMetadataAsync(Resolve(bucket), key, cancellationToken)
                .ConfigureAwait(false);

            return new StoredObject(
                metadata.ContentLength,
                metadata.Headers.ContentType,
                metadata.LastModified);
        }
        catch (AmazonS3Exception failure) when (failure.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // "Never uploaded" is an ordinary outcome of the confirm step, not
            // an error: the client may simply have abandoned the upload.
            return null;
        }
    }

    public async Task<Stream> OpenAsync(
        StorageBucket bucket,
        string key,
        CancellationToken cancellationToken)
    {
        var response = await _s3.GetObjectAsync(Resolve(bucket), key, cancellationToken)
            .ConfigureAwait(false);

        return response.ResponseStream;
    }

    public Task PutAsync(
        StorageBucket bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken) =>
        _s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Resolve(bucket),
                Key = key,
                InputStream = content,
                ContentType = contentType,
                // The SDK would otherwise buffer the whole object to compute a
                // checksum, which for a 20 MB photo is 20 MB of avoidable heap.
                AutoCloseStream = false,
            },
            cancellationToken);

    public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken) =>
        _s3.DeleteObjectAsync(Resolve(bucket), key, cancellationToken);

    /// <summary>The endpoint presigned URLs name, which may be the internal one.</summary>
    private string SigningUrl =>
        string.IsNullOrWhiteSpace(_options.PublicUrl) ? _options.ServiceUrl : _options.PublicUrl;

    public void Dispose()
    {
        if (!ReferenceEquals(_signer, _s3))
        {
            _signer.Dispose();
        }

        _s3.Dispose();
    }

    /// <summary>True for a plain-http endpoint, which in practice means local MinIO.</summary>
    private static bool UsesPlainHttp(string serviceUrl) =>
        serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private string Resolve(StorageBucket bucket) => bucket switch
    {
        StorageBucket.Quarantine => _options.QuarantineBucket,
        StorageBucket.Media => _options.MediaBucket,
        StorageBucket.Documents => _options.DocumentsBucket,
        StorageBucket.Vault => _options.VaultBucket,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
    };
}
