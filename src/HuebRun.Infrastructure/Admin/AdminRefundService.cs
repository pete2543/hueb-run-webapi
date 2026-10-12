using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HuebRun.Infrastructure.Admin;

public sealed record CreateRefundRequest(int? PaymentId, string? Reason, decimal? Amount);

/// Kind: Order = คืนเงินออเดอร์ที่จ่ายแล้ว (ยกเลิกนักวิ่ง/บิบ คืนโควตา) · Payment = คืนสลิปที่ไม่ใช่ของออเดอร์นั้น (ซ้ำซ้อน/โควตาเต็ม)
public sealed record AdminRefundItem(
    int RefundId, Guid OrderId, string OrderNo, string ContactName, string Phone, int? PaymentId, string Kind, decimal Amount,
    string Reason, string? Note, RefundStatus Status, string? ExternalRef, string? RequestedBy, string? ApprovedBy,
    DateTime CreatedAt, DateTime? ProcessedAt);

/// คืนเงิน (หัวข้อ 4.4): คืนทั้งใบเท่านั้น · approve = ปล่อยของ · complete = โอนคืนแล้ว + ออเดอร์ Refunded
public sealed class AdminRefundService(AppDbContext db, AuditWriter audit, ICurrentAdmin admin, TimeProvider time)
{
    public const int MaxTextLength = 500;
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<Paged<AdminRefundItem>> ListAsync(int eventId, RefundStatus? status, int? page, int? pageSize, CancellationToken ct)
    {
        admin.EnsureEventAccess(eventId);
        if (!await db.Events.AnyAsync(e => e.Id == eventId, ct)) throw AdminErrors.EventNotFound(eventId);
        var (pageNo, size) = Paged<AdminRefundItem>.Normalize(page, pageSize);
        var query = db.Refunds.AsNoTracking().Where(r => r.Order.EventId == eventId);
        if (status is { } s) query = query.Where(r => r.Status == s);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((pageNo - 1) * size).Take(size).Select(Projection).ToListAsync(ct);
        return new Paged<AdminRefundItem>(await WithNamesAsync(rows, ct), pageNo, size, total);
    }

    public async Task<AdminRefundItem> CreateAsync(Guid orderId, CreateRefundRequest req, CancellationToken ct)
    {
        var reason = req.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxTextLength)
            throw AdminErrors.Validation("reason", $"ต้องระบุเหตุผล (ไม่เกิน {MaxTextLength} ตัวอักษร)");

