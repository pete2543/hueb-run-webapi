using HuebRun.Domain;

namespace HuebRun.Infrastructure.Orders;

public static class OrderErrors
{
    public const string PayableIndexName = "IX_Orders_EventId_PayableAmount";

    public static AppException EventNotFound(string slug) =>
        new(404, "EVENT_NOT_FOUND", "ไม่พบงานนี้", new { slug });

    public static AppException RegistrationClosed() =>
        new(400, "REGISTRATION_CLOSED", "ไม่อยู่ในช่วงเปิดรับสมัคร");

    public static AppException Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(400, "VALIDATION_ERROR", "ข้อมูลไม่ถูกต้อง", errors);

    public static AppException Promo(PromoRejection reason) => reason == PromoRejection.Exhausted
        ? new(409, "PROMO_EXHAUSTED", "โค้ดส่วนลดถูกใช้ครบจำนวนแล้ว")
        : new(422, "PROMO_INVALID", "ใช้โค้ดส่วนลดนี้ไม่ได้", new { reason = ReasonCode(reason) });

    public static AppException QuotaFull(int distanceId, string name) =>
        new(409, "QUOTA_FULL", $"ระยะ {name} เต็มแล้ว", new { distanceId, name });

    public static AppException ShirtOutOfStock(int shirtSizeId, string size) =>
        new(409, "SHIRT_OUT_OF_STOCK", $"เสื้อไซซ์ {size} หมดแล้ว", new { shirtSizeId, size });

    public static AppException PaymentSlotBusy() =>
        new(409, "PAYMENT_SLOT_BUSY", "มีผู้สมัครยอดเดียวกันจำนวนมาก กรุณาลองใหม่อีกครั้ง");

    public static AppException OrderNotFound(Guid orderId) =>
        new(404, "ORDER_NOT_FOUND", "ไม่พบใบสมัคร", new { orderId });

    public static AppException OrderNotPaid(OrderStatus status) =>
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
