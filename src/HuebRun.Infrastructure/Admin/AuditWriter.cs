using System.Text.Json;
using HuebRun.Domain;

namespace HuebRun.Infrastructure.Admin;

/// เพิ่มแถว AuditLog เข้า DbContext ของ request — ผู้เรียก SaveChanges เอง
/// เพื่อให้อยู่ transaction เดียวกับการเปลี่ยนแปลงที่บันทึก (ห้ามใส่รหัสผ่าน/ความลับใน old/new)
public sealed class AuditWriter(AppDbContext db, ICurrentAdmin admin, TimeProvider time)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Add(string action, string entityType, string entityId, object? oldValues = null, object? newValues = null,
        Guid? actingUserId = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            AdminUserId = actingUserId ?? admin.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValues = oldValues is null ? null : JsonSerializer.Serialize(oldValues, JsonOptions),
            NewValues = newValues is null ? null : JsonSerializer.Serialize(newValues, JsonOptions),
            IpAddress = admin.IpAddress,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        });
    }
}
