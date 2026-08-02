namespace MautoDesk.SharedKernel;

/// <summary>The buckets from ADR-0005, named rather than passed as strings.</summary>
/// <remarks>
/// Separate buckets, not folders: the quarantine bucket expires its contents on
/// a lifecycle rule and is never readable by anything but the promotion path,
/// which is only enforceable at the bucket level.
/// </remarks>
public enum StorageBucket
{
    /// <summary>Where every upload lands first. Private, short-lived.</summary>
    Quarantine,

    /// <summary>Processed photos, served through the CDN.</summary>
    Media,

    /// <summary>Generated documents. Private, signed URLs only.</summary>
    Documents,

    /// <summary>Signed PDFs. Immutable.</summary>
    Vault,
}

/// <summary>What the store knows about an object without downloading it.</summary>
public sealed record StoredObject(long ByteSize, string? ContentType, DateTimeOffset LastModified);

/// <summary>
/// Object storage: MinIO in development, Cloudflare R2 in deployment.
/// </summary>
/// <remarks>
/// The interface is deliberately small and free of SDK types, because the whole
/// point of ADR-0005's "S3-compatible" choice is that the provider is a
/// configuration detail. Nothing above this line knows which one is answering.
/// </remarks>
public interface IObjectStore
{
    /// <summary>
    /// A short-lived URL the client may PUT one object to.
    /// </summary>
    /// <remarks>
    /// Constrained by content type and length so the URL cannot be reused to
    /// upload something else. The presigned URL is a capability — it is handed
    /// out narrowly and expires quickly for that reason.
    /// </remarks>
    public Task<Uri> CreateUploadUrlAsync(
        StorageBucket bucket,
        string key,
        string contentType,
        long byteSize,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    /// <summary>A short-lived URL to read one object.</summary>
    public Task<Uri> CreateDownloadUrlAsync(
        StorageBucket bucket,
        string key,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    /// <summary>Metadata only. Null when the object is not there.</summary>
    public Task<StoredObject?> StatAsync(
        StorageBucket bucket,
        string key,
        CancellationToken cancellationToken);

    public Task<Stream> OpenAsync(StorageBucket bucket, string key, CancellationToken cancellationToken);

    public Task PutAsync(
        StorageBucket bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);

    /// <summary>Deletes an object. Succeeds when it was already gone.</summary>
    public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken);
}

/// <summary>The verdict on a scanned object.</summary>
public sealed record ScanResult(bool IsClean, string? Threat)
{
    public static ScanResult Clean() => new(true, null);

    public static ScanResult Infected(string threat) => new(false, threat);
}

/// <summary>
/// Malware scanning for anything a user uploaded.
/// </summary>
/// <remarks>
/// <b>Fail closed.</b> An implementation that cannot reach its scanner must
/// throw, not return clean — a scanner outage that silently promotes unscanned
/// files is worse than an upload that fails loudly, because nobody finds out
/// until it matters.
/// </remarks>
public interface IMalwareScanner
{
    public Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}
