using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class OrderExpiryTests(PostgresFixture pg) : OrderTestBase(pg)
{
    // ออเดอร์สร้างตอน 2026-11-15T00:00Z หมดอายุ 00:30Z
    private static readonly FakeTimeProvider AfterExpiry = new(new DateTimeOffset(2026, 11, 15, 0, 31, 0, TimeSpan.Zero));
    private static readonly FakeTimeProvider BeforeExpiry = new(new DateTimeOffset(2026, 11, 15, 0, 29, 0, TimeSpan.Zero));

    private async Task<int> RunExpiry(TimeProvider time)
    {
        await using var db = Pg.CreateDbContext();
        return await new OrderExpiryService(db, time).ExpireDueOrdersAsync();
    }

    private async Task<(OrderStatus Status, int FiveK, int TenK, int M, int Promo)> Snapshot(Guid orderId, int promoId = 0)
    {
        await using var db = Pg.CreateDbContext();
        return (
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status,
            (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount,
            (await db.EventDistances.SingleAsync(d => d.Id == TenK.Id)).ReservedCount,
            (await db.EventShirtSizes.SingleAsync(s => s.Id == M.Id)).ReservedCount,
            promoId == 0 ? 0 : (await db.PromoCodes.SingleAsync(p => p.Id == promoId)).UsedCount);
    }

    [Fact]
    public async Task Expires_due_order_and_releases_seats_shirts_and_promo()
    {
        var promo = await AddPromoAsync();
        var created = await CreateOrderOk(Request(Runner(FiveK), Runner(FiveK), Runner(TenK)) with { PromoCode = "EARLY10" });
        Assert.Equal((OrderStatus.Pending, 2, 1, 3, 1), await Snapshot(created.OrderId, promo.Id));

        Assert.True(await RunExpiry(AfterExpiry) >= 1);

        Assert.Equal((OrderStatus.Expired, 0, 0, 0, 0), await Snapshot(created.OrderId, promo.Id));
    }

    [Fact]
    public async Task Running_twice_does_not_release_twice()
    {
        var keep = await CreateOrderOk();                       // จะจ่าย → ต้องคงการจองไว้
        var drop = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
            await new OrderPaymentService(db).MarkPaidAsync(keep.OrderId, TestData.Utc(2026, 11, 15));

        await RunExpiry(AfterExpiry);
        await RunExpiry(AfterExpiry);

        Assert.Equal((OrderStatus.Expired, 1, 0, 1, 0), await Snapshot(drop.OrderId));
        Assert.Equal(OrderStatus.Paid, (await Snapshot(keep.OrderId)).Status);
    }

    [Fact]
    public async Task Order_not_yet_due_is_untouched()
    {
        var created = await CreateOrderOk();
        await RunExpiry(BeforeExpiry);
        Assert.Equal((OrderStatus.Pending, 1, 0, 1, 0), await Snapshot(created.OrderId));
    }

    [Fact]
    public async Task Payment_racing_expiry_has_exactly_one_winner_and_consistent_counts()
    {
        for (var round = 0; round < 10; round++)
        {
            var created = await CreateOrderOk();
            await using var payDb = Pg.CreateDbContext();
            await using var expireDb = Pg.CreateDbContext();

            var pay = Task.Run(async () =>
            {
                try { await new OrderPaymentService(payDb).MarkPaidAsync(created.OrderId, TestData.Utc(2026, 11, 15)); return true; }
                catch (InvalidOperationException) { return false; }
            });
            var expire = Task.Run(() => new OrderExpiryService(expireDb, AfterExpiry).ExpireDueOrdersAsync());
            var paid = await pay;
            await expire;

            var snap = await Snapshot(created.OrderId);
            Assert.Equal(paid ? OrderStatus.Paid : OrderStatus.Expired, snap.Status);
        }

        // ออเดอร์ที่จ่ายแล้วเท่านั้นที่ยังถือที่นั่ง
        await using var db = Pg.CreateDbContext();
        var paidCount = await db.Orders.CountAsync(o => o.EventId == Ev.Id && o.Status == OrderStatus.Paid);
        Assert.Equal(paidCount, (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
        Assert.Equal(paidCount, (await db.EventShirtSizes.SingleAsync(s => s.Id == M.Id)).ReservedCount);
    }

    [Fact]
    public async Task Backlog_larger_than_one_batch_is_cleared_in_a_single_run()
    {
        var orders = new List<CreatedOrder>();
        for (var i = 0; i < 5; i++) orders.Add(await CreateOrderOk());

        await using (var db = Pg.CreateDbContext())
            await new OrderExpiryService(db, AfterExpiry).ExpireDueOrdersAsync(batchSize: 2);

        foreach (var o in orders) Assert.Equal(OrderStatus.Expired, (await Snapshot(o.OrderId)).Status);
        Assert.Equal(0, (await Snapshot(orders[0].OrderId)).FiveK);
    }

    [Fact]
    public async Task One_failing_order_does_not_block_the_others()
    {
        var broken = await CreateOrderOk(Request(Runner(TenK)));
        var healthy = await CreateOrderOk(Request(Runner(FiveK)));
        // จำลองข้อมูลเพี้ยน: คืนที่ของ broken แล้วจะติดลบ → ชน check constraint
        await using (var db = Pg.CreateDbContext())
            await db.EventDistances.Where(d => d.Id == TenK.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ReservedCount, 0));

        await RunExpiry(AfterExpiry);

        Assert.Equal(OrderStatus.Pending, (await Snapshot(broken.OrderId)).Status);   // rollback ทั้งออเดอร์
        Assert.Equal((OrderStatus.Expired, 0), ((await Snapshot(healthy.OrderId)).Status, (await Snapshot(healthy.OrderId)).FiveK));

        // ซ่อมข้อมูลให้ test อื่นที่รัน expiry ภายหลังไม่ต้องเจอออเดอร์นี้
        await using (var db = Pg.CreateDbContext())
            await db.Orders.Where(o => o.Id == broken.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Cancelled));
    }
}
