using HuebRun.Domain;

namespace HuebRun.Infrastructure.Line;

public static class LineErrors
{
    public static AppException InvalidToken() =>
        new(400, "INVALID_LINE_TOKEN", "ยืนยันตัวตนกับ LINE ไม่สำเร็จ กรุณาเข้าสู่ระบบ LINE ใหม่");

    public static AppException AlreadyLinked() =>
        new(409, "LINE_ALREADY_LINKED", "ออเดอร์นี้ผูกกับบัญชี LINE อื่นแล้ว");

    public static AppException OrderClosed() =>
        new(409, "ORDER_CLOSED", "ออเดอร์นี้หมดเวลา/ถูกยกเลิกแล้ว ผูก LINE ไม่ได้");
}
