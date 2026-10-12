using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HuebRun.Infrastructure.Payments;

/// ชำระเงินด้วยสลิป: ข้อมูล QR, รับสลิป, และนำผลตรวจไปเปลี่ยนสถานะ (ใช้ร่วมกับ job ตรวจซ้ำ)
public sealed class PaymentService(
    AppDbContext db, OrderPaymentService orders, ISlipVerifier verifier, ISlipStorage storage,
    TimeProvider time, IOptions<PaymentOptions> options)
{
    public const int MaxAttempts = 6;
    public static readonly TimeSpan MaxVerifyAge = TimeSpan.FromMinutes(60);
    /// Payment ใหม่ถูกจองให้ request ที่รับสลิปช่วงนี้ job จะยังไม่แตะ
    public static readonly TimeSpan ClaimWindow = TimeSpan.FromMinutes(5);
    private const string VerifyingIndex = "IX_Payments_OrderId_Verifying";
    private static readonly int[] BackoffMinutes = [1, 2, 5, 10, 15, 30];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<PaymentInfo> GetPaymentInfoAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Event).FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw OrderErrors.OrderNotFound(orderId);
        var status = order.GetEffectiveStatus(Now);
        if (status == OrderStatus.Paid) throw PaymentErrors.OrderAlreadyPaid();
        if (status != OrderStatus.Pending) throw PaymentErrors.OrderNotPayable(status);

        var latest = await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == orderId).OrderByDescending(p => p.Id)
            .Select(p => new PaymentSummary(p.Id, p.Status, p.RejectReason, p.CreatedAt, p.ReviewNote))
            .FirstOrDefaultAsync(ct);
        return new PaymentInfo(order.Id, order.OrderNo, order.PayableAmount, order.Event.PromptPayAccountName,
            PromptPayPayload.Create(order.Event.PromptPayId, order.PayableAmount), order.ExpiresAt, latest);
    }

    /// รับสลิป: hold ออเดอร์ + บันทึก Payment + เก็บรูป ใน transaction เดียว แล้วตรวจทันที
    public async Task<SlipSubmission> SubmitSlipAsync(Guid orderId, SlipImage image, CancellationToken ct)
    {
        var now = Now;
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw OrderErrors.OrderNotFound(orderId);
        if (order.Status == OrderStatus.Paid) throw PaymentErrors.OrderAlreadyPaid();
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Refunded) throw PaymentErrors.OrderNotPayable(order.Status);

        var hash = image.Sha256;
        if (await db.Payments.AnyAsync(p => p.SlipHash == hash, ct)) throw PaymentErrors.SlipDuplicate();
        if (await db.Payments.AnyAsync(p => p.OrderId == orderId && p.Status == PaymentStatus.Verifying, ct))
            throw PaymentErrors.PaymentInProgress();

        var payment = new Payment
        {
            OrderId = orderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.Verifying,
            ExpectedAmount = order.PayableAmount, SlipHash = hash, CreatedAt = now, NextCheckAt = now + ClaimWindow,
        };
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // hold: ยืดเวลาหมดอายุให้ครอบช่วงตรวจ (หมดเวลาไปแล้วหรือยืดอยู่แล้วก็ไม่แตะ)
            var holdUntil = now.AddMinutes(options.Value.VerificationHoldMinutes);
            await db.Orders
                .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending && o.ExpiresAt > now && o.ExpiresAt < holdUntil)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, holdUntil), ct);

            db.Payments.Add(payment);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
            {
                throw pg.ConstraintName == VerifyingIndex ? PaymentErrors.PaymentInProgress() : PaymentErrors.SlipDuplicate();
            }

            payment.SlipObjectKey = $"slips/{order.EventId}/{orderId}/{payment.Id}{image.Extension}";
            await storage.PutAsync(payment.SlipObjectKey, image.Content, image.ContentType, ct);   // ล้ม → rollback ทั้งหมด
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // หลัง commit ห้ามยกเลิกกลางทาง: SlipOK บันทึกสลิปแล้ว (log=true) ถ้าทิ้งผล ครั้งหน้าจะได้ 1012 → ตรวจมือ
        // เวลาถูกจำกัดด้วย timeout ของ HttpClient อยู่แล้ว
        var result = await verifier.VerifyAsync(image, payment.ExpectedAmount, CancellationToken.None);
        var submission = await ApplyResultAsync(payment.Id, result, CancellationToken.None);
        if (submission.Status == PaymentStatus.Rejected) throw PaymentErrors.SlipRejected(submission.PaymentId, submission.Reason!);
        return submission;
    }

    /// นำผลตรวจไปใช้กับ Payment ที่ยัง Verifying (ล็อกแถวไว้ คนอื่นจัดการไปแล้ว = คืนสถานะปัจจุบัน)
    public async Task<SlipSubmission> ApplyResultAsync(int paymentId, SlipCheckResult result, CancellationToken ct)
    {
        var now = Now;
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payment = (await db.Payments
            .FromSql($"SELECT * FROM \"Payments\" WHERE \"Id\" = {paymentId} FOR UPDATE")
            .ToListAsync(ct)).Single();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == payment.OrderId, ct);
        if (payment.Status != PaymentStatus.Verifying) return Summary(payment, order.Status);

        if (result.RawJson is { } raw) payment.ProviderResponse = raw;
        switch (result)
        {
            case SlipCheckResult.Verified v:
                await ApplyVerifiedAsync(payment, order, v, now, ct);
                break;
            case SlipCheckResult.Duplicate d:
                var ours = d.TransRef is not null
                    && await db.Payments.AnyAsync(p => p.Id != payment.Id && p.SlipTransRef == d.TransRef, ct);
                // ไม่รู้จัก = SlipOK อาจบันทึกครั้งก่อนที่เราไม่ได้รับคำตอบ → ให้คนตรวจ ไม่ตีกลับคนที่จ่ายจริง
                if (ours) Reject(payment, PaymentRejectReason.DuplicateSlip);
                else Review(payment, PaymentRejectReason.DuplicateUnknown);
                break;
            case SlipCheckResult.Rejected r:
                Reject(payment, r.Reason);
                break;
            case SlipCheckResult.Retry r:
                Postpone(payment, r.After, now);
                break;
            case SlipCheckResult.Unavailable:
                Postpone(payment, TimeSpan.FromMinutes(BackoffMinutes[Math.Min(payment.CheckAttempts, BackoffMinutes.Length - 1)]), now);
                break;
        }
        if (payment.Status == PaymentStatus.Verifying && (payment.CheckAttempts >= MaxAttempts || now - payment.CreatedAt > MaxVerifyAge))
            Review(payment, PaymentRejectReason.VerifyTimeout);

        // ไม่ผ่าน: ปลด hold เหลือเวลาให้อัปโหลดใหม่
        if (payment.Status == PaymentStatus.Rejected && order.Status == OrderStatus.Pending)
            await ReleaseHoldAsync(order, now, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Summary(payment, payment.Status == PaymentStatus.Verified ? OrderStatus.Paid : order.Status);
    }

    /// ปลด hold ของออเดอร์ที่สลิปไม่ผ่าน: เหลือเวลาให้อัปโหลดใหม่ แต่ไม่สั้นกว่ากำหนดเดิมของออเดอร์
    public async Task ReleaseHoldAsync(Order order, DateTime now, CancellationToken ct)
    {
        var reservation = await db.Events.Where(e => e.Id == order.EventId).Select(e => e.ReservationMinutes).SingleAsync(ct);
        var original = order.CreatedAt.AddMinutes(reservation);
        var grace = now.AddMinutes(options.Value.RejectGraceMinutes);
        if (grace < original) grace = original;
        await db.Orders.Where(o => o.Id == order.Id && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, grace), ct);
    }

    private async Task ApplyVerifiedAsync(Payment payment, Order order, SlipCheckResult.Verified v, DateTime now, CancellationToken ct)
    {
        if (v.Amount != payment.ExpectedAmount)
        {
            Reject(payment, PaymentRejectReason.AmountMismatch);
            return;
        }
        if (await db.Payments.AnyAsync(p => p.Id != payment.Id && p.SlipTransRef == v.TransRef, ct))
        {
            Reject(payment, PaymentRejectReason.DuplicateSlip);
            return;
        }

        payment.SlipTransRef = v.TransRef;
        payment.PaidAmount = v.Amount;
        payment.TransferredAt = v.TransferredAt;
        payment.NextCheckAt = null;
        if (order.Status != OrderStatus.Pending)
        {
            // จ่ายแล้วด้วยสลิปอื่น ≠ หมดเวลา: แอดมินต้องปฏิเสธ/คืนเงินสลิปซ้ำซ้อนนี้ ห้ามอนุมัติ
            Review(payment, order.Status == OrderStatus.Paid ? PaymentRejectReason.AlreadyPaid : PaymentRejectReason.OrderExpired);
            return;
        }

        payment.Status = PaymentStatus.Verified;
        payment.VerifiedAt = now;
        try
        {
            if ((await orders.MarkPaidAsync(order.Id, now, ct)).AlreadyPaid)
            {
                // แข่งกับสลิปอื่นที่จ่ายผ่านก่อน: ห้ามมี Payment Verified สองใบบนออเดอร์เดียว
                payment.VerifiedAt = null;
                Review(payment, PaymentRejectReason.AlreadyPaid);
            }
        }
        catch (InvalidOperationException)
        {
            // job หมดเวลาชนะไปก่อนระหว่างตรวจ
            payment.VerifiedAt = null;
            Review(payment, PaymentRejectReason.OrderExpired);
        }
    }

    private static void Reject(Payment p, string reason) =>
        (p.Status, p.RejectReason, p.NextCheckAt) = (PaymentStatus.Rejected, reason, null);

    private static void Review(Payment p, string reason) =>
        (p.Status, p.RejectReason, p.NextCheckAt) = (PaymentStatus.NeedsReview, reason, null);

    private static void Postpone(Payment p, TimeSpan after, DateTime now)
    {
        p.CheckAttempts++;
        p.NextCheckAt = now + after;
    }

    private static SlipSubmission Summary(Payment p, OrderStatus orderStatus) =>
        new(p.Id, p.Status, orderStatus, p.NextCheckAt, p.RejectReason);
}
