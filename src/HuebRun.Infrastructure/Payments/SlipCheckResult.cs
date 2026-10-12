using System.Security.Cryptography;

namespace HuebRun.Infrastructure.Payments;

/// รูปสลิปที่ผ่านการตรวจชนิดไฟล์แล้ว (Extension/ContentType มาจาก magic bytes; OriginalName = ชื่อไฟล์ที่อัปโหลด ถ้ามี)
public sealed record SlipImage(byte[] Content, string Extension, string ContentType, string? OriginalName = null)
{
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Content));
}

/// ตัวตรวจสลิป — ไม่ผูกกับช่องทางรับสลิป (หน้าเว็บ/LINE ใช้ร่วมกันได้)
public interface ISlipVerifier
{
    Task<SlipCheckResult> VerifyAsync(SlipImage image, decimal expectedAmount, CancellationToken ct);
}

/// ผลตรวจสลิป RawJson = คำตอบดิบ (ต้องเป็น JSON ที่ถูกต้องหรือ null เพราะเก็บลง jsonb)
public abstract record SlipCheckResult(string? RawJson)
{
    public sealed record Verified(string TransRef, decimal Amount, DateTime? TransferredAt, string Raw) : SlipCheckResult(Raw);
    public sealed record Duplicate(string? TransRef, string Raw) : SlipCheckResult(Raw);
    public sealed record Rejected(string Reason, string Raw) : SlipCheckResult(Raw);
    public sealed record Retry(TimeSpan After, string Raw) : SlipCheckResult(Raw);
    public sealed record Unavailable(string? Raw) : SlipCheckResult(Raw);
}
