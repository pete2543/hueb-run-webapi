using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Admin;

public sealed record AdminOrderItem(
    Guid OrderId, string OrderNo, OrderStatus Status, string ContactName, string Phone, int RunnerCount,
    decimal Total, decimal PayableAmount, PaymentStatus? LatestPaymentStatus, string? LatestPaymentReason,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? PaidAt);

public sealed record AdminRegistrationInfo(
    int Id, string FirstName, string LastName, string Distance, string ShirtSize, string? BibNumber, decimal PricePaid,
    string? TeamName, bool IsCancelled, DateTime? CheckedInAt,
    // ข้อมูลอ่อนไหว: Organizer/SuperAdmin เท่านั้น (Finance ได้ null)
    Gender? Gender, DateOnly? BirthDate, string? Phone, string? Email, string? IdCardOrPassport,
    string? EmergencyContactName, string? EmergencyContactPhone, string? MedicalNote);

public sealed record AdminOrderPayment(
    int PaymentId, PaymentStatus Status, string? RejectReason, string? ReviewNote, decimal ExpectedAmount, decimal? PaidAmount,
    DateTime CreatedAt, DateTime? TransferredAt, DateTime? VerifiedAt, string? ReviewedBy);

public sealed record AdminOrderShipping(string Name, string Type, decimal Fee, string? TrackingNo, string? ShipName, string? ShipPhone, string? Address);

public sealed record AdminOrderDetail(
    Guid OrderId, string OrderNo, int EventId, OrderStatus Status, string ContactName, string Phone, string? Email,
    decimal SubTotal, decimal DiscountAmount, decimal ShippingFee, decimal Total, decimal PayableAmount, string? PromoCode,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? PaidAt, AdminOrderShipping Shipping,
    IReadOnlyList<AdminRegistrationInfo> Registrations, IReadOnlyList<AdminOrderPayment> Payments);

public sealed class AdminOrderService(AppDbContext db, ICurrentAdmin admin, TimeProvider time)
{
    // ใครเห็นข้อมูลอ่อนไหว (หัวข้อ 3.4): Organizer/SuperAdmin; Finance เห็นแค่ชื่อ/เบอร์/ยอด
    private bool SeesSensitive => admin.Role is AdminRoles.SuperAdmin or AdminRoles.Organizer;

    public async Task<Paged<AdminOrderItem>> ListAsync(int eventId, OrderStatus? status, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        admin.EnsureEventAccess(eventId);
        var now = time.GetUtcNow().UtcDateTime;
        var (pageNo, size) = Paged<AdminOrderItem>.Normalize(page, pageSize);
        var query = db.Orders.AsNoTracking().Where(o => o.EventId == eventId);

        // ออเดอร์ที่เลยเวลาแต่ job ยังไม่ปิด ถือเป็น Expired (เหมือนฝั่งนักวิ่ง)
        if (status is { } s)
            query = s switch
            {
                OrderStatus.Pending => query.Where(o => o.Status == OrderStatus.Pending && o.ExpiresAt > now),
                OrderStatus.Expired => query.Where(o => o.Status == OrderStatus.Expired || (o.Status == OrderStatus.Pending && o.ExpiresAt <= now)),
                _ => query.Where(o => o.Status == s),
            };

        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{EscapeLike(q.Trim())}%";
            var digits = new string(q.Where(char.IsDigit).ToArray());
            query = query.Where(o =>
                EF.Functions.ILike(o.OrderNo, like, "\\") || EF.Functions.ILike(o.ContactName, like, "\\") ||
                EF.Functions.ILike(o.Phone, like, "\\") ||
                (digits.Length >= 3 && EF.Functions.Like(o.Phone.Replace("-", "").Replace(" ", ""), $"%{digits}%")) ||
                o.Registrations.Any(r => EF.Functions.ILike(r.FirstName + " " + r.LastName, like, "\\") || r.BibNumber == q.Trim()));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((pageNo - 1) * size).Take(size)
            .Select(o => new
            {
                o.Id, o.OrderNo, o.Status, o.ContactName, o.Phone, o.Total, o.PayableAmount, o.CreatedAt, o.ExpiresAt, o.PaidAt,
                Runners = o.Registrations.Count(r => !r.IsCancelled),
                Latest = o.Payments.OrderByDescending(p => p.Id).Select(p => new { p.Status, p.RejectReason }).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var items = rows.Select(o => new AdminOrderItem(
            o.Id, o.OrderNo, o.Status == OrderStatus.Pending && o.ExpiresAt <= now ? OrderStatus.Expired : o.Status,
            o.ContactName, o.Phone, o.Runners, o.Total, o.PayableAmount, o.Latest?.Status, o.Latest?.RejectReason,
            o.CreatedAt, o.ExpiresAt, o.PaidAt)).ToList();
        return new Paged<AdminOrderItem>(items, pageNo, size, total);
    }

    public async Task<AdminOrderDetail> GetAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.ShippingOption).Include(o => o.PromoCode)
            .Include(o => o.Registrations).ThenInclude(r => r.EventDistance)
            .Include(o => o.Registrations).ThenInclude(r => r.EventShirtSize)
            .Include(o => o.Payments)
            .AsSplitQuery()
            .SingleOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw AdminErrors.OrderNotFound(orderId);
        admin.EnsureEventAccess(order.EventId);

        var sensitive = SeesSensitive;
        var now = time.GetUtcNow().UtcDateTime;
        var address = sensitive
            ? string.Join(" ", new[] { order.ShipAddress, order.ShipSubdistrictText, order.ShipDistrictText, order.ShipProvinceText, order.ShipPostalCode }
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            : null;

        return new AdminOrderDetail(
            order.Id, order.OrderNo, order.EventId, order.GetEffectiveStatus(now), order.ContactName, order.Phone,
            sensitive ? order.Email : null,
            order.SubTotal, order.DiscountAmount, order.ShippingFee, order.Total, order.PayableAmount, order.PromoCode?.Code,
            order.CreatedAt, order.ExpiresAt, order.PaidAt,
            new AdminOrderShipping(order.ShippingOption.Name, order.ShippingOption.Type.ToString(), order.ShippingFee, order.TrackingNo,
                sensitive ? order.ShipName : null, sensitive ? order.ShipPhone : null, string.IsNullOrEmpty(address) ? null : address),
            [.. order.Registrations.OrderBy(r => r.Id).Select(r => new AdminRegistrationInfo(
                r.Id, r.FirstName, r.LastName, r.EventDistance.Name, r.EventShirtSize.Size, r.BibNumber, r.PricePaid,
                r.TeamName, r.IsCancelled, r.CheckedInAt,
                sensitive ? r.Gender : null, sensitive ? r.BirthDate : null, sensitive ? r.Phone : null, sensitive ? r.Email : null,
                sensitive ? r.IdCardOrPassport : null, sensitive ? r.EmergencyContactName : null,
                sensitive ? r.EmergencyContactPhone : null, sensitive ? r.MedicalNote : null))],
            [.. order.Payments.OrderByDescending(p => p.Id).Select(p => new AdminOrderPayment(
                p.Id, p.Status, p.RejectReason, p.ReviewNote, p.ExpectedAmount, p.PaidAmount, p.CreatedAt, p.TransferredAt,
                p.VerifiedAt, p.ReviewedBy))]);
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
