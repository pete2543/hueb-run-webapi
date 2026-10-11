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
public sealed class PaymentRecheckTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private readonly FakeSlipVerifier _verifier = new();
    private readonly InMemorySlipStorage _storage = new();

    private static FakeTimeProvider At(int minute) => new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute));

    private PaymentService Payments(AppDbContext db, TimeProvider time) =>
        new(db, new OrderPaymentService(db), _verifier, _storage, time, Options.Create(new PaymentOptions()));

    private async Task<int> Recheck(int minute)
    {
        await using var db = Pg.CreateDbContext();
        var time = At(minute);
        return await new PaymentRecheckService(db, Payments(db, time), _verifier, _storage, time).RecheckDueAsync();
    }

    private async Task<SlipSubmission> SubmitRetrying(Guid orderId, int minute = 5)
    {
        _verifier.Enqueue(new SlipCheckResult.Retry(TimeSpan.FromMinutes(8), "{}"));
        await using var db = Pg.CreateDbContext();
        return await Payments(db, At(minute)).SubmitSlipAsync(orderId, Slips.Image(minute), default);
    }

    private async Task<(OrderStatus Order, Payment Payment)> Load(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        return ((await db.Orders.SingleAsync(o => o.Id == orderId)).Status,
            await db.Payments.SingleAsync(p => p.OrderId == orderId));
    }

    [Fact]
    public async Task Delayed_slip_is_verified_on_recheck()
    {
        var created = await CreateOrderOk();
        await SubmitRetrying(created.OrderId);
        Assert.Equal(0, await Recheck(12));                      // ยังไม่ถึง 00:13

        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));
        Assert.Equal(1, await Recheck(13));

        var (order, payment) = await Load(created.OrderId);
        Assert.Equal((OrderStatus.Paid, PaymentStatus.Verified), (order, payment.Status));
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_needs_review()
    {
        var created = await CreateOrderOk();
        await SubmitRetrying(created.OrderId);                  // attempt 1, ต่อไป 00:13
        var minute = 13;
        for (var i = 0; i < 6; i++)                              // คิวว่าง = Unavailable ทุกครั้ง
        {
            await Recheck(minute);
            var p = (await Load(created.OrderId)).Payment;
            if (p.Status != PaymentStatus.Verifying) break;
            minute = (int)(p.NextCheckAt!.Value - TestData.Utc(2026, 11, 15)).TotalMinutes;
        }

        var (order, payment) = await Load(created.OrderId);
        Assert.Equal((PaymentStatus.NeedsReview, PaymentRejectReason.VerifyTimeout), (payment.Status, payment.RejectReason));
        Assert.Equal(OrderStatus.Pending, order);
    }

    [Fact]
    public async Task Abandoned_payment_is_picked_up_after_claim_window()
    {
        // จำลองแอปตายหลังบันทึก Payment แต่ก่อนตรวจ: ใส่แถว Verifying ตรง ๆ
        var created = await CreateOrderOk();
        var bytes = Slips.Png(42);
        await using (var db = Pg.CreateDbContext())
        {
            var p = new Payment
            {
                OrderId = created.OrderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.Verifying,
                ExpectedAmount = created.PayableAmount, SlipHash = Guid.NewGuid().ToString(),
                CreatedAt = TestData.Utc(2026, 11, 15).AddMinutes(5), NextCheckAt = TestData.Utc(2026, 11, 15).AddMinutes(10),
                SlipObjectKey = "slips/abandoned.png",
            };
            db.Payments.Add(p);
            await db.SaveChangesAsync();
        }
        _storage.Objects["slips/abandoned.png"] = bytes;
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));

        Assert.Equal(0, await Recheck(9));
        Assert.Equal(1, await Recheck(10));
        Assert.Equal(OrderStatus.Paid, (await Load(created.OrderId)).Order);
    }

    [Fact]
    public async Task Concurrent_runs_check_each_payment_once()
    {
        var created = await CreateOrderOk();
        await SubmitRetrying(created.OrderId);
        var callsBefore = _verifier.Calls;
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));

        await Task.WhenAll(Recheck(13), Recheck(13), Recheck(13));

        Assert.Equal(callsBefore + 1, _verifier.Calls);
        Assert.Equal(OrderStatus.Paid, (await Load(created.OrderId)).Order);
    }

    [Fact]
    public async Task Missing_image_counts_as_unavailable_attempt()
    {
        var created = await CreateOrderOk();
        await SubmitRetrying(created.OrderId);
        _storage.Objects.Clear();

        await Recheck(13);

        var (_, payment) = await Load(created.OrderId);
        Assert.Equal((PaymentStatus.Verifying, 2), (payment.Status, payment.CheckAttempts));
    }
}
