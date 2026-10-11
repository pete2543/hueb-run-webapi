using HuebRun.Domain;
using HuebRun.Infrastructure;
using HuebRun.Infrastructure.Orders;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class PaymentServiceTests(PostgresFixture pg) : OrderTestBase(pg)
{
    // ออเดอร์สร้างตอน 00:00Z หมดอายุ 00:30Z
    private static FakeTimeProvider At(int minute, int second = 0) =>
        new(new DateTimeOffset(2026, 11, 15, 0, minute, second, TimeSpan.Zero));

    private readonly FakeSlipVerifier _verifier = new();
    private readonly InMemorySlipStorage _storage = new();

    private PaymentService Service(AppDbContext db, TimeProvider time) =>
        new(db, new OrderPaymentService(db), _verifier, _storage, time, Options.Create(new PaymentOptions()));

    private async Task<SlipSubmission> Submit(Guid orderId, SlipImage image, TimeProvider? time = null)
    {
        await using var db = Pg.CreateDbContext();
        return await Service(db, time ?? At(5)).SubmitSlipAsync(orderId, image, default);
    }

    private async Task<AppException> SubmitFails(Guid orderId, SlipImage image, TimeProvider? time = null) =>
        await Assert.ThrowsAsync<AppException>(() => Submit(orderId, image, time));

    private async Task<(Order Order, List<Payment> Payments)> Load(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.Include(o => o.Registrations).SingleAsync(o => o.Id == orderId);
        return (order, await db.Payments.Where(p => p.OrderId == orderId).OrderBy(p => p.Id).ToListAsync());
    }

    [Fact]
    public async Task Verified_slip_pays_order_and_issues_bib()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount, "TR-1"));

        var r = await Submit(created.OrderId, Slips.Image(1));

        Assert.Equal((PaymentStatus.Verified, OrderStatus.Paid), (r.Status, r.OrderStatus));
        var (order, payments) = await Load(created.OrderId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal("B0001", order.Registrations.Single().BibNumber);
        var p = payments.Single();
        Assert.Equal(("TR-1", (decimal?)created.PayableAmount, PaymentProvider.SlipOk), (p.SlipTransRef, p.PaidAmount, p.Provider));
        Assert.NotNull(p.VerifiedAt);
        Assert.Null(p.NextCheckAt);
        Assert.Equal($"slips/{Ev.Id}/{created.OrderId}/{p.Id}.png", p.SlipObjectKey);
        Assert.True(_storage.Objects.ContainsKey(p.SlipObjectKey!));
    }

    [Fact]
    public async Task Rejected_slip_throws_422_keeps_order_pending_and_gives_grace_time()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Rejected(PaymentRejectReason.WrongReceiver, "{}"));

        var ex = await SubmitFails(created.OrderId, Slips.Image(1), At(29));

        Assert.Equal((422, "SLIP_REJECTED"), (ex.StatusCode, ex.Code));
        var (order, payments) = await Load(created.OrderId);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(new DateTime(2026, 11, 15, 0, 39, 0, DateTimeKind.Utc), order.ExpiresAt);   // 00:29 + 10 นาที
        Assert.Equal((PaymentStatus.Rejected, PaymentRejectReason.WrongReceiver), (payments.Single().Status, payments.Single().RejectReason));

        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));   // อัปโหลดใหม่ได้
        Assert.Equal(PaymentStatus.Verified, (await Submit(created.OrderId, Slips.Image(2), At(31))).Status);
    }

    [Fact]
    public async Task Early_rejected_slip_keeps_the_original_deadline()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Rejected(PaymentRejectReason.InvalidSlip, "{}"));

        await SubmitFails(created.OrderId, Slips.Image(1), At(1));

        // grace 00:01 + 10 = 00:11 สั้นกว่ากำหนดเดิม 00:30 → ต้องคงเวลาเดิม
        Assert.Equal(created.ExpiresAt, (await Load(created.OrderId)).Order.ExpiresAt);
    }

    [Fact]
    public async Task Verified_amount_that_differs_is_rejected()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount + 1));
        var ex = await SubmitFails(created.OrderId, Slips.Image(1));
        Assert.Contains(PaymentRejectReason.AmountMismatch, System.Text.Json.JsonSerializer.Serialize(ex.Details));
    }

    [Fact]
    public async Task Same_image_twice_is_duplicate_without_calling_verifier()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Rejected(PaymentRejectReason.InvalidSlip, "{}"));
        var image = Slips.Image(1);
        await SubmitFails(created.OrderId, image);

        var ex = await SubmitFails(created.OrderId, image);
        Assert.Equal("SLIP_DUPLICATE", ex.Code);
        Assert.Equal(1, _verifier.Calls);
    }

    [Fact]
    public async Task Trans_ref_used_by_another_order_is_rejected()
    {
        var first = await CreateOrderOk();
        var second = await CreateOrderOk();
        _verifier.Enqueue(FakeSlipVerifier.Ok(first.PayableAmount, "TR-SAME"), FakeSlipVerifier.Ok(second.PayableAmount, "TR-SAME"));
        await Submit(first.OrderId, Slips.Image(1));

        var ex = await SubmitFails(second.OrderId, Slips.Image(2));
        Assert.Contains(PaymentRejectReason.DuplicateSlip, System.Text.Json.JsonSerializer.Serialize(ex.Details));
    }

    [Fact]
    public async Task Second_upload_while_verifying_is_rejected_as_in_progress()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Retry(TimeSpan.FromMinutes(8), "{}"));
        var r = await Submit(created.OrderId, Slips.Image(1));
        Assert.Equal(PaymentStatus.Verifying, r.Status);
        Assert.Equal(new DateTime(2026, 11, 15, 0, 13, 0, DateTimeKind.Utc), r.NextCheckAt);   // 00:05 + 8

        Assert.Equal("PAYMENT_IN_PROGRESS", (await SubmitFails(created.OrderId, Slips.Image(2))).Code);
    }

    [Fact]
    public async Task Verifying_holds_the_order_past_its_original_expiry()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Retry(TimeSpan.FromMinutes(8), "{}"));
        await Submit(created.OrderId, Slips.Image(1), At(29));

        await using (var db = Pg.CreateDbContext())
            await new OrderExpiryService(db, At(31)).ExpireDueOrdersAsync();

        var (order, _) = await Load(created.OrderId);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(new DateTime(2026, 11, 15, 1, 59, 0, DateTimeKind.Utc), order.ExpiresAt);   // 00:29 + 90
    }

    [Fact]
    public async Task Valid_slip_for_expired_order_needs_review_without_reserving_again()
    {
        var created = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
            await new OrderExpiryService(db, At(31)).ExpireDueOrdersAsync();
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount, "TR-LATE"));

        var r = await Submit(created.OrderId, Slips.Image(1), At(40));

        Assert.Equal((PaymentStatus.NeedsReview, PaymentRejectReason.OrderExpired), (r.Status, r.Reason));
        var (order, payments) = await Load(created.OrderId);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal("TR-LATE", payments.Single().SlipTransRef);
        await using var check = Pg.CreateDbContext();
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
    }

    [Fact]
    public async Task Unknown_duplicate_needs_review()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Duplicate("TR-UNKNOWN", "{}"));
        var r = await Submit(created.OrderId, Slips.Image(1));
        Assert.Equal((PaymentStatus.NeedsReview, PaymentRejectReason.DuplicateUnknown), (r.Status, r.Reason));
    }

    [Fact]
    public async Task Storage_failure_leaves_no_payment_behind()
    {
        var created = await CreateOrderOk();
        _storage.FailPut = true;
        await Assert.ThrowsAsync<IOException>(() => Submit(created.OrderId, Slips.Image(1)));
        Assert.Empty((await Load(created.OrderId)).Payments);
        Assert.Equal(0, _verifier.Calls);
    }

    [Fact]
    public async Task Paid_and_unknown_orders_are_refused()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));
        await Submit(created.OrderId, Slips.Image(1));

        Assert.Equal("ORDER_ALREADY_PAID", (await SubmitFails(created.OrderId, Slips.Image(2))).Code);
        Assert.Equal("ORDER_NOT_FOUND", (await SubmitFails(Guid.NewGuid(), Slips.Image(3))).Code);
    }

    [Fact]
    public async Task Payment_info_has_qr_payload_and_latest_payment()
    {
        var created = await CreateOrderOk();
        await using var db = Pg.CreateDbContext();
        var info = await Service(db, At(5)).GetPaymentInfoAsync(created.OrderId, default);

        Assert.Equal((created.PayableAmount, "SECRET ACCOUNT"), (info.Amount, info.AccountName));
        Assert.Equal(PromptPayPayload.Create("0812345678", created.PayableAmount), info.QrPayload);
        Assert.Null(info.Payment);
    }

    [Fact]
    public async Task Payment_info_refuses_paid_and_expired_orders()
    {
        var paid = await CreateOrderOk();
        _verifier.Enqueue(FakeSlipVerifier.Ok(paid.PayableAmount));
        await Submit(paid.OrderId, Slips.Image(1));
        var expired = await CreateOrderOk();

        await using var db = Pg.CreateDbContext();
        var ex = await Assert.ThrowsAsync<AppException>(() => Service(db, At(5)).GetPaymentInfoAsync(paid.OrderId, default));
        Assert.Equal("ORDER_ALREADY_PAID", ex.Code);
        ex = await Assert.ThrowsAsync<AppException>(() => Service(db, At(31)).GetPaymentInfoAsync(expired.OrderId, default));
        Assert.Equal("ORDER_NOT_PAYABLE", ex.Code);
    }

    [Fact]
    public async Task Slip_racing_expiry_never_leaves_expired_order_with_verified_payment()
    {
        for (var round = 0; round < 10; round++)
        {
            var created = await CreateOrderOk();
            _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));
            await using var payDb = Pg.CreateDbContext();
            await using var expireDb = Pg.CreateDbContext();

            var submit = Task.Run(async () =>
            {
                try { return (await Service(payDb, At(29, 59)).SubmitSlipAsync(created.OrderId, Slips.Image(round), default)).Status; }
                catch (AppException) { return PaymentStatus.Rejected; }
            });
            var expire = Task.Run(() => new OrderExpiryService(expireDb, At(31)).ExpireDueOrdersAsync());
            await Task.WhenAll(submit, expire);

            var (order, payments) = await Load(created.OrderId);
            var verified = payments.Any(p => p.Status == PaymentStatus.Verified);
            Assert.Equal(verified, order.Status == OrderStatus.Paid);
            Assert.False(order.Status == OrderStatus.Expired && verified);
        }

        await using var db = Pg.CreateDbContext();
        var paidCount = await db.Orders.CountAsync(o => o.EventId == Ev.Id && o.Status == OrderStatus.Paid);
        var pendingCount = await db.Orders.CountAsync(o => o.EventId == Ev.Id && o.Status == OrderStatus.Pending);
        Assert.Equal(paidCount + pendingCount, (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
    }
}
