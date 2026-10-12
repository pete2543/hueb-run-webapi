using HuebRun.Domain;

namespace HuebRun.Infrastructure.Payments;

/// โหมดจำลอง (Payments:Mode = Fake, Development เท่านั้น): ชื่อไฟล์สลิปเลือกผลตรวจ ให้หน้าบ้านลองทุกสถานะโดยไม่ต้องโอนจริง
public sealed class DevSlipVerifier : ISlipVerifier
{
    public Task<SlipCheckResult> VerifyAsync(SlipImage image, decimal expectedAmount, CancellationToken ct)
    {
        var name = image.OriginalName?.ToLowerInvariant() ?? "";
        SlipCheckResult result =
            name.Contains("amount") ? new SlipCheckResult.Rejected(PaymentRejectReason.AmountMismatch, "{}")
            : name.Contains("receiver") ? new SlipCheckResult.Rejected(PaymentRejectReason.WrongReceiver, "{}")
            : name.Contains("invalid") ? new SlipCheckResult.Rejected(PaymentRejectReason.InvalidSlip, "{}")
            : name.Contains("delay") ? new SlipCheckResult.Retry(TimeSpan.FromMinutes(1), "{}")   // ตรวจซ้ำไม่มีชื่อไฟล์ → ผ่าน
            : name.Contains("review") ? new SlipCheckResult.Duplicate(null, "{}")
            : new SlipCheckResult.Verified("DEV-" + Guid.NewGuid().ToString("N"), expectedAmount, DateTime.UtcNow, "{}");
        return Task.FromResult(result);
    }
}

/// เก็บสลิปเป็นไฟล์ในเครื่อง แทน MinIO ในโหมดจำลอง (key สร้างโดยระบบเอง ไม่ได้มาจากผู้ใช้)
public sealed class LocalSlipStorage(string root) : ISlipStorage
{
    private string PathOf(string key) => Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, ct);
    }

    public Task<byte[]> GetAsync(string key, CancellationToken ct) => File.ReadAllBytesAsync(PathOf(key), ct);

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }

    // dev เท่านั้น: ไม่มี URL ให้เซ็น ส่งรูปเป็น data URL แทน
    public async Task<string> GetPresignedUrlAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        var bytes = await GetAsync(key, ct);
        var type = Path.GetExtension(key).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg",
        };
        return $"data:{type};base64,{Convert.ToBase64String(bytes)}";
    }
}
