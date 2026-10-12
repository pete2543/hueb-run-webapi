using System.Globalization;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace HuebRun.Infrastructure.Payments;

/// ที่เก็บรูปสลิป (ข้อมูลการเงินส่วนบุคคล — ห้ามเปิด public)
public interface ISlipStorage
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);
    Task<byte[]> GetAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed class MinioSlipStorage(IMinioClient client, IOptions<MinioOptions> options) : ISlipStorage
{
    private string Bucket => options.Value.Bucket;

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        UseInvariantCulture();
        using var stream = new MemoryStream(content);
        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(Bucket).WithObject(key)
            .WithStreamData(stream).WithObjectSize(content.Length)
            .WithContentType(contentType), ct);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken ct)
    {
        UseInvariantCulture();
        using var buffer = new MemoryStream();
        await client.GetObjectAsync(new GetObjectArgs()
            .WithBucket(Bucket).WithObject(key)
            .WithCallbackStream((stream, token) => stream.CopyToAsync(buffer, token)), ct);
        return buffer.ToArray();
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        UseInvariantCulture();
        await client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(key), ct);
    }

    // SDK ลงวันที่ใน signature ตาม culture ปัจจุบัน: th-TH ได้ปี พ.ศ. → SignatureDoesNotMatch
    // ตั้งใน async method จึงมีผลแค่ใน method นี้ ไม่รั่วกลับไปหาคนเรียก
    private static void UseInvariantCulture() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
}
