using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Admin;

/// ข้อมูลที่หน้างานเห็น: ชื่อ/ระยะ/ไซซ์/บิบ เท่านั้น (ไม่มีข้อมูลสุขภาพ/ติดต่อ แม้เป็น Organizer)
public sealed record CheckInRunner(
    int RegistrationId, string OrderNo, string FirstName, string LastName, string Distance, string ShirtSize, string? BibNumber,
    string? TeamName, bool Eligible, DateTime? CheckedInAt, string? CheckedInBy);

public sealed record CheckInConfirmation(CheckInRunner Runner, bool AlreadyCheckedIn);
public sealed record CheckInSummary(int Paid, int CheckedIn);
public sealed record CheckInList(Paged<CheckInRunner> Runners, CheckInSummary Summary);

public sealed class AdminCheckInService(AppDbContext db, AuditWriter audit, ICurrentAdmin admin, TimeProvider time)
{
    public const int MaxNoteLength = 200;
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// สแกน QR: คืนข้อมูลนักวิ่งและสถานะ (ไม่เปลี่ยนอะไร)
    public async Task<CheckInRunner> ScanAsync(string? token, CancellationToken ct)
    {
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 200) throw AdminErrors.Validation("token", "ต้องระบุ token");
        var id = await db.Registrations.AsNoTracking().Where(r => r.CheckInToken == token).Select(r => (int?)r.Id).SingleOrDefaultAsync(ct)
                 ?? throw AdminErrors.CheckInTokenNotFound();
        return await LoadAsync(id, ct);
    }

    /// เช็กอิน: ซ้ำไม่ error ตอบว่าเช็กอินไปแล้วพร้อมเวลา (ไม่เขียน log ซ้ำ)
    public async Task<CheckInConfirmation> ConfirmAsync(int registrationId, string? station, string? note, CancellationToken ct)
    {
        Validate(station, note);
        var info = await LoadAsync(registrationId, ct);
        if (!info.Eligible) throw AdminErrors.NotEligibleForCheckIn();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = Now;
        var updated = await db.Registrations
            .Where(r => r.Id == registrationId && r.CheckedInAt == null && !r.IsCancelled && r.Order.Status == OrderStatus.Paid)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CheckedInAt, now).SetProperty(r => r.CheckedInBy, admin.UserId.ToString()), ct);
        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            var current = await LoadAsync(registrationId, ct);
            if (!current.Eligible) throw AdminErrors.NotEligibleForCheckIn();
            return new CheckInConfirmation(current, AlreadyCheckedIn: true);
        }

        db.CheckInLogs.Add(new CheckInLog
        {
            RegistrationId = registrationId, Action = CheckInAction.CheckIn, StaffUserId = admin.UserId,
            Station = station?.Trim(), Note = note?.Trim(), At = now,
        });
        audit.Add("checkin.confirm", "Registration", registrationId.ToString(), newValues: new { station, bib = info.BibNumber });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CheckInConfirmation(await LoadAsync(registrationId, ct), AlreadyCheckedIn: false);
    }

    /// ยกเลิกเช็กอิน (กดผิด) — ต้องมีเหตุผล
    public async Task<CheckInRunner> UndoAsync(int registrationId, string? note, CancellationToken ct)
    {
        note = note?.Trim();
        if (string.IsNullOrEmpty(note) || note.Length > MaxNoteLength)
            throw AdminErrors.Validation("note", $"ต้องระบุเหตุผล (ไม่เกิน {MaxNoteLength} ตัวอักษร)");
        await LoadAsync(registrationId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var before = await db.Registrations.AsNoTracking().Where(r => r.Id == registrationId).Select(r => r.CheckedInAt).SingleAsync(ct);
        var updated = await db.Registrations.Where(r => r.Id == registrationId && r.CheckedInAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CheckedInAt, (DateTime?)null).SetProperty(r => r.CheckedInBy, (string?)null), ct);
        if (updated == 0) throw AdminErrors.NotCheckedIn();

        db.CheckInLogs.Add(new CheckInLog
        {
            RegistrationId = registrationId, Action = CheckInAction.UndoCheckIn, StaffUserId = admin.UserId, Note = note, At = Now,
        });
        audit.Add("checkin.undo", "Registration", registrationId.ToString(), new { checkedInAt = before }, new { note });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadAsync(registrationId, ct);
    }

    public async Task<CheckInList> ListAsync(int eventId, bool? checkedIn, int? distanceId, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        admin.EnsureEventAccess(eventId);
        if (!await db.Events.AnyAsync(e => e.Id == eventId, ct)) throw AdminErrors.EventNotFound(eventId);
        var (pageNo, size) = Paged<CheckInRunner>.Normalize(page, pageSize);

        var paid = db.Registrations.AsNoTracking().Where(r => r.EventId == eventId && !r.IsCancelled && r.Order.Status == OrderStatus.Paid);
        var summary = new CheckInSummary(await paid.CountAsync(ct), await paid.CountAsync(r => r.CheckedInAt != null, ct));

        var query = paid;
        if (checkedIn is { } c) query = c ? query.Where(r => r.CheckedInAt != null) : query.Where(r => r.CheckedInAt == null);
        if (distanceId is { } d) query = query.Where(r => r.EventDistanceId == d);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var like = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(r => EF.Functions.ILike(r.FirstName + " " + r.LastName, like, "\\") || r.BibNumber == term
                || EF.Functions.ILike(r.Order.OrderNo, like, "\\"));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.LastName).ThenBy(r => r.FirstName).ThenBy(r => r.Id)
            .Skip((pageNo - 1) * size).Take(size).Select(Projection).ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => r.CheckedInBy), ct);
        var items = rows.Select(r => ToRunner(r, names)).ToList();
        return new CheckInList(new Paged<CheckInRunner>(items, pageNo, size, total), summary);
    }

    // ---------- helpers ----------

    private sealed record Row(int Id, string OrderNo, string FirstName, string LastName, string Distance, string Shirt, string? Bib,
        string? Team, bool Eligible, DateTime? CheckedInAt, string? CheckedInBy, int EventId);

    private static readonly System.Linq.Expressions.Expression<Func<Registration, Row>> Projection = r => new Row(
        r.Id, r.Order.OrderNo, r.FirstName, r.LastName, r.EventDistance.Name, r.EventShirtSize.Size, r.BibNumber, r.TeamName,
        !r.IsCancelled && r.Order.Status == OrderStatus.Paid && r.BibNumber != null, r.CheckedInAt, r.CheckedInBy, r.EventId);

    private async Task<CheckInRunner> LoadAsync(int registrationId, CancellationToken ct)
    {
        var row = await db.Registrations.AsNoTracking().Where(r => r.Id == registrationId).Select(Projection).SingleOrDefaultAsync(ct)
                  ?? throw AdminErrors.RegistrationNotFound(registrationId);
        admin.EnsureEventAccess(row.EventId);
        return ToRunner(row, await NamesAsync([row.CheckedInBy], ct));
    }

    private static CheckInRunner ToRunner(Row r, Dictionary<string, string> names) => new(
        r.Id, r.OrderNo, r.FirstName, r.LastName, r.Distance, r.Shirt, r.Bib, r.Team, r.Eligible, r.CheckedInAt,
        r.CheckedInBy is { } by && names.TryGetValue(by, out var name) ? name : null);

    private async Task<Dictionary<string, string>> NamesAsync(IEnumerable<string?> ids, CancellationToken ct)
    {
        var guids = ids.Where(i => i is not null).Select(i => Guid.TryParse(i, out var g) ? g : (Guid?)null).Where(g => g is not null)
            .Select(g => g!.Value).Distinct().ToList();
        if (guids.Count == 0) return [];
        return await db.Set<AdminUser>().AsNoTracking().Where(u => guids.Contains(u.Id)).ToDictionaryAsync(u => u.Id.ToString(), u => u.DisplayName, ct);
    }

    private static void Validate(string? station, string? note)
    {
        if (station?.Length > 50) throw AdminErrors.Validation("station", "ยาวเกิน 50 ตัวอักษร");
        if (note?.Length > MaxNoteLength) throw AdminErrors.Validation("note", $"ยาวเกิน {MaxNoteLength} ตัวอักษร");
    }
}
