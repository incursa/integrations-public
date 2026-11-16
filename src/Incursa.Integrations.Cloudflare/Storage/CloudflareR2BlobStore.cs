using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace Incursa.Integrations.Cloudflare.Storage;

public sealed class CloudflareR2BlobStore : ICloudflareR2BlobStore
{
    private const int MultipartThresholdBytes = 16 * 1024 * 1024;
    private const int MultipartPartBytes = 5 * 1024 * 1024;
    private const int MultipartParallelism = 12;

    private readonly IAmazonS3 client;
    private readonly string bucket;
    private readonly ILogger<CloudflareR2BlobStore>? logger;

    public CloudflareR2BlobStore(IAmazonS3 client, string bucket)
        : this(client, bucket, null)
    {
    }

    public CloudflareR2BlobStore(
        IAmazonS3 client,
        string bucket,
        ILogger<CloudflareR2BlobStore>? logger)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.bucket = string.IsNullOrWhiteSpace(bucket)
            ? throw new ArgumentException("Bucket is required.", nameof(bucket))
            : bucket.Trim();
        this.logger = logger;
    }

    public async ValueTask PutAsync(string key, Stream content, string? contentType = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);

        if (content.CanSeek && content.Length - content.Position >= MultipartThresholdBytes)
        {
            await PutMultipartAsync(key, content, contentType, cancellationToken).ConfigureAwait(false);
            return;
        }

        PutObjectRequest request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            ContentType = contentType,
            DisablePayloadSigning = true,
            UseChunkEncoding = false,
        };

        if (content.CanSeek)
        {
            request.Headers.ContentLength = content.Length - content.Position;
        }

        await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Stream?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.GetObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }

    public async ValueTask<Stream?> GetRangeAsync(string key, long startInclusive, long endInclusive, CancellationToken cancellationToken = default)
    {
        if (startInclusive < 0 || endInclusive < startInclusive)
        {
            return null;
        }

        try
        {
            GetObjectRequest request = new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ByteRange = new ByteRange(startInclusive, endInclusive),
            };
            var response = await client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }

    public async ValueTask<CloudflareBlobObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.GetObjectMetadataAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            return new CloudflareBlobObjectInfo(key, response.ContentLength, response.Headers.ContentType, response.LastModified);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }

    public async IAsyncEnumerable<CloudflareBlobObjectInfo> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix ?? string.Empty,
                ContinuationToken = continuationToken,
            }, cancellationToken).ConfigureAwait(false);

            foreach (var item in response.S3Objects ?? new List<S3Object>())
            {
                if (!string.IsNullOrWhiteSpace(item.Key))
                {
                    yield return new CloudflareBlobObjectInfo(item.Key, item.Size ?? 0, null, item.LastModified);
                }
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));
    }

    public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await client.DeleteObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
    }

    private async Task PutMultipartAsync(
        string key,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken)
    {
        var initiated = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ContentType = contentType,
        }, cancellationToken).ConfigureAwait(false);
        var uploadId = initiated.UploadId
            ?? throw new InvalidOperationException("R2 did not return a multipart upload ID.");

        try
        {
            var completedParts = new List<PartETag>();
            var partNumber = 1;
            while (content.Position < content.Length)
            {
                var batch = new List<Task<PartETag>>(MultipartParallelism);
                for (var batchIndex = 0;
                     batchIndex < MultipartParallelism && content.Position < content.Length;
                     batchIndex++)
                {
                    var bytesRemaining = content.Length - content.Position;
                    var buffer = GC.AllocateUninitializedArray<byte>((int)Math.Min(MultipartPartBytes, bytesRemaining));
                    await content.ReadExactlyAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    batch.Add(UploadPartAsync(key, uploadId, partNumber++, buffer, cancellationToken));
                }

                completedParts.AddRange(await Task.WhenAll(batch).ConfigureAwait(false));
            }

            await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = completedParts.OrderBy(static part => part.PartNumber).ToList(),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception uploadException)
        {
            try
            {
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception abortException)
            {
                throw new AggregateException("R2 multipart upload and cleanup both failed.", uploadException, abortException);
            }

            throw;
        }
    }

    private async Task<PartETag> UploadPartAsync(
        string key,
        string uploadId,
        int partNumber,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var partStream = new MemoryStream(buffer, writable: false);
        var response = await client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = partNumber,
            PartSize = buffer.Length,
            InputStream = partStream,
            DisablePayloadSigning = true,
            UseChunkEncoding = false,
        }, cancellationToken).ConfigureAwait(false);
        logger?.LogInformation(
            "Cloudflare R2 multipart stage completed stage=part_upload partNumber={PartNumber} bytes={Bytes} elapsedMs={ElapsedMs}",
            partNumber,
            buffer.Length,
            started.Elapsed.TotalMilliseconds);
        return new PartETag(partNumber, response.ETag);
    }
}