        var order = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => new { o.EventId, o.Status }).SingleOrDefaultAsync(ct)
                    ?? throw AdminErrors.OrderNotFound(orderId);
        admin.EnsureEventAccess(order.EventId);

        var payments = db.Payments.AsNoTracking().Where(p => p.OrderId == orderId);
        var payment = req.PaymentId is { } pid
            ? await payments.SingleOrDefaultAsync(p => p.Id == pid, ct)
            : await payments.Where(p => p.Status == PaymentStatus.Verified).OrderByDescending(p => p.Id).FirstOrDefaultAsync(ct);
        if (payment is null) throw AdminErrors.RefundNotAllowed("ไม่พบรายการชำระเงินที่คืนได้ของออเดอร์นี้");
        var ok = (payment.Status == PaymentStatus.Verified && order.Status == OrderStatus.Paid) || payment.Status == PaymentStatus.NeedsReview;
        if (!ok) throw AdminErrors.RefundNotAllowed($"คืนเงินไม่ได้: payment {payment.Status}, order {order.Status}");

        var paid = payment.PaidAmount ?? payment.ExpectedAmount;
        var amount = req.Amount ?? paid;
        if (amount <= 0 || amount > paid) throw AdminErrors.Validation("amount", $"ต้องมากกว่า 0 และไม่เกิน {paid:0.00}");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var refund = new Refund
        {
            OrderId = orderId, PaymentId = payment.Id, Amount = amount, Reason = reason, Status = RefundStatus.Requested,
            RequestedById = admin.UserId, CreatedAt = Now,
        };
        db.Refunds.Add(refund);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw AdminErrors.RefundExists();
        }
        audit.Add("refund.create", "Refund", refund.Id.ToString(), newValues: new { orderId, paymentId = payment.Id, amount, reason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ItemAsync(refund.Id, ct);
    }

    public async Task<AdminRefundItem> ApproveAsync(int refundId, CancellationToken ct)
    {
        await AuthorizeAsync(refundId, ct);
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Transition(refundId, RefundStatus.Requested, RefundStatus.Approved, s => s
            .SetProperty(r => r.ApprovedById, admin.UserId).SetProperty(r => r.ProcessedAt, (DateTime?)null), ct);

        var refund = await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId, ct);
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == refund.PaymentId, ct);
        if (payment.Status == PaymentStatus.Verified)
            await ReleaseOrderAsync(refund.OrderId, ct);
        else
        {
            // สลิปรอตรวจที่คืนเงินแล้ว ออกจากคิวตรวจสลิป (ออเดอร์เดิมไม่เปลี่ยน)
            var rejected = await db.Payments.Where(p => p.Id == payment.Id && p.Status == PaymentStatus.NeedsReview)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Rejected)
                    .SetProperty(p => p.RejectReason, PaymentRejectReason.AdminRejected)
                    .SetProperty(p => p.ReviewNote, "คืนเงินแล้ว").SetProperty(p => p.ReviewedBy, admin.UserId.ToString()), ct);
            if (rejected == 0) throw AdminErrors.RefundNotAllowed("สลิปนี้ถูกจัดการไปแล้ว");
        }

        audit.Add("refund.approve", "Refund", refundId.ToString(), new { status = RefundStatus.Requested.ToString() },
            new { status = RefundStatus.Approved.ToString(), kind = payment.Status == PaymentStatus.Verified ? "Order" : "Payment" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ItemAsync(refundId, ct);
    }

    public async Task<AdminRefundItem> RejectAsync(int refundId, string? note, CancellationToken ct)
    {
        note = note?.Trim();
        if (string.IsNullOrEmpty(note) || note.Length > MaxTextLength)
            throw AdminErrors.Validation("note", $"ต้องระบุเหตุผล (ไม่เกิน {MaxTextLength} ตัวอักษร)");
        await AuthorizeAsync(refundId, ct);
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Transition(refundId, RefundStatus.Requested, RefundStatus.Rejected, s => s
            .SetProperty(r => r.ApprovedById, admin.UserId).SetProperty(r => r.Note, note).SetProperty(r => r.ProcessedAt, now), ct);
        audit.Add("refund.reject", "Refund", refundId.ToString(), new { status = RefundStatus.Requested.ToString() }, new { status = "Rejected", note });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ItemAsync(refundId, ct);
    }

    /// โอนเงินคืนแล้ว: ต้องใส่เลขอ้างอิง → Completed และออเดอร์ที่คืนทั้งใบเป็น Refunded
    public async Task<AdminRefundItem> CompleteAsync(int refundId, string? externalRef, string? note, CancellationToken ct)
    {
        externalRef = externalRef?.Trim();
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (string.IsNullOrEmpty(externalRef) || externalRef.Length > 100) throw AdminErrors.Validation("externalRef", "ต้องระบุเลขอ้างอิงการโอนคืน (ไม่เกิน 100 ตัวอักษร)");
        if (note?.Length > MaxTextLength) throw AdminErrors.Validation("note", $"ไม่เกิน {MaxTextLength} ตัวอักษร");
        await AuthorizeAsync(refundId, ct);
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Transition(refundId, RefundStatus.Approved, RefundStatus.Completed, s => s
            .SetProperty(r => r.ExternalRef, externalRef).SetProperty(r => r.Note, note).SetProperty(r => r.ProcessedAt, now), ct);

        var refund = await db.Refunds.AsNoTracking().Include(r => r.Payment).SingleAsync(r => r.Id == refundId, ct);
        if (refund.Payment!.Status == PaymentStatus.Verified)
            await db.Orders.Where(o => o.Id == refund.OrderId && o.Status == OrderStatus.Paid)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Refunded), ct);

        audit.Add("refund.complete", "Refund", refundId.ToString(), new { status = RefundStatus.Approved.ToString() },
            new { status = RefundStatus.Completed.ToString(), externalRef });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ItemAsync(refundId, ct);
    }

    // ---------- helpers ----------

    // ยกเลิกนักวิ่ง/บิบ แล้วคืนโควตา ไซซ์เสื้อ และการใช้โค้ด (ลอจิกเดียวกับ OrderExpiryService)
    private async Task ReleaseOrderAsync(Guid orderId, CancellationToken ct)
    {
        var status = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync(ct);
        if (status != OrderStatus.Paid) throw AdminErrors.RefundNotAllowed($"ออเดอร์อยู่ในสถานะ {status}");

        var regs = await db.Registrations.AsNoTracking().Where(r => r.OrderId == orderId && !r.IsCancelled)
            .Select(r => new { r.Id, r.EventDistanceId, r.EventShirtSizeId }).ToListAsync(ct);
        var ids = regs.Select(r => r.Id).ToList();
        await db.Registrations.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCancelled, true), ct);

        foreach (var g in regs.GroupBy(r => r.EventDistanceId).OrderBy(g => g.Key))
        {
            var (id, n) = (g.Key, g.Count());
            await db.EventDistances.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ReservedCount, d => d.ReservedCount - n), ct);
        }
        foreach (var g in regs.GroupBy(r => r.EventShirtSizeId).OrderBy(g => g.Key))
        {
            var (id, n) = (g.Key, g.Count());
            await db.EventShirtSizes.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedCount, x => x.ReservedCount - n), ct);
        }
        var promoId = await db.Orders.Where(o => o.Id == orderId).Select(o => o.PromoCodeId).SingleAsync(ct);
        if (promoId is { } pid)
            await db.PromoCodes.Where(p => p.Id == pid && p.UsedCount > 0).ExecuteUpdateAsync(s => s.SetProperty(p => p.UsedCount, p => p.UsedCount - 1), ct);
    }

    private async Task Transition(int refundId, RefundStatus from, RefundStatus to,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Refund>> extra, CancellationToken ct)
    {
        var updated = await db.Refunds.Where(r => r.Id == refundId && r.Status == from)
            .ExecuteUpdateAsync(s => { s.SetProperty(r => r.Status, to); extra(s); }, ct);
        if (updated == 0)
            throw AdminErrors.RefundWrongState(await db.Refunds.AsNoTracking().Where(r => r.Id == refundId).Select(r => r.Status).SingleAsync(ct));
    }

    private async Task AuthorizeAsync(int refundId, CancellationToken ct)
    {
        var eventId = await db.Refunds.AsNoTracking().Where(r => r.Id == refundId).Select(r => (int?)r.Order.EventId).SingleOrDefaultAsync(ct)
                      ?? throw AdminErrors.RefundNotFound(refundId);
        admin.EnsureEventAccess(eventId);
    }

    private static readonly System.Linq.Expressions.Expression<Func<Refund, Row>> Projection = r => new Row(
        r.Id, r.OrderId, r.Order.OrderNo, r.Order.ContactName, r.Order.Phone, r.PaymentId,
        r.Payment != null && r.Payment.Status == PaymentStatus.Verified ? "Order" : "Payment", r.Amount, r.Reason, r.Note, r.Status,
        r.ExternalRef, r.RequestedById, r.ApprovedById, r.CreatedAt, r.ProcessedAt);

    private sealed record Row(int Id, Guid OrderId, string OrderNo, string ContactName, string Phone, int? PaymentId, string Kind, decimal Amount,
        string Reason, string? Note, RefundStatus Status, string? ExternalRef, Guid? RequestedById, Guid? ApprovedById, DateTime CreatedAt, DateTime? ProcessedAt);

    private async Task<AdminRefundItem> ItemAsync(int refundId, CancellationToken ct) =>
        (await WithNamesAsync([await db.Refunds.AsNoTracking().Where(r => r.Id == refundId).Select(Projection).SingleAsync(ct)], ct)).Single();

    private async Task<List<AdminRefundItem>> WithNamesAsync(List<Row> rows, CancellationToken ct)
    {
        var ids = rows.SelectMany(r => new[] { r.RequestedById, r.ApprovedById }).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var names = ids.Count == 0 ? [] : await db.Set<AdminUser>().AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? Name(Guid? id) => id is { } g && names.TryGetValue(g, out var n) ? n : null;
        return rows.Select(r => new AdminRefundItem(r.Id, r.OrderId, r.OrderNo, r.ContactName, r.Phone, r.PaymentId, r.Kind, r.Amount, r.Reason,
            r.Note, r.Status, r.ExternalRef, Name(r.RequestedById), Name(r.ApprovedById), r.CreatedAt, r.ProcessedAt)).ToList();
    }
}
