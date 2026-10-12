using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class OrderPaymentServiceTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private static readonly DateTime PaidAt = TestData.Utc(2026, 11, 15);

    private async Task<MarkPaidResult> MarkPaid(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        return await new OrderPaymentService(db).MarkPaidAsync(orderId, PaidAt);
    }

    private async Task<List<string?>> Bibs(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        return await db.Registrations.Where(r => r.OrderId == orderId).OrderBy(r => r.Id).Select(r => r.BibNumber).ToListAsync();
    }

    [Fact]
    public async Task Marks_paid_and_issues_sequential_bibs_per_distance()
    {
        var first = await CreateOrderOk(Request(Runner(FiveK), Runner(TenK), Runner(FiveK)));
        var second = await CreateOrderOk(Request(Runner(FiveK)));

        Assert.False((await MarkPaid(first.OrderId)).AlreadyPaid);
        await MarkPaid(second.OrderId);

        Assert.Equal(["B0001", "C0001", "B0002"], await Bibs(first.OrderId));
        Assert.Equal(["B0003"], await Bibs(second.OrderId));
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.SingleAsync(o => o.Id == first.OrderId);
        Assert.Equal((OrderStatus.Paid, (DateTime?)PaidAt), (order.Status, order.PaidAt));
        Assert.Equal(3, (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);   // ยังนับเป็นจอง
    }

    [Fact]
    public async Task Marking_twice_is_idempotent()
    {
        var created = await CreateOrderOk();
        await MarkPaid(created.OrderId);
        Assert.True((await MarkPaid(created.OrderId)).AlreadyPaid);
        Assert.Equal(["B0001"], await Bibs(created.OrderId));
    }

    [Fact]
    public async Task Concurrent_payments_never_share_a_bib()
    {
        var orders = new List<CreatedOrder>();
        for (var i = 0; i < 8; i++) orders.Add(await CreateOrderOk(Request(Runner(FiveK), Runner(FiveK))));

        await Task.WhenAll(orders.Select(o => MarkPaid(o.OrderId)));

        await using var db = Pg.CreateDbContext();
        var bibs = await db.Registrations.Where(r => r.EventId == Ev.Id).Select(r => r.BibNumber!).ToListAsync();
        Assert.Equal(16, bibs.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 16).Select(n => $"B{n:D4}"), bibs.Order());
    }

    [Fact]
    public async Task Expired_order_cannot_be_marked_paid()
    {
        var created = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
            await db.Orders.Where(o => o.Id == created.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarkPaid(created.OrderId));
    }

    [Fact]
    public async Task Unknown_order_cannot_be_marked_paid() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarkPaid(Guid.NewGuid()));

    [Fact]
    public async Task Joins_an_outer_transaction_and_rolls_back_with_it()
    {
        var created = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await new OrderPaymentService(db).MarkPaidAsync(created.OrderId, PaidAt);
            Assert.Same(tx, db.Database.CurrentTransaction);   // ไม่ได้ commit/ปิด transaction ของคนเรียก
            await tx.RollbackAsync();
        }

        Assert.Equal([null], await Bibs(created.OrderId));
        await using var check = Pg.CreateDbContext();
        Assert.Equal(OrderStatus.Pending, (await check.Orders.SingleAsync(o => o.Id == created.OrderId)).Status);
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).LastBibNumber);
    }
}
