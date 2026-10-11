namespace HuebRun.Domain;

public enum PromoRejection { NotFound, Inactive, NotStarted, Expired, MinOrderNotMet, Exhausted }

public sealed record PriceBreakdown(decimal SubTotal, decimal DiscountAmount, decimal ShippingFee, decimal Total);

/// กติกาคิดราคาออเดอร์ (pure) ใช้ร่วมกันระหว่างตรวจโค้ดและสร้างออเดอร์
public static class OrderPricing
{
    public static decimal SubTotal(IEnumerable<EventDistance> runnerDistances, DateTime utcNow) =>
        runnerDistances.Sum(d => d.GetEffectivePrice(utcNow));

    /// null = ใช้ได้
    public static PromoRejection? CheckPromo(PromoCode? promo, decimal subTotal, DateTime utcNow)
    {
        if (promo is null) return PromoRejection.NotFound;
        if (!promo.IsActive) return PromoRejection.Inactive;
        if (promo.StartsAt is { } starts && utcNow < starts) return PromoRejection.NotStarted;
        if (promo.EndsAt is { } ends && utcNow >= ends) return PromoRejection.Expired;
        if (promo.MinOrderAmount is { } min && subTotal < min) return PromoRejection.MinOrderNotMet;
        if (promo.MaxUses is { } max && promo.UsedCount >= max) return PromoRejection.Exhausted;
        return null;
    }

    public static decimal Discount(PromoCode promo, decimal subTotal)
    {
        var raw = promo.Type == DiscountType.Percent
            ? Math.Round(subTotal * promo.Value / 100m, 2, MidpointRounding.AwayFromZero)
            : promo.Value;
        return Math.Min(raw, subTotal);
    }

    public static PriceBreakdown Calculate(decimal subTotal, decimal discount, decimal shippingFee) =>
        new(subTotal, discount, shippingFee, subTotal - discount + shippingFee);
}
