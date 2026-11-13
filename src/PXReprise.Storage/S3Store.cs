using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;

namespace PXReprise.Storage;

/// <summary>A store that is a prefix in an S3 bucket.</summary>
/// <remarks>
/// <para>Write-once is S3's conditional write: <c>If-None-Match: *</c> on PutObject and on CompleteMultipartUpload, so
/// S3 itself refuses a second writer. Every object carries its sha256 twice: as S3's own checksum (S3 checks the bytes
/// it received against it) and as <c>x-amz-meta-sha256</c>, which is what a later writer of the same key is compared
/// with. A multipart object's S3 checksum is a checksum of parts, so the metadata is the one that names the whole
/// file.</para>
/// <para>Credentials come from the SDK's default chain (for the lab: <c>AWS_PROFILE</c> naming an SSO profile). The
/// bucket name is never written into code, docs or a committed machine file's comments; it comes from the machine
/// file or the operator's own environment.</para>
/// </remarks>
public sealed class S3Store : IStore
{
    /// <summary>Objects above this are uploaded in parts (a mouse library is about 1.2 GB).</summary>
    internal static long MultipartThreshold { get; set; } = 256L * 1024 * 1024;

    internal static long PartSize { get; set; } = 64L * 1024 * 1024;

    private const string ShaMetadata = "sha256";

    private readonly IAmazonS3 _client;
    private readonly bool _ownsClient;

    public string Bucket { get; }

    /// <summary>The key prefix, empty or ending in '/'.</summary>
    public string Prefix { get; }

    public S3Store(IAmazonS3 client, string bucket, string prefix, bool ownsClient = false)
    {
        _client = client;
        _ownsClient = ownsClient;
        Bucket = bucket;
        prefix = prefix.Trim('/');
        Prefix = prefix.Length == 0 ? "" : StoreKey.CheckPrefix(prefix + "/");
    }

    /// <summary>Opens <c>s3://bucket[/prefix]</c> with a client from the default credential chain and region.</summary>
    public static S3Store Open(string location)
    {
        var rest = location["s3://".Length..];
        var slash = rest.IndexOf('/');
        var bucket = slash < 0 ? rest : rest[..slash];
        var prefix = slash < 0 ? "" : rest[(slash + 1)..];
        if (bucket.Length == 0) throw new ArgumentException($"'{location}': no bucket (expected s3://bucket/prefix)", nameof(location));
        return new S3Store(new AmazonS3Client(), bucket, prefix, ownsClient: true);
    }

    public string Location => $"s3://{Bucket}/{Prefix}";

    private string Full(string key) => Prefix + StoreKey.Check(key);

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = Bucket, Key = Full(key) }, ct).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async IAsyncEnumerable<StoreEntry> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        StoreKey.CheckPrefix(prefix);
        var request = new ListObjectsV2Request { BucketName = Bucket, Prefix = Prefix + prefix };
        do
        {
            var page = await _client.ListObjectsV2Async(request, ct).ConfigureAwait(false);
            foreach (var o in page.S3Objects ?? [])
                yield return new StoreEntry(o.Key[Prefix.Length..], o.Size ?? 0);
            request.ContinuationToken = page.NextContinuationToken;
            if (page.IsTruncated != true) break;
        } while (true);
    }

    public async Task DownloadAsync(string key, string localPath, CancellationToken ct = default)
    {
        using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = Full(key) }, ct).ConfigureAwait(false);
        await using var target = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write);
        await response.ResponseStream.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    public async Task<WriteOutcome> WriteOnceAsync(string key, string localPath, string sha256, CancellationToken ct = default)
    {
        var full = Full(key);
        try
        {
            if (new FileInfo(localPath).Length <= MultipartThreshold) await PutAsync(full, localPath, sha256, ct).ConfigureAwait(false);
            else await PutInPartsAsync(full, localPath, sha256, ct).ConfigureAwait(false);
            return WriteOutcome.Written;
        }
        catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            // 412: the key exists. 409: another conditional write to it was in flight; it exists now or will.
            return await CompareAsync(key, full, sha256, ct).ConfigureAwait(false);
        }
    }

    private async Task PutAsync(string full, string localPath, string sha256, CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            BucketName = Bucket,
            Key = full,
            FilePath = localPath,
            IfNoneMatch = "*",
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(sha256)),
        };
        request.Metadata.Add(ShaMetadata, sha256);
        await _client.PutObjectAsync(request, ct).ConfigureAwait(false);
    }

    private async Task PutInPartsAsync(string full, string localPath, string sha256, CancellationToken ct)
    {
        var start = new InitiateMultipartUploadRequest { BucketName = Bucket, Key = full, ChecksumAlgorithm = ChecksumAlgorithm.SHA256 };
        start.Metadata.Add(ShaMetadata, sha256);
        var upload = await _client.InitiateMultipartUploadAsync(start, ct).ConfigureAwait(false);
        try
        {
            var parts = new List<PartETag>();
            var length = new FileInfo(localPath).Length;
            for (int number = 1; (long)(number - 1) * PartSize < length; number++)
            {
                var offset = (number - 1) * PartSize;
                var part = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = Bucket,
                    Key = full,
                    UploadId = upload.UploadId,
                    PartNumber = number,
                    FilePath = localPath,
                    FilePosition = offset,
                    PartSize = Math.Min(PartSize, length - offset),
                    ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                }, ct).ConfigureAwait(false);
                parts.Add(new PartETag { PartNumber = number, ETag = part.ETag, ChecksumSHA256 = part.ChecksumSHA256 });
            }
            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = full,
                UploadId = upload.UploadId,
                PartETags = parts,
                IfNoneMatch = "*",
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = Bucket, Key = full, UploadId = upload.UploadId }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<WriteOutcome> CompareAsync(string key, string full, string sha256, CancellationToken ct)
    {
        var meta = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = Bucket, Key = full }, ct).ConfigureAwait(false);
        var existing = meta.Metadata["x-amz-meta-" + ShaMetadata];
        if (string.IsNullOrEmpty(existing))
        {
            // Written by something other than this class (no sha256 recorded): hash the bytes themselves.
            var temp = Path.Combine(Path.GetTempPath(), "pxreprise-compare-" + Guid.NewGuid().ToString("N"));
            try
            {
                await DownloadAsync(key, temp, ct).ConfigureAwait(false);
                existing = Stores.Sha256File(temp);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        if (existing == sha256) return WriteOutcome.AlreadyPresent;
        throw new StoreConflictException($"{Location}: {key} already exists with sha256 {existing}, not {sha256}; nothing in a store is replaced");
    }

    public string? LocalFile(string key) => null;

    public async Task ReplaceAsync(string key, byte[] content, CancellationToken ct = default)
    {
        using var body = new MemoryStream(content);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = Full(key),
            InputStream = body,
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
        }, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
