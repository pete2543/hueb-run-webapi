using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Amazon.S3.Model;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;
using Testcontainers.Minio;

namespace HuebRun.Tests.Data;

/// เก็บสลิปผ่าน S3 API: MinIO จริงใน container (path-style) + ตรวจรูปแบบ URL ของ Railway/Tigris (virtual-hosted)
public sealed class S3SlipStorageTests : IAsyncLifetime
{
    private readonly MinioContainer _minio = new MinioBuilder(
        // minio/minio ถูกถอดจาก Docker Hub (quay.io ต้อง login) → ใช้ build ของ Chainguard ตรึงด้วย digest
        "cgr.dev/chainguard/minio@sha256:dc455cdd4db6e390b1b26514f15ff37681a777bf8dab41aac445a0259f751e56").Build();
    private S3SlipStorage _storage = null!;

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();
        var options = new MinioOptions
        {
            Endpoint = new Uri(_minio.GetConnectionString()).Authority,   // ไม่มี scheme → ใช้ UseSsl
            UseSsl = false, ForcePathStyle = true,
            AccessKey = _minio.GetAccessKey(), SecretKey = _minio.GetSecretKey(), Bucket = "slips-test",
        };
        var client = S3SlipStorage.CreateClient(options);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = "slips-test" });
        _storage = new S3SlipStorage(client, Microsoft.Extensions.Options.Options.Create(options));
    }

    public Task DisposeAsync() => _minio.DisposeAsync().AsTask();

    [Fact]
    public async Task Put_get_delete_round_trip()
    {
        var bytes = Slips.Png(7);
        await _storage.PutAsync("slips/1/abc/1.png", bytes, "image/png", default);
        Assert.Equal(bytes, await _storage.GetAsync("slips/1/abc/1.png", default));

        await _storage.DeleteAsync("slips/1/abc/1.png", default);
        await Assert.ThrowsAnyAsync<Exception>(() => _storage.GetAsync("slips/1/abc/1.png", default));
    }

    [Fact]
    public async Task Works_when_current_culture_uses_buddhist_calendar()
    {
        // th-TH ใช้ปี พ.ศ. → SDK เคยเซ็นคำขอด้วยวันที่ 2569 แล้วโดน SignatureDoesNotMatch
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        var bytes = Slips.Png(8);
        await _storage.PutAsync("slips/1/abc/2.png", bytes, "image/png", default);
        Assert.Equal(bytes, await _storage.GetAsync("slips/1/abc/2.png", default));
        await _storage.DeleteAsync("slips/1/abc/2.png", default);
        Assert.Equal("th-TH", CultureInfo.CurrentCulture.Name);
    }
}

public class S3SlipStorageAddressingTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler);
    }

    [Fact]
    public async Task Railway_bucket_uses_virtual_hosted_url_from_full_endpoint()
    {
        var capture = new Capture();
        var options = new MinioOptions
        {
            Endpoint = "https://t3.storageapi.dev", Region = "auto", ForcePathStyle = false,
            AccessKey = "k", SecretKey = "s", Bucket = "lightweight-tin-x1eceftla",
        };
        var client = S3SlipStorage.CreateClient(options, new Factory(capture));

        await new S3SlipStorage(client, Microsoft.Extensions.Options.Options.Create(options))
            .PutAsync("slips/1/a/1.png", Slips.Png(1), "image/png", default);

        Assert.Equal("https://lightweight-tin-x1eceftla.t3.storageapi.dev/slips/1/a/1.png", capture.Uri!.GetLeftPart(UriPartial.Path));
    }
}
