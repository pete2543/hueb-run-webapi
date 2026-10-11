using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Testcontainers.Minio;

namespace HuebRun.Tests.Data;

public sealed class MinioSlipStorageTests : IAsyncLifetime
{
    private readonly MinioContainer _minio = new MinioBuilder("minio/minio:RELEASE.2023-01-31T02-24-19Z").Build();
    private MinioSlipStorage _storage = null!;

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();
        var client = new MinioClient()
            .WithEndpoint(new Uri(_minio.GetConnectionString()).Authority)
            .WithCredentials(_minio.GetAccessKey(), _minio.GetSecretKey())
            .WithSSL(false)
            .Build();
        await client.MakeBucketAsync(new MakeBucketArgs().WithBucket("slips-test"));
        _storage = new MinioSlipStorage(client, Options.Create(new MinioOptions { Bucket = "slips-test" }));
    }

    public Task DisposeAsync() => _minio.DisposeAsync().AsTask();

    // image ทางการ minio/minio ถูกถอดจาก Docker Hub (quay.io ต้อง login) — รอเลือก image/วิธีทดสอบใหม่
    [Fact(Skip = "minio/minio image no longer available on Docker Hub; pending decision on replacement")]
    public async Task Put_get_delete_round_trip()
    {
        var bytes = Slips.Png(7);
        await _storage.PutAsync("slips/1/abc/1.png", bytes, "image/png", default);
        Assert.Equal(bytes, await _storage.GetAsync("slips/1/abc/1.png", default));

        await _storage.DeleteAsync("slips/1/abc/1.png", default);
        await Assert.ThrowsAnyAsync<Exception>(() => _storage.GetAsync("slips/1/abc/1.png", default));
    }
}
