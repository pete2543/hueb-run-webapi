namespace HuebRun.Domain;

/// ข้อผิดพลาดที่ตั้งใจตอบกลับไปหาผู้เรียก (ข้อความต้องปลอดภัยที่จะแสดง) Api แปลงเป็น error body กลาง
public sealed class AppException(int statusCode, string code, string message, object? details = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public object? Details { get; } = details;
}
