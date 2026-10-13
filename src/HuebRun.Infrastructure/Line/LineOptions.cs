namespace HuebRun.Infrastructure.Line;

/// config `Line` — ความลับทั้งหมดมาจาก user-secrets หรือ env var เท่านั้น; ไม่ตั้ง = ยังไม่ส่ง LINE (ข้อความค้างในคิว)
public sealed class LineOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.line.me";
    /// Messaging API channel: ใช้ push ข้อความ
    public string? ChannelAccessToken { get; set; }
    /// Messaging API channel secret: ตรวจ signature ของ webhook
    public string? ChannelSecret { get; set; }
    /// LINE Login channel ID: ตรวจ ID token ที่นักวิ่งได้จาก LIFF ตอนผูก LINE กับออเดอร์
    public string? LoginChannelId { get; set; }
    /// ลิงก์ตั๋วในข้อความ order_paid: {base}/{orderId} (เว้นว่าง = ไม่ใส่ลิงก์)
    public string? TicketUrlBase { get; set; }

    public bool CanPush => !string.IsNullOrWhiteSpace(ChannelAccessToken);
}
