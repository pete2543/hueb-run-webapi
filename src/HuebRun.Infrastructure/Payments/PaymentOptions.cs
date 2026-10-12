using System.ComponentModel.DataAnnotations;

namespace HuebRun.Infrastructure.Payments;

/// config `Payments`
public sealed class PaymentOptions
{
    public const string FakeMode = "Fake";

    /// "Live" = SlipOK + MinIO; "Fake" = ตรวจสลิปจำลอง + เก็บไฟล์ในเครื่อง (Development เท่านั้น ไม่ต้องใช้ key)
    public string Mode { get; set; } = "Live";

    /// ยืดเวลาหมดอายุของออเดอร์เมื่อรับสลิป ให้ครอบช่วงตรวจซ้ำทั้งหมด
    [Range(1, 1440)] public int VerificationHoldMinutes { get; set; } = 90;
    /// สลิปไม่ผ่าน: เวลาที่เหลือให้อัปโหลดใหม่
    [Range(1, 1440)] public int RejectGraceMinutes { get; set; } = 10;
}
