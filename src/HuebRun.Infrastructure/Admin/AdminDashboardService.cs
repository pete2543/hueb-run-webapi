using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Admin;

public sealed record DashboardOrders(int Total, int Paid, int Pending, int Expired, int Cancelled, int Refunded);
public sealed record DashboardPayments(int NeedsReview, int Verifying);
public sealed record DashboardRevenue(decimal Paid, decimal PendingAmount);
public sealed record DashboardDistance(int Id, string Name, int Quota, int Reserved, int Remaining, int PaidRunners, int PendingRunners);
public sealed record DashboardShirt(int Id, string Size, int Stock, int Reserved, int Remaining, int PaidRunners);

public sealed record Dashboard(
    int EventId, DateTime AsOf, DashboardOrders Orders, int PaidRunners, int PendingRunners, DashboardPayments Payments,
    DashboardRevenue Revenue, IReadOnlyList<DashboardDistance> Distances, IReadOnlyList<DashboardShirt> Shirts);

/// ตัวเลขรวมเท่านั้น ไม่มีข้อมูลส่วนบุคคล (ทุกบทบาทที่เข้าหน้านี้ได้เห็นเหมือนกัน)
public sealed class AdminDashboardService(AppDbContext db, ICurrentAdmin admin, TimeProvider time)
{
    public async Task<Dashboard> GetAsync(int eventId, CancellationToken ct)
    {
        admin.EnsureEventAccess(eventId);
        if (!await db.Events.AnyAsync(e => e.Id == eventId, ct)) throw AdminErrors.EventNotFound(eventId);
        var now = time.GetUtcNow().UtcDateTime;

        // ออเดอร์ Pending ที่เลยเวลาแต่ job ยังไม่ปิด นับเป็น Expired (เหมือนหน้ารายการออเดอร์)
        var orders = await db.Orders.AsNoTracking().Where(o => o.EventId == eventId)
            .Select(o => new
            {
                Status = o.Status == OrderStatus.Pending && o.ExpiresAt <= now ? OrderStatus.Expired : o.Status,
                o.PayableAmount,
            }).ToListAsync(ct);
        int Count(OrderStatus s) => orders.Count(o => o.Status == s);
        decimal Sum(OrderStatus s) => orders.Where(o => o.Status == s).Sum(o => o.PayableAmount);

        // นักวิ่งต่อระยะ/ไซซ์ แยกจ่ายแล้ว–รอจ่าย (นับเฉพาะคนที่ไม่ถูกยกเลิก)
        var runners = await db.Registrations.AsNoTracking()
            .Where(r => r.EventId == eventId && !r.IsCancelled && (r.Order.Status == OrderStatus.Paid || (r.Order.Status == OrderStatus.Pending && r.Order.ExpiresAt > now)))
            .Select(r => new { r.EventDistanceId, r.EventShirtSizeId, Paid = r.Order.Status == OrderStatus.Paid })
            .ToListAsync(ct);

        var distances = await db.EventDistances.AsNoTracking().Where(d => d.EventId == eventId).OrderBy(d => d.DistanceKm).ThenBy(d => d.Id).ToListAsync(ct);
        var shirts = await db.EventShirtSizes.AsNoTracking().Where(s => s.EventId == eventId).OrderBy(s => s.Id).ToListAsync(ct);

        var payments = await db.Payments.AsNoTracking().Where(p => p.Order.EventId == eventId
                && (p.Status == PaymentStatus.NeedsReview || p.Status == PaymentStatus.Verifying))
            .GroupBy(p => p.Status).Select(g => new { Status = g.Key, N = g.Count() }).ToListAsync(ct);

        return new Dashboard(
            eventId, now,
            new DashboardOrders(orders.Count, Count(OrderStatus.Paid), Count(OrderStatus.Pending), Count(OrderStatus.Expired),
                Count(OrderStatus.Cancelled), Count(OrderStatus.Refunded)),
            runners.Count(r => r.Paid), runners.Count(r => !r.Paid),
            new DashboardPayments(payments.FirstOrDefault(p => p.Status == PaymentStatus.NeedsReview)?.N ?? 0,
                payments.FirstOrDefault(p => p.Status == PaymentStatus.Verifying)?.N ?? 0),
            new DashboardRevenue(Sum(OrderStatus.Paid), Sum(OrderStatus.Pending)),
            [.. distances.Select(d => new DashboardDistance(d.Id, d.Name, d.Quota, d.ReservedCount, d.GetRemaining(),
                runners.Count(r => r.EventDistanceId == d.Id && r.Paid), runners.Count(r => r.EventDistanceId == d.Id && !r.Paid)))],
            [.. shirts.Select(s => new DashboardShirt(s.Id, s.Size, s.Stock, s.ReservedCount, s.GetRemaining(),
                runners.Count(r => r.EventShirtSizeId == s.Id && r.Paid)))]);
    }
}
