using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Admin;

public sealed record AuditItem(
    long Id, DateTime CreatedAt, Guid? UserId, string? UserName, string Action, string EntityType, string EntityId,
    JsonElement? OldValues, JsonElement? NewValues, string? IpAddress);

/// ดูประวัติการกระทำของแอดมิน (SuperAdmin) — อ่านอย่างเดียว
public sealed class AdminAuditService(AppDbContext db)
{
    /// action: ตรงตัว หรือลงท้ายด้วย "." เพื่อดูทั้งกลุ่ม เช่น "payment." · from รวม / to ไม่รวม
    public async Task<Paged<AuditItem>> ListAsync(
        Guid? userId, string? action, string? entityType, string? entityId, DateTime? from, DateTime? to,
        int? page, int? pageSize, CancellationToken ct)
    {
        if (from is { Kind: DateTimeKind.Unspecified }) throw AdminErrors.Validation("from", "ต้องระบุเขตเวลา (เช่น ลงท้าย Z)");
        if (to is { Kind: DateTimeKind.Unspecified }) throw AdminErrors.Validation("to", "ต้องระบุเขตเวลา (เช่น ลงท้าย Z)");
        if (from is not null && to is not null && from.Value.ToUniversalTime() >= to.Value.ToUniversalTime())
            throw AdminErrors.Validation("to", "ต้องหลัง from");
        var (pageNo, size) = Paged<AuditItem>.Normalize(page, pageSize);

        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (userId is { } u) query = query.Where(a => a.AdminUserId == u);
        if (!string.IsNullOrWhiteSpace(action))
        {
            var a = action.Trim();
            query = a.EndsWith('.') ? query.Where(x => x.Action.StartsWith(a)) : query.Where(x => x.Action == a);
        }
        if (!string.IsNullOrWhiteSpace(entityType)) { var t = entityType.Trim(); query = query.Where(x => x.EntityType == t); }
        if (!string.IsNullOrWhiteSpace(entityId)) { var e = entityId.Trim(); query = query.Where(x => x.EntityId == e); }
        if (from is { } f) { var fu = f.ToUniversalTime(); query = query.Where(x => x.CreatedAt >= fu); }
        if (to is { } t2) { var tu = t2.ToUniversalTime(); query = query.Where(x => x.CreatedAt < tu); }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((pageNo - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Where(r => r.AdminUserId != null).Select(r => r.AdminUserId!.Value).Distinct().ToList();
        var names = ids.Count == 0 ? [] : await db.Set<Domain.AdminUser>().AsNoTracking()
            .Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);

        var items = rows.Select(r => new AuditItem(
            r.Id, r.CreatedAt, r.AdminUserId, r.AdminUserId is { } id && names.TryGetValue(id, out var n) ? n : null,
            r.Action, r.EntityType, r.EntityId, Parse(r.OldValues), Parse(r.NewValues), r.IpAddress)).ToList();
        return new Paged<AuditItem>(items, pageNo, size, total);
    }

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();
}
