using Amazon.S3;
using Amazon.S3.Model;
using MautoDesk.SharedKernel;
using Microsoft.Extensions.Options;

namespace MautoDesk.Infrastructure;

/// <summary>Where the buckets are and how to reach them.</summary>
public sealed class ObjectStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>The S3 endpoint. MinIO locally, R2 in deployment.</summary>
    public string ServiceUrl { get; set; } = "http://localhost:9000";

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
    private readonly ObjectStorageOptions _options;

    public S3ObjectStore(IOptions<ObjectStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;

        _s3 = new AmazonS3Client(
            _options.AccessKey,
            _options.SecretKey,
            new AmazonS3Config
            {
                ServiceURL = _options.ServiceUrl,
                ForcePathStyle = _options.ForcePathStyle,
                UseHttp = UsesPlainHttp(_options.ServiceUrl),
                // R2 ignores the region but the SDK insists on one being set.
                AuthenticationRegion = "auto",
            });
    }

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
            Protocol = UsesPlainHttp(_options.ServiceUrl) ? Protocol.HTTP : Protocol.HTTPS,
        };

        // Signed into the URL, so a client that received a URL for a 2 MB JPEG
        // cannot use it to upload a 2 GB anything. The server re-verifies both
        // after the fact regardless — this only stops the waste.
        request.Headers.ContentLength = byteSize;

        return Task.FromResult(new Uri(_s3.GetPreSignedURL(request)));
    }

    public Task<Uri> CreateDownloadUrlAsync(
        StorageBucket bucket,
        string key,
        TimeSpan lifetime,
        CancellationToken cancellationToken) =>
        Task.FromResult(new Uri(_s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = Resolve(bucket),
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = UsesPlainHttp(_options.ServiceUrl) ? Protocol.HTTP : Protocol.HTTPS,
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

    public void Dispose() => _s3.Dispose();

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
