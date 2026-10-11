using System.Buffers.Text;
using System.Security.Cryptography;
using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    private const int MaxCreateAttempts = 3;

    /// สร้างออเดอร์ + จองโควตา/เสื้อ/โค้ดใน transaction เดียว
    public async Task<CreatedOrder> CreateOrderAsync(string slug, CreateOrderRequest request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryCreateOrderAsync(slug, request, ct);
            }
            catch (DbUpdateException ex) when (IsPayableConflict(ex))
            {
                // ด่านสำรอง: ปกติ advisory lock กันไว้แล้ว
                db.ChangeTracker.Clear();
                if (attempt >= MaxCreateAttempts) throw OrderErrors.PaymentSlotBusy();
            }
        }
    }

    private async Task<CreatedOrder> TryCreateOrderAsync(string slug, CreateOrderRequest request, CancellationToken ct)
    {
        var now = Now;
        var ev = await LoadPublishedEventAsync(slug, ct);
        if (!ev.IsRegistrationOpen(now)) throw OrderErrors.RegistrationClosed();

        var data = await LoadReferenceDataAsync(ev.Id, request, ct);
        var errors = OrderValidator.Validate(request, data, now);
        if (errors.Count > 0) throw OrderErrors.Validation(errors);

        var runners = request.Runners!;
        var subTotal = OrderPricing.SubTotal(runners.Select(r => data.Distances[r.DistanceId!.Value]), now);
        PromoCode? promo = null;
        var discount = 0m;
        if (OrderValidator.Clean(request.PromoCode) is { } code)
        {
            promo = await FindPromoAsync(ev.Id, code, ct);
            if (OrderPricing.CheckPromo(promo, subTotal, now) is { } rejection) throw OrderErrors.Promo(rejection);
            discount = OrderPricing.Discount(promo!, subTotal);
        }
        var shipping = data.ShippingOptions[request.ShippingOptionId!.Value];
        var price = OrderPricing.Calculate(subTotal, discount, shipping.Fee);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // จองแบบ atomic เรียงตาม Id ลดโอกาส deadlock; ล้มที่ใดก็ rollback ทั้งหมด
        foreach (var group in runners.GroupBy(r => r.DistanceId!.Value).OrderBy(g => g.Key))
        {
            var (id, n) = (group.Key, group.Count());
            var reserved = await db.EventDistances
                .Where(d => d.Id == id && d.ReservedCount + n <= d.Quota)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReservedCount, d => d.ReservedCount + n), ct);
            if (reserved == 0) throw OrderErrors.QuotaFull(id, data.Distances[id].Name);
        }
        foreach (var group in runners.GroupBy(r => r.ShirtSizeId!.Value).OrderBy(g => g.Key))
        {
            var (id, n) = (group.Key, group.Count());
            var reserved = await db.EventShirtSizes
                .Where(s => s.Id == id && s.ReservedCount + n <= s.Stock)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedCount, x => x.ReservedCount + n), ct);
            if (reserved == 0) throw OrderErrors.ShirtOutOfStock(id, data.ShirtSizes[id].Size);
        }
        if (promo is not null)
        {
            var promoId = promo.Id;
            var used = await db.PromoCodes
                .Where(p => p.Id == promoId && (p.MaxUses == null || p.UsedCount < p.MaxUses))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UsedCount, p => p.UsedCount + 1), ct);
            if (used == 0) throw OrderErrors.Promo(PromoRejection.Exhausted);
        }

        // ยอด 0 (โค้ดฟรี + รับเอง) ไม่ต้องโอน → ไม่ใช้เศษสตางค์ และจ่ายแล้วทันที
        var isFree = price.Total == 0;
        var payable = isFree ? 0m : await PickPayableAmountAsync(ev.Id, price.Total, ct);
        var sequence = await db.Database.SqlQuery<long>($"SELECT nextval('order_no_seq') AS \"Value\"").SingleAsync(ct);

        var order = BuildOrder(ev, request, data, promo, shipping, price, payable, sequence, now);
        if (isFree)
            order.Payments.Add(new Payment
            {
                Provider = PaymentProvider.Free, Status = PaymentStatus.Verified,
                ExpectedAmount = 0, PaidAmount = 0, CreatedAt = now, VerifiedAt = now,
            });
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        if (isFree) await new OrderPaymentService(db).MarkPaidAsync(order.Id, now, ct);
        await tx.CommitAsync(ct);

        return new CreatedOrder(order.Id, order.OrderNo, order.Total, order.PayableAmount, order.ExpiresAt);
    }

    /// ยอด + เศษสตางค์ 0.01–0.99 ค่าน้อยสุดที่ออเดอร์ Pending ของงานนี้ยังไม่ใช้
    /// ถือ advisory lock ต่อ (งาน, ยอด) จนจบ transaction ให้คำขอยอดเดียวกันเลือกทีละคน
    private async Task<decimal> PickPayableAmountAsync(int eventId, decimal total, CancellationToken ct)
    {
        var totalCents = (int)(total * 100);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({eventId}, {totalCents})", ct);

        var low = total + 0.01m;
        var high = total + 0.99m;
        var taken = (await db.Orders
                .Where(o => o.EventId == eventId && o.Status == OrderStatus.Pending
                         && o.PayableAmount >= low && o.PayableAmount <= high)
                .Select(o => o.PayableAmount)
                .ToListAsync(ct))
            .ToHashSet();
        for (var satang = 1; satang <= 99; satang++)
        {
            var candidate = total + satang / 100m;
            if (!taken.Contains(candidate)) return candidate;
        }
        throw OrderErrors.PaymentSlotBusy();
    }

    private async Task<OrderReferenceData> LoadReferenceDataAsync(int eventId, CreateOrderRequest request, CancellationToken ct)
    {
        Subdistrict? subdistrict = null;
        if (request.ShippingAddress?.SubdistrictId is { } subId)
            subdistrict = await db.Subdistricts.AsNoTracking()
                .Include(s => s.District).ThenInclude(d => d.Province)
                .FirstOrDefaultAsync(s => s.Id == subId, ct);

        return new OrderReferenceData
        {
            Distances = await db.EventDistances.AsNoTracking().Where(d => d.EventId == eventId).ToDictionaryAsync(d => d.Id, ct),
            ShirtSizes = await db.EventShirtSizes.AsNoTracking().Where(s => s.EventId == eventId).ToDictionaryAsync(s => s.Id, ct),
            ShippingOptions = await db.ShippingOptions.AsNoTracking().Where(s => s.EventId == eventId && s.IsActive).ToDictionaryAsync(s => s.Id, ct),
            TitleIds = (await db.Titles.Where(x => x.IsActive).Select(x => x.Id).ToListAsync(ct)).ToHashSet(),
            NationalityIds = (await db.Nationalities.Where(x => x.IsActive).Select(x => x.Id).ToListAsync(ct)).ToHashSet(),
            BloodTypeIds = (await db.BloodTypes.Where(x => x.IsActive).Select(x => x.Id).ToListAsync(ct)).ToHashSet(),
            RelationIds = (await db.EmergencyRelations.Where(x => x.IsActive).Select(x => x.Id).ToListAsync(ct)).ToHashSet(),
            ShipSubdistrict = subdistrict,
        };
    }

    private static Order BuildOrder(Event ev, CreateOrderRequest request, OrderReferenceData data, PromoCode? promo,
        ShippingOption shipping, PriceBreakdown price, decimal payable, long sequence, DateTime now)
    {
        var contact = request.Contact!;
        var isPost = shipping.Type == ShippingType.Post;
        var address = isPost ? request.ShippingAddress : null;
        var sub = isPost ? data.ShipSubdistrict : null;

        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNo = $"HR{ev.Year % 100:D2}-{sequence:D6}",
            EventId = ev.Id,
            ContactName = contact.Name!.Trim(),
            Phone = OrderValidator.NormalizePhone(contact.Phone)!,
            Email = OrderValidator.Clean(contact.Email),
            Status = OrderStatus.Pending,
            SubTotal = price.SubTotal,
            DiscountAmount = price.DiscountAmount,
            ShippingFee = price.ShippingFee,
            Total = price.Total,
            PayableAmount = payable,
            PromoCodeId = promo?.Id,
            ShippingOptionId = shipping.Id,
            ShipName = address?.Name?.Trim(),
            ShipPhone = address is null ? null : OrderValidator.NormalizePhone(address.Phone),
            ShipAddress = address?.Line1?.Trim(),
            ShipSubdistrictId = sub?.Id,
            ShipSubdistrictText = sub?.NameTh,
            ShipDistrictText = sub?.District.NameTh,
            ShipProvinceText = sub?.District.Province.NameTh,
            ShipPostalCode = sub?.ZipCode,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(ev.ReservationMinutes),
        };

        foreach (var r in request.Runners!)
        {
            var ec = r.EmergencyContact!;
            order.Registrations.Add(new Registration
            {
                EventId = ev.Id,
                EventDistanceId = r.DistanceId!.Value,
                EventShirtSizeId = r.ShirtSizeId!.Value,
                PricePaid = data.Distances[r.DistanceId.Value].GetEffectivePrice(now),
                TitleId = r.TitleId,
                FirstName = r.FirstName!.Trim(),
                LastName = r.LastName!.Trim(),
                Gender = r.Gender!.Value,
                BirthDate = r.BirthDate!.Value,
                NationalityId = r.NationalityId!.Value,
                BloodTypeId = r.BloodTypeId,
                Phone = OrderValidator.NormalizePhone(r.Phone),
                Email = OrderValidator.Clean(r.Email),
                TeamName = OrderValidator.Clean(r.TeamName),
                EmergencyRelationId = ec.RelationId,
                EmergencyContactName = ec.Name!.Trim(),
                EmergencyContactPhone = OrderValidator.NormalizePhone(ec.Phone)!,
                MedicalNote = OrderValidator.Clean(r.MedicalNote),
                CheckInToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
            });
        }
        return order;
    }

    private static bool IsPayableConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == OrderErrors.PayableIndexName;

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
