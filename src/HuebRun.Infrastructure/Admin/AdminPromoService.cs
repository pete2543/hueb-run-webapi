using System.Text.RegularExpressions;
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HuebRun.Infrastructure.Admin;

public sealed record PromoInput(
    string? Code, DiscountType Type, decimal Value, decimal? MinOrderAmount, DateTime? StartsAt, DateTime? EndsAt,
    int? MaxUses, bool? IsActive);

public sealed record PromoInfo(
    int Id, string Code, DiscountType Type, decimal Value, decimal? MinOrderAmount, DateTime? StartsAt, DateTime? EndsAt,
    int? MaxUses, int UsedCount, bool IsActive);

/// โค้ดส่วนลด: ไม่ลบ ปิดด้วย IsActive; ลด MaxUses ต่ำกว่า UsedCount ไม่ได้; แก้ได้มีผลกับออเดอร์ใหม่เท่านั้น
public sealed partial class AdminPromoService(AppDbContext db, AuditWriter audit, ICurrentAdmin admin)
{
    [GeneratedRegex("^[A-Z0-9_-]{3,30}$")] private static partial Regex CodeRegex();

    public async Task<IReadOnlyList<PromoInfo>> ListAsync(int eventId, CancellationToken ct)
    {
        await EnsureEventAsync(eventId, ct);
        return await ReadAsync(eventId, ct);
    }

    public async Task<PromoInfo> CreateAsync(int eventId, PromoInput input, CancellationToken ct)
    {
        await EnsureEventAsync(eventId, ct);
        var code = input.Code?.Trim().ToUpperInvariant() ?? "";
        var errors = Validate(input);
        if (!CodeRegex().IsMatch(code)) errors["code"] = ["A-Z 0-9 _ - ยาว 3–30 ตัว"];
        if (errors.Count > 0) throw OrderErrors.Validation(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // ลูกค้ากรอกโค้ดแบบไม่สนตัวพิมพ์ ต้องกันซ้ำแบบไม่สนตัวพิมพ์ด้วย (unique index ของ DB สนตัวพิมพ์)
        if (await db.PromoCodes.AnyAsync(p => p.EventId == eventId && p.Code.ToUpper() == code, ct)) throw AdminErrors.PromoCodeTaken(code);
        var promo = new PromoCode
        {
            EventId = eventId, Code = code, Type = input.Type, Value = input.Value, MinOrderAmount = input.MinOrderAmount,
            StartsAt = input.StartsAt?.ToUniversalTime(), EndsAt = input.EndsAt?.ToUniversalTime(), MaxUses = input.MaxUses,
            IsActive = input.IsActive ?? true,
        };
        db.PromoCodes.Add(promo);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw AdminErrors.PromoCodeTaken(code);
        }
        var info = ToInfo(promo);
        audit.Add("promo.create", "PromoCode", promo.Id.ToString(), newValues: info);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return info;
    }

    /// แก้ได้ทุกฟิลด์ยกเว้นตัวโค้ด (ลูกค้าอาจได้รับโค้ดนี้ไปแล้ว) — ส่งค่าครบทุกฟิลด์
    public async Task<PromoInfo> UpdateAsync(int eventId, int promoId, PromoInput input, CancellationToken ct)
    {
        await EnsureEventAsync(eventId, ct);
        var errors = Validate(input);
        if (errors.Count > 0) throw OrderErrors.Validation(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var before = await db.PromoCodes.AsNoTracking().SingleOrDefaultAsync(p => p.Id == promoId && p.EventId == eventId, ct)
                     ?? throw AdminErrors.PromoNotFound(promoId);
        var (startsAt, endsAt) = (input.StartsAt?.ToUniversalTime(), input.EndsAt?.ToUniversalTime());
        var isActive = input.IsActive ?? before.IsActive;

        // เงื่อนไขใน WHERE: ลดเพดานต่ำกว่าที่ใช้ไปแล้วไม่ได้ แม้มีคนใช้โค้ดพร้อมกัน
        var updated = await db.PromoCodes
            .Where(p => p.Id == promoId && (input.MaxUses == null || p.UsedCount <= input.MaxUses))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Type, input.Type).SetProperty(p => p.Value, input.Value)
                .SetProperty(p => p.MinOrderAmount, input.MinOrderAmount).SetProperty(p => p.StartsAt, startsAt)
                .SetProperty(p => p.EndsAt, endsAt).SetProperty(p => p.MaxUses, input.MaxUses)
                .SetProperty(p => p.IsActive, isActive), ct);
        if (updated == 0)
        {
            var used = await db.PromoCodes.AsNoTracking().Where(p => p.Id == promoId).Select(p => p.UsedCount).SingleAsync(ct);
            throw AdminErrors.MaxUsesBelowUsed(input.MaxUses!.Value, used);
        }

        var after = ToInfo(await db.PromoCodes.AsNoTracking().SingleAsync(p => p.Id == promoId, ct));
        audit.Add("promo.update", "PromoCode", promoId.ToString(), ToInfo(before), after);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return after;
    }

    private static Dictionary<string, string[]> Validate(PromoInput i)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(i.Type)) errors["type"] = ["Percent หรือ Fixed"];
        else if (i.Type == DiscountType.Percent ? i.Value is <= 0 or > 100 : i.Value <= 0)
            errors["value"] = [i.Type == DiscountType.Percent ? "ต้องมากกว่า 0 และไม่เกิน 100" : "ต้องมากกว่า 0"];
        if (i.MinOrderAmount is < 0) errors["minOrderAmount"] = ["ต้องไม่ติดลบ"];
        if (i.MaxUses is < 1) errors["maxUses"] = ["ต้องอย่างน้อย 1 (เว้นว่าง = ไม่จำกัด)"];
        if (i.StartsAt is { Kind: DateTimeKind.Unspecified }) errors["startsAt"] = ["ต้องระบุเขตเวลา (เช่น ลงท้าย Z)"];
        if (i.EndsAt is { Kind: DateTimeKind.Unspecified }) errors["endsAt"] = ["ต้องระบุเขตเวลา (เช่น ลงท้าย Z)"];
        if (errors.Count == 0 && i.StartsAt is { } s && i.EndsAt is { } e && s.ToUniversalTime() >= e.ToUniversalTime())
            errors["endsAt"] = ["ต้องหลังวันเริ่ม"];
        return errors;
    }

    private async Task EnsureEventAsync(int eventId, CancellationToken ct)
    {
        admin.EnsureEventAccess(eventId);
        if (!await db.Events.AnyAsync(e => e.Id == eventId, ct)) throw AdminErrors.EventNotFound(eventId);
    }

    private async Task<List<PromoInfo>> ReadAsync(int eventId, CancellationToken ct) =>
        (await db.PromoCodes.AsNoTracking().Where(p => p.EventId == eventId).OrderBy(p => p.Code).ToListAsync(ct)).Select(ToInfo).ToList();

    private static PromoInfo ToInfo(PromoCode p) =>
        new(p.Id, p.Code, p.Type, p.Value, p.MinOrderAmount, p.StartsAt, p.EndsAt, p.MaxUses, p.UsedCount, p.IsActive);
}
