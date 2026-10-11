using HuebRun.Domain;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class FreeOrderTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private Task UseFullDiscount() => AddPromoAsync(p => { p.Code = "FREE100"; p.Value = 100; });

    [Fact]
    public async Task Zero_total_order_is_paid_immediately_with_bib_and_free_payment()
    {
        await UseFullDiscount();
        var created = await CreateOrderOk(Request() with { PromoCode = "FREE100" });

        Assert.Equal(0m, created.PayableAmount);
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.Include(o => o.Registrations).Include(o => o.Payments).SingleAsync(o => o.Id == created.OrderId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal("B0001", order.Registrations.Single().BibNumber);
        var payment = order.Payments.Single();
        Assert.Equal((PaymentProvider.Free, PaymentStatus.Verified, 0m), (payment.Provider, payment.Status, payment.ExpectedAmount));
    }

    [Fact]
    public async Task Concurrent_free_orders_all_succeed()
    {
        await UseFullDiscount();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostOrder(Request() with { PromoCode = "FREE100" })));
        Assert.All(results, r => Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode));
    }

    [Fact]
    public async Task Paid_shipping_keeps_normal_flow()
    {
        await UseFullDiscount();
        var created = await CreateOrderOk(Request() with
        {
            PromoCode = "FREE100", ShippingOptionId = Post.Id,
            ShippingAddress = await TestAddressAsync(),
        });
        Assert.Equal(60.01m, created.PayableAmount);
    }

    private async Task<HuebRun.Infrastructure.Orders.AddressInput> TestAddressAsync()
    {
        await using var db = Pg.CreateDbContext();
        var sub = await db.Subdistricts.OrderBy(s => s.Id).FirstAsync();
        return new("สมชาย", "0812345678", "1 ถนนสุขุมวิท", sub.Id, sub.ZipCode);
    }
}
