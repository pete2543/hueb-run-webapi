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
}
