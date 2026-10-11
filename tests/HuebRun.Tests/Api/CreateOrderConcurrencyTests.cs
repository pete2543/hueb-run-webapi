using System.Net;
using System.Net.Http.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class CreateOrderConcurrencyTests(PostgresFixture pg) : OrderTestBase(pg)
{
    protected override void ConfigureEvent(Event e) => e.Distances.Single(d => d.Name == "10K").Quota = 5;

    [Fact]
    public async Task Twenty_concurrent_orders_for_five_seats_create_exactly_five()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => PostOrder(Request(Runner(TenK, M, $"r{i}")))));

        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        var conflicts = results.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();
        Assert.Equal(15, conflicts.Count);
        foreach (var c in conflicts) await AssertError(c, HttpStatusCode.Conflict, "QUOTA_FULL");

        await using var db = Pg.CreateDbContext();
        Assert.Equal(5, (await db.EventDistances.SingleAsync(d => d.Id == TenK.Id)).ReservedCount);
        Assert.Equal(5, (await db.EventShirtSizes.SingleAsync(s => s.Id == M.Id)).ReservedCount);
        Assert.Equal(5, await db.Orders.CountAsync(o => o.EventId == Ev.Id));
    }

    [Fact]
    public async Task Concurrent_orders_with_same_total_all_succeed_with_distinct_payable_amounts()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => PostOrder(Request(Runner(FiveK, L, $"p{i}")))));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var created = await Task.WhenAll(results.Select(r => r.Content.ReadFromJsonAsync<CreatedOrder>(Json)));
        var payables = created.Select(c => c!.PayableAmount).ToList();
        Assert.Equal(40, payables.Distinct().Count());
        Assert.All(payables, p => Assert.InRange(p, 400.01m, 400.99m));
    }

    [Fact]
    public async Task Last_promo_use_goes_to_exactly_one_order()
    {
        await AddPromoAsync(p => p.MaxUses = 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(i => PostOrder(Request(Runner(FiveK, M, $"x{i}")) with { PromoCode = "EARLY10" })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var r in results.Where(r => r.StatusCode != HttpStatusCode.Created))
            await AssertError(r, HttpStatusCode.Conflict, "PROMO_EXHAUSTED");
        await using var db = Pg.CreateDbContext();
        Assert.Equal(1, (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
    }

    [Fact]
    public async Task Two_orders_with_same_total_get_different_payable_amounts()
    {
        var a = await CreateOrderOk();
        var b = await CreateOrderOk();
        Assert.Equal(400.01m, a.PayableAmount);
        Assert.Equal(400.02m, b.PayableAmount);
    }

    [Fact]
    public async Task Non_pending_order_frees_its_payable_amount()
    {
        var a = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
            await db.Orders.Where(o => o.Id == a.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired));
        var b = await CreateOrderOk();
        Assert.Equal(a.PayableAmount, b.PayableAmount);
    }

    [Fact]
    public async Task All_99_satang_slots_taken_is_conflict_and_rolls_back_reservation()
    {
        await using (var db = Pg.CreateDbContext())
        {
            for (var i = 1; i <= 99; i++)
                db.Orders.Add(new Order
                {
                    Id = Guid.NewGuid(), OrderNo = $"FILL-{Ev.Id}-{i}", EventId = Ev.Id, ContactName = "x", Phone = "0812345678",
                    Status = OrderStatus.Pending, Total = 400, PayableAmount = 400 + i / 100m, ShippingOptionId = Pickup.Id,
                    CreatedAt = TestData.Utc(2026, 11, 15), ExpiresAt = TestData.Utc(2026, 11, 16),
                });
            await db.SaveChangesAsync();
        }

        await AssertError(await PostOrder(Request()), HttpStatusCode.Conflict, "PAYMENT_SLOT_BUSY");
        await using var check = Pg.CreateDbContext();
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
    }
}
