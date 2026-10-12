using HuebRun.Domain;

namespace HuebRun.Infrastructure.Admin;

/// แอดมินที่เรียก request นี้ (Api เติมจาก token + DB) — ใช้ลง AuditLog และตรวจสิทธิ์รายงาน
public interface ICurrentAdmin
{
    Guid? UserId { get; }
    string? IpAddress { get; }
    string? Role { get; }
    IReadOnlyList<int> EventIds { get; }
}

public sealed class NoCurrentAdmin : ICurrentAdmin
{
    public Guid? UserId => null;
    public string? IpAddress => null;
    public string? Role => null;
    public IReadOnlyList<int> EventIds => [];
}

public static class CurrentAdminExtensions
{
    /// SuperAdmin เข้าได้ทุกงาน; บทบาทอื่นต้องมี AdminEventAccess ของงานนั้น
    public static void EnsureEventAccess(this ICurrentAdmin admin, int eventId)
    {
        if (admin.Role == AdminRoles.SuperAdmin || admin.EventIds.Contains(eventId)) return;
        throw AdminErrors.Forbidden();
    }
}
