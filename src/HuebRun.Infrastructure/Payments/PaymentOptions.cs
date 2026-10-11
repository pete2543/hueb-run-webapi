using System.ComponentModel.DataAnnotations;

namespace HuebRun.Infrastructure.Payments;

/// config `Payments`
public sealed class PaymentOptions
{
    /// ยืดเวลาหมดอายุของออเดอร์เมื่อรับสลิป ให้ครอบช่วงตรวจซ้ำทั้งหมด
    [Range(1, 1440)] public int VerificationHoldMinutes { get; set; } = 90;
    /// สลิปไม่ผ่าน: เวลาที่เหลือให้อัปโหลดใหม่
    [Range(1, 1440)] public int RejectGraceMinutes { get; set; } = 10;
}
