using ClosedXML.Excel;
using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Admin;

public sealed record AdminRunnerItem(
    int RegistrationId, Guid OrderId, string OrderNo, string FirstName, string LastName, string Distance, string ShirtSize,
    string? BibNumber, string? TeamName, DateTime? CheckedInAt,
    // ข้อมูลอ่อนไหว: Organizer/SuperAdmin เท่านั้น
    Gender? Gender, DateOnly? BirthDate, string? Phone, string? Email, string? EmergencyContactName,
    string? EmergencyContactPhone, string? MedicalNote);

public sealed record ExportFile(byte[] Content, string FileName);

/// รายชื่อนักวิ่งที่จ่ายแล้ว + export Excel (หัวข้อ 3.4: export ทุกครั้งลง audit)
public sealed class AdminRunnerService(AppDbContext db, AuditWriter audit, ICurrentAdmin admin, TimeProvider time)
{
    private bool SeesSensitive => admin.Role is AdminRoles.SuperAdmin or AdminRoles.Organizer;

    private IQueryable<Registration> Paid(int eventId) =>
        db.Registrations.AsNoTracking().Where(r => r.EventId == eventId && !r.IsCancelled && r.Order.Status == OrderStatus.Paid);

    public async Task<Paged<AdminRunnerItem>> ListAsync(int eventId, int? distanceId, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        await EnsureEventAsync(eventId, ct);
        var (pageNo, size) = Paged<AdminRunnerItem>.Normalize(page, pageSize);
        var query = Paid(eventId);
        if (distanceId is { } d) query = query.Where(r => r.EventDistanceId == d);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var like = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(r => EF.Functions.ILike(r.FirstName + " " + r.LastName, like, "\\") || r.BibNumber == term
                || EF.Functions.ILike(r.Order.OrderNo, like, "\\") || (r.TeamName != null && EF.Functions.ILike(r.TeamName, like, "\\"))
                || (r.Phone != null && EF.Functions.ILike(r.Phone, like, "\\")) || EF.Functions.ILike(r.Order.Phone, like, "\\"));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.LastName).ThenBy(r => r.FirstName).ThenBy(r => r.Id)
            .Skip((pageNo - 1) * size).Take(size)
            .Select(r => new { R = r, r.Order.OrderNo, Distance = r.EventDistance.Name, Shirt = r.EventShirtSize.Size })
            .ToListAsync(ct);
        var s = SeesSensitive;
        var items = rows.Select(x => new AdminRunnerItem(
            x.R.Id, x.R.OrderId, x.OrderNo, x.R.FirstName, x.R.LastName, x.Distance, x.Shirt, x.R.BibNumber, x.R.TeamName, x.R.CheckedInAt,
            s ? x.R.Gender : null, s ? x.R.BirthDate : null, s ? x.R.Phone : null, s ? x.R.Email : null,
            s ? x.R.EmergencyContactName : null, s ? x.R.EmergencyContactPhone : null, s ? x.R.MedicalNote : null)).ToList();
        return new Paged<AdminRunnerItem>(items, pageNo, size, total);
    }

    public async Task<ExportFile> ExportAsync(int eventId, CancellationToken ct)
    {
        await EnsureEventAsync(eventId, ct);
        var rows = await Paid(eventId)
            .OrderBy(r => r.EventDistance.DistanceKm).ThenBy(r => r.BibNumber)
            .Select(r => new
            {
                r.Order.OrderNo, r.Order.ContactName, ContactPhone = r.Order.Phone, r.Order.PaidAt,
                Distance = r.EventDistance.Name, Shirt = r.EventShirtSize.Size, r.BibNumber,
                r.FirstName, r.LastName, r.Gender, r.BirthDate, r.Phone, r.Email, r.TeamName,
                r.EmergencyContactName, r.EmergencyContactPhone, r.MedicalNote, r.CheckedInAt,
                Shipping = r.Order.ShippingOption.Type, r.Order.ShipName, r.Order.ShipPhone, r.Order.ShipAddress,
                r.Order.ShipSubdistrictText, r.Order.ShipDistrictText, r.Order.ShipProvinceText, r.Order.ShipPostalCode, r.Order.TrackingNo,
            }).ToListAsync(ct);

        string[] headers =
        [
            "เลขออเดอร์", "ผู้ติดต่อ", "เบอร์ผู้ติดต่อ", "ชำระเมื่อ (UTC)", "ระยะ", "ไซซ์เสื้อ", "เลขบิบ", "ชื่อ", "นามสกุล", "เพศ",
            "วันเกิด", "เบอร์", "อีเมล", "ทีม", "ผู้ติดต่อฉุกเฉิน", "เบอร์ฉุกเฉิน", "โรคประจำตัว/แพ้ยา", "เช็กอินเมื่อ (UTC)",
            "วิธีรับของ", "ชื่อผู้รับ", "เบอร์ผู้รับ", "ที่อยู่จัดส่ง", "เลขพัสดุ",
        ];
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Runners");
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
        sheet.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var r in rows)
        {
            var address = string.Join(" ", new[] { r.ShipAddress, r.ShipSubdistrictText, r.ShipDistrictText, r.ShipProvinceText, r.ShipPostalCode }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            object?[] values =
            [
                r.OrderNo, r.ContactName, r.ContactPhone, r.PaidAt, r.Distance, r.Shirt, r.BibNumber, r.FirstName, r.LastName, r.Gender.ToString(),
                r.BirthDate.ToString("yyyy-MM-dd"), r.Phone, r.Email, r.TeamName, r.EmergencyContactName, r.EmergencyContactPhone, r.MedicalNote,
                r.CheckedInAt, r.Shipping.ToString(), r.ShipName, r.ShipPhone, address, r.TrackingNo,
            ];
            for (var c = 0; c < values.Length; c++) Put(sheet.Cell(row, c + 1), values[c]);
            row++;
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(row, 200));

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        audit.Add("export.runners", "Event", eventId.ToString(), newValues: new { rows = rows.Count });
        await db.SaveChangesAsync(ct);
        return new ExportFile(stream.ToArray(), $"runners-event{eventId}-{time.GetUtcNow():yyyyMMdd-HHmm}.xlsx");
    }

    // เขียนเป็นข้อความเสมอ (ชื่อ/ที่อยู่ที่ขึ้นต้นด้วย = + - @ ต้องไม่ถูกตีเป็นสูตร และเบอร์โทรไม่ถูกตัดเลข 0 นำหน้า)
    private static void Put(IXLCell cell, object? v)
    {
        switch (v)
        {
            case null: break;
            case DateTime d: cell.Value = d; cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm"; break;
            default:
                cell.Value = v.ToString() ?? "";   // XLCellValue ข้อความ ไม่ผ่านการแปลงเป็นตัวเลข/สูตร
                break;
        }
    }

    private async Task EnsureEventAsync(int eventId, CancellationToken ct)
    {
        admin.EnsureEventAccess(eventId);
        if (!await db.Events.AnyAsync(e => e.Id == eventId, ct)) throw AdminErrors.EventNotFound(eventId);
    }
}
