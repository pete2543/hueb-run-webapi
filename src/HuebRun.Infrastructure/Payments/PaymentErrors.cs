using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;

namespace HuebRun.Infrastructure.Payments;

public static class PaymentErrors
{
    public static AppException InvalidFile(string message) =>
        OrderErrors.Validation(new Dictionary<string, string[]> { ["file"] = [message] });

    public static AppException OrderAlreadyPaid() =>
        new(409, "ORDER_ALREADY_PAID", "ใบสมัครนี้ชำระเงินแล้ว");

    public static AppException OrderNotPayable(OrderStatus status) =>
        new(409, "ORDER_NOT_PAYABLE", "ใบสมัครนี้ไม่อยู่ในสถานะที่ชำระเงินได้", new { status = status.ToString() });

    public static AppException PaymentInProgress() =>
        new(409, "PAYMENT_IN_PROGRESS", "กำลังตรวจสอบสลิปก่อนหน้า กรุณารอสักครู่");

    public static AppException SlipDuplicate() =>
        new(409, "SLIP_DUPLICATE", "สลิปนี้ถูกส่งเข้ามาแล้ว");

    public static AppException SlipRejected(int paymentId, string reason) =>
        new(422, "SLIP_REJECTED", "สลิปไม่ผ่านการตรวจสอบ", new { paymentId, reason });
}
