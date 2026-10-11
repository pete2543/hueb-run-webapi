using Microsoft.AspNetCore.Identity;

namespace RunEvent.Domain;

// ---------- Enums ----------
public enum NotificationChannel { Line, Email }
public enum NotificationStatus { Pending, Sent, Failed, Skipped }
public enum BroadcastStatus { Draft, Scheduled, Sending, Sent, Cancelled }
public enum CheckInAction { CheckIn, UndoCheckIn, KitPickup }
public enum RefundStatus { Requested, Approved, Completed, Rejected }

// roles: SuperAdmin, Organizer, Finance, Staff (seed ใน SeedData)

// ---------- ผู้ใช้แอดมิน (ASP.NET Core Identity) ----------
public class AdminUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public bool IsActive { get; set; } = true;       // ปิดการใช้งานแทนการลบ (audit log อ้างถึง)
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public List<AdminEventAccess> EventAccesses { get; set; } = new();
}

// จำกัดว่าแอดมิน/สตาฟคนไหนเข้าถึง Event ไหนได้ (SuperAdmin เข้าได้ทุกงาน)
public class AdminEventAccess
{
    public Guid AdminUserId { get; set; }
    public AdminUser AdminUser { get; set; } = null!;
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;
}

// ---------- ประวัติการกระทำของแอดมิน ----------
public class AuditLog
{
    public long Id { get; set; }
    public Guid? AdminUserId { get; set; }
    public string Action { get; set; } = "";          // "payment.approve", "order.cancel", "promo.update"
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? OldValues { get; set; }            // jsonb
    public string? NewValues { get; set; }            // jsonb
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ---------- ประวัติเช็กอิน / รับของ (ย้อนดูและแก้ผิดได้) ----------
public class CheckInLog
{
    public long Id { get; set; }
    public int RegistrationId { get; set; }
    public Registration Registration { get; set; } = null!;
    public CheckInAction Action { get; set; }
    public Guid? StaffUserId { get; set; }
    public string? Station { get; set; }              // "โต๊ะ 1"
    public string? Note { get; set; }
    public DateTime At { get; set; }
}

// ---------- คืนเงิน ----------
public class Refund
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public int? PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public RefundStatus Status { get; set; } = RefundStatus.Requested;
    public Guid? RequestedById { get; set; }
    public Guid? ApprovedById { get; set; }
    public string? ExternalRef { get; set; }          // เลขอ้างอิงการโอนคืน / refund id ของ gateway
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

// ---------- LINE ----------
// 1 แถว = 1 คนที่เรารู้ LINE userId (ได้จาก LINE Login / LIFF ตอนสมัคร)
public class LineAccount
{
    public int Id { get; set; }
    public string LineUserId { get; set; } = "";      // unique
    public string? DisplayName { get; set; }
    public string? PictureUrl { get; set; }
    public bool IsFollowing { get; set; }             // เป็นเพื่อนกับ OA อยู่ไหม (webhook follow/unfollow)
    public DateTime? FollowedAt { get; set; }
    public DateTime? UnfollowedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

// เทมเพลตข้อความ แก้จากหลังบ้านได้ ใช้ placeholder เช่น {{Name}} {{OrderNo}} {{Total}}
public class NotificationTemplate
{
    public int Id { get; set; }
    public string Key { get; set; } = "";             // "order_created", "order_paid", ...
    public NotificationChannel Channel { get; set; }
    public string? Subject { get; set; }              // ใช้กับอีเมล
    public string Body { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}

// Outbox: ทุกข้อความที่จะส่ง เขียนลงตารางนี้ก่อน แล้วให้ Hangfire ไปหยิบส่ง (ส่งซ้ำ/ย้อนดูได้)
public class Notification
{
    public long Id { get; set; }
    public NotificationChannel Channel { get; set; }
    public string Recipient { get; set; } = "";       // LINE userId หรืออีเมล
    public string TemplateKey { get; set; } = "";
    public string Payload { get; set; } = "{}";       // jsonb ค่าที่จะแทนใน placeholder
    public Guid? OrderId { get; set; }
    public Order? Order { get; set; }
    public int? RegistrationId { get; set; }
    public int? BroadcastId { get; set; }
    public Broadcast? Broadcast { get; set; }
    public string? DedupeKey { get; set; }            // เช่น "order_paid:{orderId}" กันส่งซ้ำ

    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime ScheduledAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

// ประกาศถึงนักวิ่งหลายคน (เช่น "แจ้งเลื่อนเวลาปล่อยตัว")
public class Broadcast
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public NotificationChannel Channel { get; set; }
    public int? AudienceDistanceId { get; set; }      // null = ทุกระยะ
    public bool OnlyPaid { get; set; } = true;
    public BroadcastStatus Status { get; set; } = BroadcastStatus.Draft;
    public DateTime? ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int RecipientCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ตั้งค่าทั่วไปที่แก้ได้จากหลังบ้าน (ห้ามเก็บ secret/token ตรงนี้ ให้ใช้ environment/secret store)
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Description { get; set; }
    public DateTime UpdatedAt { get; set; }
}
