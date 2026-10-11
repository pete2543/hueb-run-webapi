using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Orders;

/// เปลี่ยนออเดอร์ Pending ที่เลยเวลาเป็น Expired และคืนโควตา/เสื้อ/โค้ด
public sealed class OrderExpiryService(AppDbContext db, TimeProvider time)
{
    public const int BatchSize = 200;

    public async Task<int> ExpireDueOrdersAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Pending && o.ExpiresAt <= now)
            .OrderBy(o => o.ExpiresAt)
            .Select(o => o.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        var expired = 0;
        foreach (var orderId in due)
            if (await ExpireAsync(orderId, ct)) expired++;
        return expired;
    }

    private async Task<bool> ExpireAsync(Guid orderId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // update แบบมีเงื่อนไข: ถ้าเพิ่งถูกจ่ายหรือถูก job อีกตัวทำไปแล้ว จะไม่คืนซ้ำ
        var updated = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired), ct);
        if (updated == 0) return false;

        var registrations = await db.Registrations.AsNoTracking()
            .Where(r => r.OrderId == orderId && !r.IsCancelled)
            .Select(r => new { r.EventDistanceId, r.EventShirtSizeId })
            .ToListAsync(ct);
        foreach (var group in registrations.GroupBy(r => r.EventDistanceId).OrderBy(g => g.Key))
        {
            var (id, n) = (group.Key, group.Count());
            await db.EventDistances.Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReservedCount, d => d.ReservedCount - n), ct);
        }
        foreach (var group in registrations.GroupBy(r => r.EventShirtSizeId).OrderBy(g => g.Key))
        {
            var (id, n) = (group.Key, group.Count());
            await db.EventShirtSizes.Where(s => s.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedCount, x => x.ReservedCount - n), ct);
        }

        var promoId = await db.Orders.Where(o => o.Id == orderId).Select(o => o.PromoCodeId).SingleAsync(ct);
        if (promoId is { } pid)
            await db.PromoCodes.Where(p => p.Id == pid && p.UsedCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UsedCount, p => p.UsedCount - 1), ct);

        await tx.CommitAsync(ct);
        return true;
    }
}
