using System.Globalization;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Testcontainers.Minio;

namespace HuebRun.Tests.Data;

public sealed class MinioSlipStorageTests : IAsyncLifetime
{
    private readonly MinioContainer _minio = new MinioBuilder(
        // minio/minio ถูกถอดจาก Docker Hub (quay.io ต้อง login) → ใช้ build ของ Chainguard ตรึงด้วย digest
        "cgr.dev/chainguard/minio@sha256:dc455cdd4db6e390b1b26514f15ff37681a777bf8dab41aac445a0259f751e56").Build();
    private MinioSlipStorage _storage = null!;

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();
        var client = new MinioClient()
            .WithEndpoint(new Uri(_minio.GetConnectionString()).Authority)
            .WithCredentials(_minio.GetAccessKey(), _minio.GetSecretKey())
            .WithSSL(false)
            .Build();
        // SDK ลงวันที่ตาม culture ปัจจุบัน — setup ใช้ invariant เสมอ (เครื่อง dev อาจเป็น th-TH)
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        await client.MakeBucketAsync(new MakeBucketArgs().WithBucket("slips-test"));
        _storage = new MinioSlipStorage(client, Options.Create(new MinioOptions { Bucket = "slips-test" }));
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
        Assert.Equal("th-TH", CultureInfo.CurrentCulture.Name);   // ไม่เปลี่ยน culture ของคนเรียก
    }
}
