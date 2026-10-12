using System.Text.RegularExpressions;
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HuebRun.Infrastructure.Admin;

/// ตั้งค่างานแทนการรัน SQL — กติกา docs/admin-api-plan.md หัวข้อ 4.3
public sealed partial class AdminEventService(AppDbContext db, AuditWriter audit, ICurrentAdmin admin)
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")] private static partial Regex SlugRegex();
    [GeneratedRegex("^[A-Z0-9]{1,5}$")] private static partial Regex PrefixRegex();

    // ---------- Event ----------

    public async Task<AdminEventInfo> GetAsync(int eventId, CancellationToken ct)
    {
        admin.EnsureEventAccess(eventId);
        return ToInfo(await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, ct) ?? throw AdminErrors.EventNotFound(eventId));
    }

    public async Task<AdminEventInfo> CreateAsync(CreateEventRequest r, CancellationToken ct)
    {
        var errors = new Errors();
        var name = Text(errors, "name", r.Name, 200);
        var slug = r.Slug?.Trim() ?? "";
        if (slug.Length is < 3 or > 60 || !SlugRegex().IsMatch(slug)) errors.Add("slug", "ใช้ a-z 0-9 และ - ยาว 3–60 ตัว");
        if (r.Year is < 2000 or > 2200) errors.Add("year", "ปีไม่ถูกต้อง");
        var reservation = r.ReservationMinutes ?? 30;
        var dates = Dates(errors, r.EventDate, r.RegistrationOpensAt, r.RegistrationClosesAt);
        var promptPay = PromptPay(errors, r.PromptPayId);
        var account = Text(errors, "promptPayAccountName", r.PromptPayAccountName, 100);
        Reservation(errors, reservation);
        errors.ThrowIfAny();

        var ev = new Event
        {
            Name = name, Slug = slug, Year = r.Year, EventDate = dates.EventDate, RegistrationOpensAt = dates.Opens,
            RegistrationClosesAt = dates.Closes, PromptPayId = promptPay, PromptPayAccountName = account,
            ReservationMinutes = reservation, IsPublished = false,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Events.Add(ev);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex)) { throw AdminErrors.SlugTaken(slug); }
        audit.Add("event.create", "Event", ev.Id.ToString(), newValues: ToInfo(ev));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToInfo(ev);
    }

    public async Task<EventUpdateResult> UpdateAsync(int eventId, UpdateEventRequest r, CancellationToken ct)
    {
        admin.EnsureEventAccess(eventId);
        var errors = new Errors();
        var name = Text(errors, "name", r.Name, 200);
        var dates = Dates(errors, r.EventDate, r.RegistrationOpensAt, r.RegistrationClosesAt);
        var promptPay = PromptPay(errors, r.PromptPayId);
        var account = Text(errors, "promptPayAccountName", r.PromptPayAccountName, 100);
        Reservation(errors, r.ReservationMinutes);
        errors.ThrowIfAny();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ev = await db.Events.SingleOrDefaultAsync(e => e.Id == eventId, ct) ?? throw AdminErrors.EventNotFound(eventId);
        var before = ToInfo(ev);

        if (r.IsPublished && !ev.IsPublished) await EnsureReadyToPublishAsync(eventId, ct);
        ev.Name = name; ev.EventDate = dates.EventDate; ev.RegistrationOpensAt = dates.Opens; ev.RegistrationClosesAt = dates.Closes;
        ev.PromptPayId = promptPay; ev.PromptPayAccountName = account; ev.ReservationMinutes = r.ReservationMinutes; ev.IsPublished = r.IsPublished;

        var after = ToInfo(ev);
        audit.Add("event.update", "Event", eventId.ToString(), before, after);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        List<string> warnings = [];
        if (before.PromptPayId != after.PromptPayId)
            warnings.Add("เปลี่ยน PromptPay แล้ว ต้องแก้บัญชีรับเงินใน SlipOK LIFF ให้ตรงกัน ไม่เช่นนั้นทุกสลิปจะถูกปฏิเสธเป็น WRONG_RECEIVER");
        return new EventUpdateResult(after, warnings);
    }

    private async Task EnsureReadyToPublishAsync(int eventId, CancellationToken ct)
    {
        var missing = new List<string>();
        if (!await db.EventDistances.AnyAsync(d => d.EventId == eventId, ct)) missing.Add("distances");
        if (!await db.EventShirtSizes.AnyAsync(s => s.EventId == eventId, ct)) missing.Add("shirtSizes");
        if (!await db.ShippingOptions.AnyAsync(s => s.EventId == eventId && s.IsActive, ct)) missing.Add("shippingOptions");
        if (missing.Count > 0) throw AdminErrors.NotReadyToPublish(missing);
    }

    // ---------- ระยะ ----------

    public async Task<IReadOnlyList<DistanceInfo>> GetDistancesAsync(int eventId, CancellationToken ct)
    {
        await EnsureEventAsync(eventId);
        return await ReadDistancesAsync(eventId, ct);
    }

    public async Task<IReadOnlyList<DistanceInfo>> PutDistancesAsync(int eventId, IReadOnlyList<DistanceInput>? input, CancellationToken ct)
    {
        await EnsureEventAsync(eventId);
        input ??= [];
        var errors = new Errors();
        for (var i = 0; i < input.Count; i++)
        {
            var d = input[i];
            var at = $"distances[{i}]";
            Text(errors, $"{at}.name", d.Name, 50);
            if (d.DistanceKm <= 0 || d.DistanceKm > 1000) errors.Add($"{at}.distanceKm", "ต้องมากกว่า 0");
            if (!PrefixRegex().IsMatch(d.BibPrefix?.Trim().ToUpperInvariant() ?? "")) errors.Add($"{at}.bibPrefix", "A-Z 0-9 ยาว 1–5 ตัว");
            if (d.Quota < 0) errors.Add($"{at}.quota", "ต้องไม่ติดลบ");
            if (d.Price < 0) errors.Add($"{at}.price", "ต้องไม่ติดลบ");
            if ((d.EarlyBirdPrice is null) != (d.EarlyBirdEndsAt is null)) errors.Add($"{at}.earlyBirdPrice", "ต้องใส่ราคาและวันตัดรอบพร้อมกัน");
            else if (d.EarlyBirdPrice is { } eb && (eb < 0 || eb > d.Price)) errors.Add($"{at}.earlyBirdPrice", "ต้องไม่เกินราคาปกติ");
            if (d.EarlyBirdEndsAt is { Kind: DateTimeKind.Unspecified }) errors.Add($"{at}.earlyBirdEndsAt", "ต้องระบุเขตเวลา (เช่น ลงท้าย Z)");
        }
        Duplicates(errors, "distances", input.Select(d => d.Name?.Trim()), "name");
        Duplicates(errors, "distances", input.Select(d => d.BibPrefix?.Trim().ToUpperInvariant()), "bibPrefix");
        errors.ThrowIfAny();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.EventDistances.AsNoTracking().Where(d => d.EventId == eventId).ToListAsync(ct);
        var before = existing.Select(ToInfo).ToList();
        CheckIds(input.Select(d => d.Id), existing.Select(d => d.Id), "distances");

        try
        {
            foreach (var gone in existing.Where(e => input.All(i => i.Id != e.Id)))
            {
                if (await db.Registrations.AnyAsync(r => r.EventDistanceId == gone.Id, ct)) throw AdminErrors.InUse("distance", gone.Name);
                await db.EventDistances.Where(d => d.Id == gone.Id).ExecuteDeleteAsync(ct);
            }
            foreach (var d in input)
            {
                var (name, prefix) = (d.Name.Trim(), d.BibPrefix.Trim().ToUpperInvariant());
                var endsAt = d.EarlyBirdEndsAt?.ToUniversalTime();
                if (d.Id is not { } id)
                {
                    db.EventDistances.Add(new EventDistance
                    {
                        EventId = eventId, Name = name, DistanceKm = d.DistanceKm, BibPrefix = prefix, Quota = d.Quota,
                        Price = d.Price, EarlyBirdPrice = d.EarlyBirdPrice, EarlyBirdEndsAt = endsAt,
                    });
                    continue;
                }
                // เงื่อนไขอยู่ใน WHERE: ลดโควตาต่ำกว่าที่จองไม่ได้ / เปลี่ยน prefix หลังออกบิบไม่ได้ แม้มีคนจองหรือจ่ายพร้อมกัน
                var updated = await db.EventDistances
                    .Where(x => x.Id == id && x.ReservedCount <= d.Quota && (x.BibPrefix == prefix || x.LastBibNumber == 0))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Name, name).SetProperty(x => x.DistanceKm, d.DistanceKm)
                        .SetProperty(x => x.BibPrefix, prefix).SetProperty(x => x.Quota, d.Quota)
                        .SetProperty(x => x.Price, d.Price).SetProperty(x => x.EarlyBirdPrice, d.EarlyBirdPrice)
                        .SetProperty(x => x.EarlyBirdEndsAt, endsAt), ct);
                if (updated == 0)
                {
                    var now = await db.EventDistances.AsNoTracking().SingleAsync(x => x.Id == id, ct);
                    throw now.ReservedCount > d.Quota
                        ? AdminErrors.QuotaBelowReserved(now.Name, d.Quota, now.ReservedCount)
                        : AdminErrors.BibPrefixLocked(now.Name);
                }
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUnique(ex)) { throw AdminErrors.Duplicate("distances"); }

        var after = await ReadDistancesAsync(eventId, ct);
        audit.Add("event.distances.update", "Event", eventId.ToString(), before, after);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return after;
    }

    // ---------- ไซซ์เสื้อ ----------

    public async Task<IReadOnlyList<ShirtSizeInfo>> GetShirtSizesAsync(int eventId, CancellationToken ct)
    {
        await EnsureEventAsync(eventId);
        return await ReadShirtSizesAsync(eventId, ct);
    }

    public async Task<IReadOnlyList<ShirtSizeInfo>> PutShirtSizesAsync(int eventId, IReadOnlyList<ShirtSizeInput>? input, CancellationToken ct)
    {
        await EnsureEventAsync(eventId);
        input ??= [];
        var errors = new Errors();
        for (var i = 0; i < input.Count; i++)
        {
            Text(errors, $"shirtSizes[{i}].size", input[i].Size, 10);
            if (input[i].Detail?.Length > 100) errors.Add($"shirtSizes[{i}].detail", "ยาวเกิน 100 ตัวอักษร");
            if (input[i].Stock < 0) errors.Add($"shirtSizes[{i}].stock", "ต้องไม่ติดลบ");
        }
        Duplicates(errors, "shirtSizes", input.Select(s => s.Size?.Trim().ToUpperInvariant()), "size");
        errors.ThrowIfAny();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.EventShirtSizes.AsNoTracking().Where(s => s.EventId == eventId).ToListAsync(ct);
        var before = existing.Select(ToInfo).ToList();
        CheckIds(input.Select(s => s.Id), existing.Select(s => s.Id), "shirtSizes");

        try
        {
            foreach (var gone in existing.Where(e => input.All(i => i.Id != e.Id)))
            {
                if (await db.Registrations.AnyAsync(r => r.EventShirtSizeId == gone.Id, ct)) throw AdminErrors.InUse("shirtSize", gone.Size);
                await db.EventShirtSizes.Where(s => s.Id == gone.Id).ExecuteDeleteAsync(ct);
            }
            foreach (var s in input)
            {
                var (size, detail) = (s.Size.Trim(), string.IsNullOrWhiteSpace(s.Detail) ? null : s.Detail.Trim());
                if (s.Id is not { } id)
                {
                    db.EventShirtSizes.Add(new EventShirtSize { EventId = eventId, Size = size, Detail = detail, Stock = s.Stock });
                    continue;
                }
                var updated = await db.EventShirtSizes.Where(x => x.Id == id && x.ReservedCount <= s.Stock)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.Size, size).SetProperty(x => x.Detail, detail).SetProperty(x => x.Stock, s.Stock), ct);
                if (updated == 0)
                {
                    var now = await db.EventShirtSizes.AsNoTracking().SingleAsync(x => x.Id == id, ct);
                    throw AdminErrors.QuotaBelowReserved(now.Size, s.Stock, now.ReservedCount);
                }
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUnique(ex)) { throw AdminErrors.Duplicate("shirtSizes"); }

        var after = await ReadShirtSizesAsync(eventId, ct);
        audit.Add("event.shirtsizes.update", "Event", eventId.ToString(), before, after);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return after;
    }

    // ---------- วิธีรับของ ----------

    public async Task<IReadOnlyList<ShippingOptionInfo>> GetShippingOptionsAsync(int eventId, CancellationToken ct)
    {
        await EnsureEventAsync(eventId);
        return await ReadShippingAsync(eventId, ct);
    }

    public async Task<IReadOnlyList<ShippingOptionInfo>> PutShippingOptionsAsync(int eventId, IReadOnlyList<ShippingOptionInput>? input, CancellationToken ct)
    {
        await EnsureEventAsync(eventId);
        input ??= [];
        var errors = new Errors();
        for (var i = 0; i < input.Count; i++)
        {
            Text(errors, $"shippingOptions[{i}].name", input[i].Name, 100);
            if (input[i].Fee < 0) errors.Add($"shippingOptions[{i}].fee", "ต้องไม่ติดลบ");
            if (!Enum.IsDefined(input[i].Type)) errors.Add($"shippingOptions[{i}].type", "Pickup หรือ Post");
        }
        errors.ThrowIfAny();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var published = await db.Events.Where(e => e.Id == eventId).Select(e => e.IsPublished).SingleAsync(ct);
        if (published && !input.Any(s => s.IsActive)) throw AdminErrors.NotReadyToPublish(["shippingOptions"]);   // งานที่เปิดอยู่ต้องมีวิธีรับของ
        var existing = await db.ShippingOptions.Where(s => s.EventId == eventId).ToListAsync(ct);
        var before = existing.Select(ToInfo).ToList();
        CheckIds(input.Select(s => s.Id), existing.Select(s => s.Id), "shippingOptions");

        foreach (var gone in existing.Where(e => input.All(i => i.Id != e.Id)))
        {
            // ออเดอร์เก่าอ้างถึงอยู่ ลบไม่ได้ ให้ปิดใช้งาน (isActive=false) แทน
            if (await db.Orders.AnyAsync(o => o.ShippingOptionId == gone.Id, ct)) throw AdminErrors.InUse("shippingOption", gone.Name);
            db.ShippingOptions.Remove(gone);
        }
        foreach (var s in input)
        {
            var row = s.Id is { } id ? existing.Single(e => e.Id == id) : db.ShippingOptions.Add(new ShippingOption { EventId = eventId }).Entity;
            (row.Type, row.Name, row.Fee, row.IsActive) = (s.Type, s.Name.Trim(), s.Fee, s.IsActive);
        }
        await db.SaveChangesAsync(ct);

        var after = await ReadShippingAsync(eventId, ct);
        audit.Add("event.shipping.update", "Event", eventId.ToString(), before, after);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return after;
    }

    // ---------- helpers ----------

    private async Task EnsureEventAsync(int eventId)
    {
        admin.EnsureEventAccess(eventId);
        if (!await db.Events.AnyAsync(e => e.Id == eventId)) throw AdminErrors.EventNotFound(eventId);
    }

    private async Task<List<DistanceInfo>> ReadDistancesAsync(int eventId, CancellationToken ct) =>
        (await db.EventDistances.AsNoTracking().Where(d => d.EventId == eventId).OrderBy(d => d.DistanceKm).ThenBy(d => d.Id).ToListAsync(ct))
        .Select(ToInfo).ToList();

    private async Task<List<ShirtSizeInfo>> ReadShirtSizesAsync(int eventId, CancellationToken ct) =>
        (await db.EventShirtSizes.AsNoTracking().Where(s => s.EventId == eventId).OrderBy(s => s.Id).ToListAsync(ct)).Select(ToInfo).ToList();

    private async Task<List<ShippingOptionInfo>> ReadShippingAsync(int eventId, CancellationToken ct) =>
        (await db.ShippingOptions.AsNoTracking().Where(s => s.EventId == eventId).OrderBy(s => s.Id).ToListAsync(ct)).Select(ToInfo).ToList();

    private static AdminEventInfo ToInfo(Event e) => new(e.Id, e.Name, e.Slug, e.Year, e.EventDate, e.RegistrationOpensAt,
        e.RegistrationClosesAt, e.PromptPayId, e.PromptPayAccountName, e.ReservationMinutes, e.IsPublished);
    private static DistanceInfo ToInfo(EventDistance d) => new(d.Id, d.Name, d.DistanceKm, d.BibPrefix, d.Quota, d.ReservedCount,
        d.LastBibNumber, d.Price, d.EarlyBirdPrice, d.EarlyBirdEndsAt);
    private static ShirtSizeInfo ToInfo(EventShirtSize s) => new(s.Id, s.Size, s.Detail, s.Stock, s.ReservedCount);
    private static ShippingOptionInfo ToInfo(ShippingOption s) => new(s.Id, s.Type, s.Name, s.Fee, s.IsActive);

    private static bool IsUnique(Exception ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
        || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static void CheckIds(IEnumerable<int?> given, IEnumerable<int> existing, string field)
    {
        var known = existing.ToHashSet();
        var ids = given.Where(i => i is not null).Select(i => i!.Value).ToList();
        if (ids.Any(i => !known.Contains(i)) || ids.Distinct().Count() != ids.Count)
            throw AdminErrors.Validation(field, "id ไม่ถูกต้อง (ไม่ใช่ของงานนี้หรือซ้ำ)");
    }

    private static void Duplicates(Errors errors, string field, IEnumerable<string?> values, string prop)
    {
        var list = values.ToList();
        if (list.Where(v => !string.IsNullOrEmpty(v)).GroupBy(v => v).Any(g => g.Count() > 1))
            errors.Add($"{field}.{prop}", "ห้ามซ้ำกัน");
    }

    private static string Text(Errors errors, string field, string? value, int max)
    {
        var v = value?.Trim() ?? "";
        if (v.Length == 0 || v.Length > max) errors.Add(field, $"ต้องไม่ว่าง ไม่เกิน {max} ตัวอักษร");
        return v;
    }

    private static string PromptPay(Errors errors, string? value)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        if (!(digits.Length == 10 && digits[0] == '0') && digits.Length != 13)
            errors.Add("promptPayId", "ต้องเป็นเบอร์ 10 หลักขึ้นต้นด้วย 0 หรือเลข 13 หลัก");
        return digits;
    }

    private static void Reservation(Errors errors, int minutes)
    {
        if (minutes is < 5 or > 240) errors.Add("reservationMinutes", "5–240 นาที");
    }

    private static (DateTime EventDate, DateTime Opens, DateTime Closes) Dates(Errors errors, DateTime eventDate, DateTime opens, DateTime closes)
    {
        foreach (var (field, d) in new[] { ("eventDate", eventDate), ("registrationOpensAt", opens), ("registrationClosesAt", closes) })
            if (d.Kind == DateTimeKind.Unspecified) errors.Add(field, "ต้องระบุเขตเวลา (เช่น ลงท้าย Z)");
        if (errors.Count == 0 && opens.ToUniversalTime() >= closes.ToUniversalTime()) errors.Add("registrationClosesAt", "ต้องหลังวันเปิดรับ");
        return errors.Count == 0 ? (eventDate.ToUniversalTime(), opens.ToUniversalTime(), closes.ToUniversalTime()) : default;
    }

    private sealed class Errors
    {
        private readonly Dictionary<string, string[]> _errors = [];
        public int Count => _errors.Count;
        public void Add(string field, string message) => _errors[field] = [message];
        public void ThrowIfAny()
        {
            if (_errors.Count > 0) throw OrderErrors.Validation(_errors);
        }
    }
}
