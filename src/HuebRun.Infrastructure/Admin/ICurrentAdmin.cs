namespace HuebRun.Infrastructure.Admin;

/// แอดมินที่เรียก request นี้ (Api เติมจาก token) — ใช้ลง AuditLog
public interface ICurrentAdmin
{
    Guid? UserId { get; }
    string? IpAddress { get; }
}

public sealed class NoCurrentAdmin : ICurrentAdmin
{
    public Guid? UserId => null;
    public string? IpAddress => null;
}
