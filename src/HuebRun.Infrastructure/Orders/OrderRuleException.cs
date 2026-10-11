using HuebRun.Domain;

namespace HuebRun.Infrastructure.Orders;

/// ข้อผิดพลาดตามกติกาธุรกิจของการสมัคร (ข้อความปลอดภัยที่จะแสดงผู้ใช้) Api แปลงเป็น error body กลาง
public sealed class OrderRuleException(int statusCode, string code, string message, object? details = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

public static class OrderErrors
{
    public const string PayableIndexName = "IX_Orders_EventId_PayableAmount";

    public static OrderRuleException EventNotFound(string slug) =>
        new(404, "EVENT_NOT_FOUND", "ไม่พบงานนี้", new { slug });

    public static OrderRuleException RegistrationClosed() =>
        new(400, "REGISTRATION_CLOSED", "ไม่อยู่ในช่วงเปิดรับสมัคร");

    public static OrderRuleException Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(400, "VALIDATION_ERROR", "ข้อมูลไม่ถูกต้อง", errors);

    public static OrderRuleException Promo(PromoRejection reason) => reason == PromoRejection.Exhausted
        ? new(409, "PROMO_EXHAUSTED", "โค้ดส่วนลดถูกใช้ครบจำนวนแล้ว")
        : new(422, "PROMO_INVALID", "ใช้โค้ดส่วนลดนี้ไม่ได้", new { reason = ReasonCode(reason) });

    public static OrderRuleException QuotaFull(int distanceId, string name) =>
        new(409, "QUOTA_FULL", $"ระยะ {name} เต็มแล้ว", new { distanceId, name });

    public static OrderRuleException ShirtOutOfStock(int shirtSizeId, string size) =>
        new(409, "SHIRT_OUT_OF_STOCK", $"เสื้อไซซ์ {size} หมดแล้ว", new { shirtSizeId, size });

    public static OrderRuleException PaymentSlotBusy() =>
        new(409, "PAYMENT_SLOT_BUSY", "มีผู้สมัครยอดเดียวกันจำนวนมาก กรุณาลองใหม่อีกครั้ง");

    public static OrderRuleException OrderNotFound(Guid orderId) =>
        new(404, "ORDER_NOT_FOUND", "ไม่พบใบสมัคร", new { orderId });

    public static OrderRuleException OrderNotPaid(OrderStatus status) =>
        new(409, "ORDER_NOT_PAID", "ใบสมัครยังไม่ได้ชำระเงิน", new { status = status.ToString() });

    private static string ReasonCode(PromoRejection reason) => reason switch
    {
        PromoRejection.NotFound => "NOT_FOUND",
        PromoRejection.Inactive => "INACTIVE",
        PromoRejection.NotStarted => "NOT_STARTED",
        PromoRejection.Expired => "EXPIRED",
        PromoRejection.MinOrderNotMet => "MIN_ORDER_NOT_MET",
        _ => "EXHAUSTED",
    };
}
