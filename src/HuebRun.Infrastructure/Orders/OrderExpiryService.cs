using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HuebRun.Infrastructure.Orders;

/// เปลี่ยนออเดอร์ Pending ที่เลยเวลาเป็น Expired และคืนโควตา/เสื้อ/โค้ด
public sealed class OrderExpiryService(AppDbContext db, TimeProvider time, ILogger<OrderExpiryService>? logger = null)
{
    public const int BatchSize = 200;

    /// วนทีละ batch จนไม่เหลือออเดอร์ที่ถึงเวลา; ออเดอร์ที่ error ถูกข้าม (log ไว้) ไม่ให้ขวางออเดอร์อื่น
    public async Task<int> ExpireDueOrdersAsync(CancellationToken ct = default, int batchSize = BatchSize)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var failed = new List<Guid>();
        var expired = 0;
        while (true)
        {
            var due = await db.Orders.AsNoTracking()
                .Where(o => o.Status == OrderStatus.Pending && o.ExpiresAt <= now && !failed.Contains(o.Id))
                .OrderBy(o => o.ExpiresAt)
                .Select(o => o.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            foreach (var orderId in due)
            {
                try
                {
                    if (await ExpireAsync(orderId, now, ct)) expired++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add(orderId);
                    logger?.LogError(ex, "Failed to expire order {OrderId}; skipped", orderId);
                }
            }
            if (due.Count < batchSize) return expired;
        }
    }

    private async Task<bool> ExpireAsync(Guid orderId, DateTime now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // update แบบมีเงื่อนไข: ถ้าเพิ่งถูกจ่าย, ถูก job อีกตัวทำไปแล้ว หรือถูกยืดเวลา (รอตรวจสลิป) จะไม่คืนซ้ำ
        var updated = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending && o.ExpiresAt <= now)
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
