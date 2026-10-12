using HuebRun.Domain;

namespace HuebRun.Infrastructure.Admin;

public static class AdminErrors
{
    public static AppException InvalidCredentials() =>
        new(401, "INVALID_CREDENTIALS", "อีเมลหรือรหัสผ่านไม่ถูกต้อง หรือบัญชีถูกล็อกชั่วคราว");

    public static AppException UserNotFound(Guid id) =>
        new(404, "USER_NOT_FOUND", "ไม่พบผู้ใช้", new { id });

    public static AppException EmailTaken() =>
        new(409, "EMAIL_TAKEN", "อีเมลนี้ถูกใช้แล้ว");

    public static AppException WeakPassword(IEnumerable<string> reasons) =>
        new(400, "WEAK_PASSWORD", "รหัสผ่านไม่ผ่านเกณฑ์", new { reasons });

    public static AppException Validation(string field, string message) =>
        new(400, "VALIDATION_ERROR", "ข้อมูลไม่ถูกต้อง", new Dictionary<string, string[]> { [field] = [message] });

    public static AppException CannotChangeSelf() =>
        new(409, "CANNOT_CHANGE_SELF", "ปิดบัญชีหรือเปลี่ยนบทบาทของตัวเองไม่ได้");

    public static AppException LastSuperAdmin() =>
        new(409, "LAST_SUPER_ADMIN", "ต้องเหลือ SuperAdmin ที่ใช้งานได้อย่างน้อย 1 คน");

    public static AppException WrongPassword() =>
        new(400, "WRONG_PASSWORD", "รหัสผ่านปัจจุบันไม่ถูกต้อง");

    public static AppException Forbidden() =>
        new(403, "FORBIDDEN", "ไม่มีสิทธิ์ทำรายการนี้");

    public static AppException PaymentNotFound(int id) =>
        new(404, "PAYMENT_NOT_FOUND", "ไม่พบรายการชำระเงิน", new { id });

    public static AppException PaymentNotReviewable(PaymentStatus status) =>
        new(409, "PAYMENT_NOT_REVIEWABLE", "รายการนี้ไม่ได้อยู่ในสถานะรอตรวจ (มีคนจัดการไปแล้ว?)", new { status = status.ToString() });

    public static AppException ApproveBlocked(string reason) =>
        new(409, "APPROVE_NOT_ALLOWED", "อนุมัติรายการนี้ไม่ได้", new { reason });

    public static AppException CapacityFull(string what) =>
        new(409, "CAPACITY_FULL", $"{what}เต็มแล้ว จองใหม่ให้ออเดอร์ที่หมดเวลาไม่ได้ — ต้องคืนเงิน", new { what });

    public static AppException NoSlip(int id) =>
        new(404, "SLIP_NOT_FOUND", "รายการนี้ไม่มีรูปสลิป", new { id });

    public static AppException OrderNotFound(Guid id) =>
        new(404, "ORDER_NOT_FOUND", "ไม่พบใบสมัคร", new { id });

    public static AppException EventNotFound(int id) =>
        new(404, "EVENT_NOT_FOUND", "ไม่พบงานนี้", new { id });

    public static AppException SlugTaken(string slug) =>
        new(409, "SLUG_TAKEN", "slug นี้ถูกใช้แล้ว", new { slug });

    public static AppException Duplicate(string field) =>
        new(409, "DUPLICATE", "ชื่อ/รหัสซ้ำกับรายการที่มีอยู่", new { field });

    public static AppException InUse(string kind, string name) =>
        new(409, "IN_USE", "มีผู้สมัคร/ออเดอร์อ้างถึงอยู่ ลบไม่ได้", new { kind, name });

    public static AppException QuotaBelowReserved(string name, int newLimit, int reserved) =>
        new(409, "QUOTA_BELOW_RESERVED", "ลดต่ำกว่าจำนวนที่จองไว้แล้วไม่ได้", new { name, newLimit, reserved });

    public static AppException BibPrefixLocked(string name) =>
        new(409, "BIB_PREFIX_LOCKED", "ออกเลขบิบไปแล้ว เปลี่ยนตัวอักษรนำหน้าบิบไม่ได้", new { name });

    public static AppException NotReadyToPublish(IEnumerable<string> missing) =>
        new(409, "NOT_READY_TO_PUBLISH", "ตั้งค่างานยังไม่ครบ ต้องมีระยะ ไซซ์เสื้อ และวิธีรับของที่เปิดใช้อย่างน้อยอย่างละ 1", new { missing });
}
