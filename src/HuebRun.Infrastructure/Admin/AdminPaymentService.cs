using HuebRun.Domain;
using HuebRun.Infrastructure.Line;
using HuebRun.Infrastructure.Orders;
using HuebRun.Infrastructure.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HuebRun.Infrastructure.Admin;

public sealed record AdminPaymentItem(
    int PaymentId, Guid OrderId, string OrderNo, OrderStatus OrderStatus, string ContactName, string Phone,
    int RunnerCount, decimal ExpectedAmount, decimal? PaidAmount, PaymentStatus Status, string? RejectReason,
    string? ReviewNote, DateTime CreatedAt, DateTime? TransferredAt, string? ReviewedBy);

public sealed record SlipLink(string Url, DateTime ExpiresAt);

/// ตรวจสลิปด้วยมือ (Finance/SuperAdmin) — กติกา docs/admin-api-plan.md หัวข้อ 4.1
public sealed class AdminPaymentService(
    AppDbContext db, OrderPaymentService orders, PaymentService payments, ISlipStorage storage,
    AuditWriter audit, ICurrentAdmin admin, TimeProvider time, IOptions<PaymentOptions> options, NotificationEnqueuer notifications)
{
    public static readonly TimeSpan SlipLinkTtl = TimeSpan.FromMinutes(5);
    public const int MaxNoteLength = 500;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<Paged<AdminPaymentItem>> ListAsync(int eventId, PaymentStatus? status, int? page, int? pageSize, CancellationToken ct)
    {
        admin.EnsureEventAccess(eventId);
        var (pageNo, size) = Paged<AdminPaymentItem>.Normalize(page, pageSize);
        var query = db.Payments.AsNoTracking().Where(p => p.Order.EventId == eventId);
        if (status is { } s) query = query.Where(p => p.Status == s);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Skip((pageNo - 1) * size).Take(size)
            .Select(p => new AdminPaymentItem(
                p.Id, p.OrderId, p.Order.OrderNo, p.Order.Status, p.Order.ContactName, p.Order.Phone,
                p.Order.Registrations.Count(r => !r.IsCancelled), p.ExpectedAmount, p.PaidAmount, p.Status,
                p.RejectReason, p.ReviewNote, p.CreatedAt, p.TransferredAt, p.ReviewedBy))
            .ToListAsync(ct);
        return new Paged<AdminPaymentItem>(items, pageNo, size, total);
    }

    public async Task<SlipLink> GetSlipLinkAsync(int paymentId, CancellationToken ct)
    {
        var info = await LoadAsync(paymentId, ct);
        if (info.SlipObjectKey is null) throw AdminErrors.NoSlip(paymentId);
        var url = await storage.GetPresignedUrlAsync(info.SlipObjectKey, SlipLinkTtl, ct);
        return new SlipLink(url, Now + SlipLinkTtl);
    }

    public async Task<AdminPaymentItem> ApproveAsync(int paymentId, CancellationToken ct)
    {
        await LoadAsync(paymentId, ct);   // 404 / สิทธิ์งาน
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // ยึดงานแบบมีเงื่อนไข: สองคนกดพร้อมกันได้คนเดียว
        var claimed = await db.Payments.Where(p => p.Id == paymentId && p.Status == PaymentStatus.NeedsReview)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PaymentStatus.Verified)
                .SetProperty(p => p.VerifiedAt, now)
                .SetProperty(p => p.ReviewedBy, admin.UserId.ToString())
                .SetProperty(p => p.PaidAmount, p => p.PaidAmount ?? p.ExpectedAmount), ct);
        if (claimed == 0) throw AdminErrors.PaymentNotReviewable(await StatusOf(paymentId, ct));

        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId, ct);
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == payment.OrderId, ct);
        if (payment.RejectReason == PaymentRejectReason.AlreadyPaid || order.Status == OrderStatus.Paid)
            throw AdminErrors.ApproveBlocked(PaymentRejectReason.AlreadyPaid);   // จ่ายแล้ว → ปฏิเสธ/คืนเงิน ไม่ใช่อนุมัติ
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Refunded)
            throw PaymentErrors.OrderNotPayable(order.Status);
        if (order.Status == OrderStatus.Expired)
            await ReviveExpiredOrderAsync(order, ct);

        if ((await orders.MarkPaidAsync(order.Id, now, ct)).AlreadyPaid)
            throw AdminErrors.ApproveBlocked(PaymentRejectReason.AlreadyPaid);

        audit.Add("payment.approve", "Payment", paymentId.ToString(),
            new { status = PaymentStatus.NeedsReview.ToString(), reason = payment.RejectReason, orderStatus = order.Status.ToString() },
            new { status = PaymentStatus.Verified.ToString(), orderStatus = OrderStatus.Paid.ToString() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ItemAsync(paymentId, ct);
    }

    public async Task<AdminPaymentItem> RejectAsync(int paymentId, string? note, CancellationToken ct)
    {
        note = note?.Trim();
        if (string.IsNullOrEmpty(note) || note.Length > MaxNoteLength)
            throw AdminErrors.Validation("note", $"ต้องระบุเหตุผล (ไม่เกิน {MaxNoteLength} ตัวอักษร)");
        await LoadAsync(paymentId, ct);
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var claimed = await db.Payments.Where(p => p.Id == paymentId && p.Status == PaymentStatus.NeedsReview)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PaymentStatus.Rejected)
                .SetProperty(p => p.RejectReason, PaymentRejectReason.AdminRejected)
                .SetProperty(p => p.ReviewNote, note)
                .SetProperty(p => p.ReviewedBy, admin.UserId.ToString()), ct);
        if (claimed == 0) throw AdminErrors.PaymentNotReviewable(await StatusOf(paymentId, ct));

        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId, ct);
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == payment.OrderId, ct);
        if (order.Status == OrderStatus.Pending)
            await payments.ReleaseHoldAsync(order, now, ct);   // เปิดให้อัปโหลดสลิปใหม่ได้ถ้ายังไม่หมดเวลา
        await notifications.EnqueueForOrderAsync(order.Id, "payment_rejected",
            new Dictionary<string, string> { ["Reason"] = note }, $"payment_rejected:{paymentId}", ct);

        audit.Add("payment.reject", "Payment", paymentId.ToString(),
            new { status = PaymentStatus.NeedsReview.ToString() }, new { status = PaymentStatus.Rejected.ToString(), note });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ItemAsync(paymentId, ct);
    }

    /// ส่งกลับเข้า job ตรวจสลิปใหม่ — ใช้ได้เฉพาะ VERIFY_TIMEOUT (SlipOK ล่ม/โควตาหมด)
    public async Task<AdminPaymentItem> RecheckAsync(int paymentId, CancellationToken ct)
    {
        await LoadAsync(paymentId, ct);
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var claimed = await db.Payments
                .Where(p => p.Id == paymentId && p.Status == PaymentStatus.NeedsReview && p.RejectReason == PaymentRejectReason.VerifyTimeout)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, PaymentStatus.Verifying)
                    .SetProperty(p => p.RejectReason, (string?)null)
                    .SetProperty(p => p.CheckAttempts, 0)
                    .SetProperty(p => p.NextCheckAt, now), ct);
            if (claimed == 0)
            {
                var current = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => new { p.Status, p.RejectReason }).SingleAsync(ct);
                throw current.Status == PaymentStatus.NeedsReview
                    ? AdminErrors.ApproveBlocked($"recheck only for {PaymentRejectReason.VerifyTimeout}, got {current.RejectReason}")
                    : AdminErrors.PaymentNotReviewable(current.Status);
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw PaymentErrors.PaymentInProgress();   // นักวิ่งส่งสลิปใหม่เข้ามาแล้ว
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw PaymentErrors.PaymentInProgress();
        }

        // ยืดเวลาออเดอร์ให้ครอบช่วงตรวจ ไม่งั้น job หมดเวลาอาจปิดออเดอร์ก่อนได้ผล
        var holdUntil = now.AddMinutes(options.Value.VerificationHoldMinutes);
        await db.Orders
            .Where(o => o.Status == OrderStatus.Pending && o.ExpiresAt < holdUntil && o.Payments.Any(p => p.Id == paymentId))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, holdUntil), ct);

        audit.Add("payment.recheck", "Payment", paymentId.ToString(),
            new { status = PaymentStatus.NeedsReview.ToString(), reason = PaymentRejectReason.VerifyTimeout },
            new { status = PaymentStatus.Verifying.ToString() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ItemAsync(paymentId, ct);
    }

    // ออเดอร์หมดเวลาแต่เงินเข้า: จองโควตา/ไซซ์เสื้อ/โค้ดใหม่แบบ atomic ถ้าเต็มต้องไม่อนุมัติ (ต้องคืนเงิน) — exception ทำให้ทั้ง transaction ย้อนกลับ
    private async Task ReviveExpiredOrderAsync(Order order, CancellationToken ct)
    {
        var regs = await db.Registrations.AsNoTracking()
            .Where(r => r.OrderId == order.Id && !r.IsCancelled)
            .Select(r => new { r.EventDistanceId, r.EventShirtSizeId })
            .ToListAsync(ct);

        foreach (var group in regs.GroupBy(r => r.EventDistanceId).OrderBy(g => g.Key))
        {
            var (id, n) = (group.Key, group.Count());
            var ok = await db.EventDistances.Where(d => d.Id == id && d.ReservedCount + n <= d.Quota)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReservedCount, d => d.ReservedCount + n), ct);
            if (ok == 0) throw AdminErrors.CapacityFull("โควตาระยะ");
        }
        foreach (var group in regs.GroupBy(r => r.EventShirtSizeId).OrderBy(g => g.Key))
        {
            var (id, n) = (group.Key, group.Count());
            var ok = await db.EventShirtSizes.Where(s => s.Id == id && s.ReservedCount + n <= s.Stock)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedCount, x => x.ReservedCount + n), ct);
            if (ok == 0) throw AdminErrors.CapacityFull("เสื้อไซซ์นี้");
        }
        // โค้ดส่วนลด: นักวิ่งจ่ายราคาลดไปแล้ว นับการใช้กลับโดยไม่เช็ก MaxUses (ไม่ให้โค้ดเต็มกลายเป็นเหตุปฏิเสธคนที่จ่ายจริง)
        if (order.PromoCodeId is { } pid)
            await db.PromoCodes.Where(p => p.Id == pid)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UsedCount, p => p.UsedCount + 1), ct);

        await db.Orders.Where(o => o.Id == order.Id && o.Status == OrderStatus.Expired)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Pending), ct);
    }

    private async Task<(int EventId, string? SlipObjectKey)> LoadAsync(int paymentId, CancellationToken ct)
    {
        var info = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId)
            .Select(p => new { p.Order.EventId, p.SlipObjectKey }).SingleOrDefaultAsync(ct)
            ?? throw AdminErrors.PaymentNotFound(paymentId);
        admin.EnsureEventAccess(info.EventId);
        return (info.EventId, info.SlipObjectKey);
    }

    private Task<PaymentStatus> StatusOf(int paymentId, CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => p.Status).SingleAsync(ct);

    private async Task<AdminPaymentItem> ItemAsync(int paymentId, CancellationToken ct) =>
        await db.Payments.AsNoTracking().Where(p => p.Id == paymentId)
            .Select(p => new AdminPaymentItem(
                p.Id, p.OrderId, p.Order.OrderNo, p.Order.Status, p.Order.ContactName, p.Order.Phone,
                p.Order.Registrations.Count(r => !r.IsCancelled), p.ExpectedAmount, p.PaidAmount, p.Status,
                p.RejectReason, p.ReviewNote, p.CreatedAt, p.TransferredAt, p.ReviewedBy))
            .SingleAsync(ct);
}
