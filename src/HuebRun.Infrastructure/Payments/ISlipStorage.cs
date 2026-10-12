using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace HuebRun.Infrastructure.Payments;

/// ที่เก็บรูปสลิป (ข้อมูลการเงินส่วนบุคคล — ห้ามเปิด public)
public interface ISlipStorage
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);
    Task<byte[]> GetAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    /// ลิงก์ดูรูปอายุสั้น (bucket เป็น private เสมอ)
    Task<string> GetPresignedUrlAsync(string key, TimeSpan ttl, CancellationToken ct);
}

/// S3 API ใดก็ได้: MinIO ของบริษัท (path-style) หรือ Railway Bucket/Tigris (virtual-hosted, region "auto")
public sealed class S3SlipStorage(IAmazonS3 s3, IOptions<MinioOptions> options) : ISlipStorage
{
    private string Bucket => options.Value.Bucket;

    public static IAmazonS3 CreateClient(MinioOptions o, HttpClientFactory? httpClientFactory = null)
    {
        // ยอมทั้ง "host:port" และ URL เต็ม (ค่า ENDPOINT ของ Railway มี https:// มาด้วย)
        var endpoint = o.Endpoint.Contains("://") ? o.Endpoint : (o.UseSsl ? "https://" : "http://") + o.Endpoint;
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = o.ForcePathStyle,
            AuthenticationRegion = o.Region,
            // ผู้ให้บริการ S3-compatible หลายเจ้าไม่รองรับ checksum แบบใหม่ของ SDK v4
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (httpClientFactory is not null) config.HttpClientFactory = httpClientFactory;
        return new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), config);
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        using var stream = new MemoryStream(content);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key, InputStream = stream, ContentType = contentType,
        }, ct);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken ct)
    {
        using var response = await s3.GetObjectAsync(Bucket, key, ct);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public Task DeleteAsync(string key, CancellationToken ct) => s3.DeleteObjectAsync(Bucket, key, ct);

    public Task<string> GetPresignedUrlAsync(string key, TimeSpan ttl, CancellationToken ct) =>
        s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Bucket, Key = key, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.Add(ttl),
            Protocol = options.Value.UseSsl ? Protocol.HTTPS : Protocol.HTTP,
        });
}
