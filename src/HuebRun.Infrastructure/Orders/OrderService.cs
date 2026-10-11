using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Orders;

public sealed class OrderService(AppDbContext db, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// ตรวจโค้ดส่วนลดกับยอดที่เซิร์ฟเวอร์คำนวณเอง (ยังไม่นับการใช้)
    public async Task<PromoQuote> ValidatePromoAsync(string slug, ValidatePromoRequest request, CancellationToken ct)
    {
        var ev = await LoadPublishedEventAsync(slug, ct);
        var now = Now;
        var code = request.Code?.Trim() ?? "";
        var ids = request.DistanceIds ?? [];
        var distances = await db.EventDistances.AsNoTracking()
            .Where(d => d.EventId == ev.Id)
            .ToDictionaryAsync(d => d.Id, ct);

        var errors = new Dictionary<string, string[]>();
        if (code.Length == 0) errors["code"] = ["กรุณากรอกโค้ดส่วนลด"];
        if (ids.Count is < 1 or > OrderValidator.MaxRunners)
            errors["distanceIds"] = [$"ต้องมี 1–{OrderValidator.MaxRunners} รายการ"];
        else if (ids.Any(id => !distances.ContainsKey(id)))
            errors["distanceIds"] = ["มีระยะที่ไม่ใช่ของงานนี้"];
        if (errors.Count > 0) throw OrderErrors.Validation(errors);

        var subTotal = OrderPricing.SubTotal(ids.Select(id => distances[id]), now);
        var promo = await FindPromoAsync(ev.Id, code, ct);
        if (OrderPricing.CheckPromo(promo, subTotal, now) is { } rejection) throw OrderErrors.Promo(rejection);
        return new PromoQuote(promo!.Code, subTotal, OrderPricing.Discount(promo, subTotal));
    }

    private async Task<Event> LoadPublishedEventAsync(string slug, CancellationToken ct)
    {
        var normalized = slug.ToLowerInvariant();
        return await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Slug == normalized && e.IsPublished, ct)
            ?? throw OrderErrors.EventNotFound(slug);
    }

    private Task<PromoCode?> FindPromoAsync(int eventId, string code, CancellationToken ct)
    {
        var upper = code.ToUpperInvariant();
        return db.PromoCodes.AsNoTracking().FirstOrDefaultAsync(p => p.EventId == eventId && p.Code.ToUpper() == upper, ct);
    }
}
