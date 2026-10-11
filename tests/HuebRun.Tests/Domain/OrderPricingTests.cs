using HuebRun.Domain;

namespace HuebRun.Tests.Domain;

public class OrderPricingTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

    private static PromoCode Promo(Action<PromoCode>? configure = null)
    {
        var p = new PromoCode { Code = "EARLY10", Type = DiscountType.Percent, Value = 10, IsActive = true };
        configure?.Invoke(p);
        return p;
    }

    [Fact]
    public void Subtotal_sums_effective_price_per_runner()
    {
        var fiveK = new EventDistance { Price = 500, EarlyBirdPrice = 400, EarlyBirdEndsAt = Now.AddDays(1) };
        var tenK = new EventDistance { Price = 700 };
        Assert.Equal(1500m, OrderPricing.SubTotal([fiveK, fiveK, tenK], Now));
    }

    [Fact]
    public void Percent_discount_rounds_half_away_from_zero()
    {
        // 333.33 × 15% = 49.9995 → 50.00
        Assert.Equal(50.00m, OrderPricing.Discount(Promo(p => p.Value = 15), 333.33m));
    }

    [Fact]
    public void Fixed_discount_never_exceeds_subtotal()
    {
        Assert.Equal(300m, OrderPricing.Discount(Promo(p => { p.Type = DiscountType.Fixed; p.Value = 1000; }), 300m));
        Assert.Equal(100m, OrderPricing.Discount(Promo(p => { p.Type = DiscountType.Fixed; p.Value = 100; }), 300m));
    }

    [Fact]
    public void Calculate_total_is_subtotal_minus_discount_plus_shipping()
    {
        Assert.Equal(new PriceBreakdown(1100m, 110m, 60m, 1050m), OrderPricing.Calculate(1100m, 110m, 60m));
    }

    [Fact]
    public void Valid_promo_has_no_rejection() =>
        Assert.Null(OrderPricing.CheckPromo(Promo(p => { p.StartsAt = Now; p.EndsAt = Now.AddDays(1); p.MinOrderAmount = 500; p.MaxUses = 2; p.UsedCount = 1; }), 500m, Now));

    [Fact]
    public void Promo_rejections()
    {
        Assert.Equal(PromoRejection.NotFound, OrderPricing.CheckPromo(null, 500m, Now));
        Assert.Equal(PromoRejection.Inactive, OrderPricing.CheckPromo(Promo(p => p.IsActive = false), 500m, Now));
        Assert.Equal(PromoRejection.NotStarted, OrderPricing.CheckPromo(Promo(p => p.StartsAt = Now.AddSeconds(1)), 500m, Now));
        Assert.Equal(PromoRejection.Expired, OrderPricing.CheckPromo(Promo(p => p.EndsAt = Now), 500m, Now));
        Assert.Equal(PromoRejection.MinOrderNotMet, OrderPricing.CheckPromo(Promo(p => p.MinOrderAmount = 500.01m), 500m, Now));
        Assert.Equal(PromoRejection.Exhausted, OrderPricing.CheckPromo(Promo(p => { p.MaxUses = 3; p.UsedCount = 3; }), 500m, Now));
    }

    [Fact]
    public void Pending_order_past_expiry_is_effectively_expired()
    {
        var order = new Order { Status = OrderStatus.Pending, ExpiresAt = Now };
        Assert.Equal(OrderStatus.Pending, order.GetEffectiveStatus(Now.AddSeconds(-1)));
        Assert.Equal(OrderStatus.Expired, order.GetEffectiveStatus(Now));
    }

    [Fact]
    public void Paid_order_never_becomes_expired()
    {
        var order = new Order { Status = OrderStatus.Paid, ExpiresAt = Now };
        Assert.Equal(OrderStatus.Paid, order.GetEffectiveStatus(Now.AddDays(1)));
    }
}
