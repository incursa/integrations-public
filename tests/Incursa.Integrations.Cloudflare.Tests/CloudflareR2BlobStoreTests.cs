using Amazon.S3;
using Amazon.S3.Model;
using Incursa.Integrations.Cloudflare.Storage;
using NSubstitute;

namespace Incursa.Integrations.Cloudflare.Tests;

public sealed class CloudflareR2BlobStoreTests
{
    [Fact]
    public async Task PutAsync_LargeSeekableStream_UsesBoundedMultipartUpload()
    {
        IAmazonS3 client = Substitute.For<IAmazonS3>();
        client.InitiateMultipartUploadAsync(Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new InitiateMultipartUploadResponse { UploadId = "upload-1" });
        client.UploadPartAsync(Arg.Any<UploadPartRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new UploadPartResponse { ETag = $"etag-{call.Arg<UploadPartRequest>().PartNumber}" });
        client.CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CompleteMultipartUploadResponse());
        CloudflareR2BlobStore sut = new(client, "bucket");
        await using var content = new MemoryStream(new byte[17 * 1024 * 1024], writable: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await sut.PutAsync("large.deb", content, "application/vnd.debian.binary-package", cancellationToken);

        await client.DidNotReceiveWithAnyArgs().PutObjectAsync(default!, cancellationToken);
        List<UploadPartRequest> parts = client.ReceivedCalls()
            .Select(static call => call.GetArguments()[0])
            .OfType<UploadPartRequest>()
            .OrderBy(static request => request.PartNumber)
            .ToList();
        Assert.Equal(4, parts.Count);
        Assert.Equal(new long?[] { 5 * 1024 * 1024, 5 * 1024 * 1024, 5 * 1024 * 1024, 2 * 1024 * 1024 }, parts.Select(static request => request.PartSize));
        Assert.All(parts, static request =>
        {
            Assert.True(request.DisablePayloadSigning);
            Assert.False(request.UseChunkEncoding);
        });
        await client.Received(1).CompleteMultipartUploadAsync(
            Arg.Is<CompleteMultipartUploadRequest>(request =>
                request.BucketName == "bucket"
                && request.Key == "large.deb"
                && request.UploadId == "upload-1"
                && request.PartETags.Count == 4),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutAsync_SmallSeekableStream_UsesSingleUnsignedUploadWithLength()
    {
        IAmazonS3 client = Substitute.For<IAmazonS3>();
        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse());
        CloudflareR2BlobStore sut = new(client, "bucket");
        await using var content = new MemoryStream(new byte[1024], writable: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await sut.PutAsync("small.bin", content, "application/octet-stream", cancellationToken);

        PutObjectRequest request = Assert.Single(client.ReceivedCalls()
            .Select(static call => call.GetArguments()[0])
            .OfType<PutObjectRequest>());
        Assert.Equal("bucket", request.BucketName);
        Assert.Equal("small.bin", request.Key);
        Assert.Equal(1024, request.Headers.ContentLength);
        Assert.True(request.DisablePayloadSigning);
        Assert.False(request.UseChunkEncoding);
        await client.DidNotReceiveWithAnyArgs().InitiateMultipartUploadAsync(default!, cancellationToken);
    }

    [Fact]
    public async Task ListAsync_NullObjectCollection_ReturnsEmpty()
    {
        IAmazonS3 client = Substitute.For<IAmazonS3>();
        client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response { S3Objects = null! });
        CloudflareR2BlobStore sut = new(client, "bucket");

        List<CloudflareBlobObjectInfo> results = await CollectAsync(
            sut.ListAsync("prefix/", TestContext.Current.CancellationToken));

        Assert.Empty(results);
    }

    [Fact]
    public async Task ListAsync_EmptyObjectCollection_ReturnsEmpty()
    {
        IAmazonS3 client = Substitute.For<IAmazonS3>();
        client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response { S3Objects = new List<S3Object>() });
        CloudflareR2BlobStore sut = new(client, "bucket");

        List<CloudflareBlobObjectInfo> results = await CollectAsync(
            sut.ListAsync("prefix/", TestContext.Current.CancellationToken));

        Assert.Empty(results);
    }

    [Fact]
    public async Task ListAsync_PopulatedResult_MapsObjectMetadata()
    {
        DateTime lastModified = new(2026, 11, 14, 12, 34, 56, DateTimeKind.Utc);
        IAmazonS3 client = Substitute.For<IAmazonS3>();
        client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response
            {
                S3Objects =
                [
                    new S3Object { Key = "prefix/object.bin", Size = 42, LastModified = lastModified },
                ],
            });
        CloudflareR2BlobStore sut = new(client, "bucket");

        CloudflareBlobObjectInfo result = Assert.Single(await CollectAsync(
            sut.ListAsync("prefix/", TestContext.Current.CancellationToken)));

        Assert.Equal("prefix/object.bin", result.Key);
        Assert.Equal(42, result.Size);
        Assert.Null(result.ContentType);
        Assert.Equal(new DateTimeOffset(lastModified), result.LastModifiedUtc);
    }

    [Fact]
    public async Task ListAsync_TruncatedResult_UsesContinuationTokenAndReturnsAllPages()
    {
        IAmazonS3 client = Substitute.For<IAmazonS3>();
        client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(
                new ListObjectsV2Response
                {
                    IsTruncated = true,
                    NextContinuationToken = "page-2",
                    S3Objects = new List<S3Object> { new() { Key = "first", Size = 1 } },
                },
                new ListObjectsV2Response
                {
                    IsTruncated = false,
                    S3Objects = new List<S3Object> { new() { Key = "second", Size = 2 } },
                });
        CloudflareR2BlobStore sut = new(client, "bucket");

        List<CloudflareBlobObjectInfo> results = await CollectAsync(
            sut.ListAsync("prefix/", TestContext.Current.CancellationToken));

        Assert.Equal(new[] { "first", "second" }, results.Select(static item => item.Key));
        List<ListObjectsV2Request> requests = client.ReceivedCalls()
            .Select(static call => call.GetArguments()[0])
            .OfType<ListObjectsV2Request>()
            .ToList();
        Assert.Collection(
            requests,
            request =>
            {
                Assert.Equal("bucket", request.BucketName);
                Assert.Equal("prefix/", request.Prefix);
                Assert.Null(request.ContinuationToken);
            },
            request => Assert.Equal("page-2", request.ContinuationToken));
    }

    private static async Task<List<CloudflareBlobObjectInfo>> CollectAsync(
        IAsyncEnumerable<CloudflareBlobObjectInfo> source)
    {
        List<CloudflareBlobObjectInfo> results = new();
        await foreach (CloudflareBlobObjectInfo item in source)
        {
            results.Add(item);
        }

        return results;
    }
}
