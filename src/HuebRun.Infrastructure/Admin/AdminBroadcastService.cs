using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Line;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Admin;

public sealed record BroadcastInput(string? Title, string? Body, int? AudienceDistanceId, bool? OnlyPaid);
public sealed record SendBroadcastRequest(int ExpectedRecipients, DateTime? ScheduledAt);

/// TargetOrders = ออเดอร์ที่เข้าเงื่อนไข · Recipients = คนที่ส่ง LINE ถึงได้จริง (ผูก LINE แล้วและยังเป็นเพื่อน, ไม่ซ้ำคน)
public sealed record BroadcastAudience(int TargetOrders, int Recipients);

public sealed record BroadcastInfo(
    int Id, int EventId, string Title, string Body, NotificationChannel Channel, int? AudienceDistanceId, bool OnlyPaid,
    BroadcastStatus Status, DateTime? ScheduledAt, DateTime? SentAt, int RecipientCount, int SuccessCount, int FailCount,
    DateTime CreatedAt, string? CreatedBy, BroadcastAudience? Audience);

/// ประกาศถึงนักวิ่งทางไลน์: ร่าง → ตรวจจำนวนผู้รับ → ส่ง (ลง outbox ให้ worker ส่งทีละข้อความ)
public sealed class AdminBroadcastService(AppDbContext db, AuditWriter audit, ICurrentAdmin admin, TimeProvider time)
{
    public const int MaxTitle = 100;
    public const int MaxBody = 2000;
    public const int MaxRecipients = 20_000;
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<Paged<BroadcastInfo>> ListAsync(int eventId, BroadcastStatus? status, int? page, int? pageSize, CancellationToken ct)
    {
        await EnsureEventAsync(eventId, ct);
        var (pageNo, size) = Paged<BroadcastInfo>.Normalize(page, pageSize);
        var query = db.Broadcasts.AsNoTracking().Where(b => b.EventId == eventId);
        if (status is { } s) query = query.Where(b => b.Status == s);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id).Skip((pageNo - 1) * size).Take(size).ToListAsync(ct);
        var infos = new List<BroadcastInfo>();
        foreach (var b in rows) infos.Add(await ToInfoAsync(b, withAudience: false, ct));
        return new Paged<BroadcastInfo>(infos, pageNo, size, total);
    }

    public async Task<BroadcastInfo> GetAsync(int id, CancellationToken ct)
    {
        var b = await LoadAsync(id, ct);
        return await ToInfoAsync(b, withAudience: b.Status == BroadcastStatus.Draft, ct);
    }

    public async Task<BroadcastInfo> CreateAsync(int eventId, BroadcastInput input, CancellationToken ct)
    {
        await EnsureEventAsync(eventId, ct);
        var (title, body) = await ValidateAsync(eventId, input, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var b = new Broadcast
        {
            EventId = eventId, Title = title, Body = body, Channel = NotificationChannel.Line, AudienceDistanceId = input.AudienceDistanceId,
            OnlyPaid = input.OnlyPaid ?? true, Status = BroadcastStatus.Draft, CreatedById = admin.UserId!.Value, CreatedAt = Now,
        };
        db.Broadcasts.Add(b);
        await db.SaveChangesAsync(ct);
        audit.Add("broadcast.create", "Broadcast", b.Id.ToString(), newValues: new { title, audienceDistanceId = b.AudienceDistanceId, b.OnlyPaid });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToInfoAsync(b, withAudience: true, ct);
    }

    public async Task<BroadcastInfo> UpdateAsync(int id, BroadcastInput input, CancellationToken ct)
    {
        var existing = await LoadAsync(id, ct);
        var (title, body) = await ValidateAsync(existing.EventId, input, ct);
        var onlyPaid = input.OnlyPaid ?? existing.OnlyPaid;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.Broadcasts.Where(b => b.Id == id && b.Status == BroadcastStatus.Draft)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Title, title).SetProperty(b => b.Body, body)
                .SetProperty(b => b.AudienceDistanceId, input.AudienceDistanceId).SetProperty(b => b.OnlyPaid, onlyPaid), ct);
        if (updated == 0) throw AdminErrors.BroadcastWrongState((await LoadAsync(id, ct)).Status);
        audit.Add("broadcast.update", "Broadcast", id.ToString(), new { existing.Title, existing.Body, existing.AudienceDistanceId, existing.OnlyPaid },
            new { title, body, input.AudienceDistanceId, onlyPaid });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<BroadcastInfo> SendAsync(int id, SendBroadcastRequest req, CancellationToken ct)
    {
        var b = await LoadAsync(id, ct);
        var now = Now;
        if (req.ScheduledAt is { Kind: DateTimeKind.Unspecified }) throw AdminErrors.Validation("scheduledAt", "ต้องระบุเขตเวลา (เช่น ลงท้าย Z)");
        var at = req.ScheduledAt?.ToUniversalTime();
        if (at is { } t && t <= now) throw AdminErrors.Validation("scheduledAt", "ต้องเป็นเวลาในอนาคต");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var userIds = await RecipientsQuery(b, now).Take(MaxRecipients + 1).ToListAsync(ct);
        if (userIds.Count > MaxRecipients) throw AdminErrors.Validation("audience", $"ผู้รับเกิน {MaxRecipients} คน");
        if (userIds.Count != req.ExpectedRecipients) throw AdminErrors.AudienceChanged(req.ExpectedRecipients, userIds.Count);
        if (userIds.Count == 0) throw AdminErrors.NoRecipients();

        var status = at is null ? BroadcastStatus.Sending : BroadcastStatus.Scheduled;
        var claimed = await db.Broadcasts.Where(x => x.Id == id && x.Status == BroadcastStatus.Draft)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.ScheduledAt, at)
                .SetProperty(x => x.RecipientCount, userIds.Count).SetProperty(x => x.SuccessCount, 0).SetProperty(x => x.FailCount, 0), ct);
        if (claimed == 0) throw AdminErrors.BroadcastWrongState((await LoadAsync(id, ct)).Status);

        var text = NotificationText(b);
        var when = at ?? now;
        db.Notifications.AddRange(userIds.Select(u => new Notification
        {
            Channel = NotificationChannel.Line, Recipient = u, TemplateKey = "broadcast", BroadcastId = id,
            Payload = JsonSerializer.Serialize(new { text }), DedupeKey = $"broadcast:{id}:{u}",
            Status = NotificationStatus.Pending, ScheduledAt = when, CreatedAt = now,
        }));
        audit.Add("broadcast.send", "Broadcast", id.ToString(), new { status = "Draft" }, new { status = status.ToString(), recipients = userIds.Count, scheduledAt = at });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    /// ยกเลิก: ข้อความที่ยังไม่ส่งในคิวถูกข้าม (ที่ส่งไปแล้วเรียกคืนไม่ได้)
    public async Task<BroadcastInfo> CancelAsync(int id, CancellationToken ct)
    {
        var before = await LoadAsync(id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.Broadcasts
            .Where(b => b.Id == id && (b.Status == BroadcastStatus.Draft || b.Status == BroadcastStatus.Scheduled || b.Status == BroadcastStatus.Sending))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BroadcastStatus.Cancelled), ct);
        if (updated == 0) throw AdminErrors.BroadcastWrongState((await LoadAsync(id, ct)).Status);
        var skipped = await db.Notifications.Where(n => n.BroadcastId == id && n.Status == NotificationStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, NotificationStatus.Skipped), ct);
        audit.Add("broadcast.cancel", "Broadcast", id.ToString(), new { status = before.Status.ToString() }, new { status = "Cancelled", skipped });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------- helpers ----------

    private static string NotificationText(Broadcast b)
    {
        var text = $"{b.Title}\n\n{b.Body}";
        return text.Length > NotificationEnqueuer.MaxTextLength ? text[..NotificationEnqueuer.MaxTextLength] : text;
    }

    private IQueryable<Order> TargetOrders(Broadcast b, DateTime now)
    {
        var orders = db.Orders.AsNoTracking().Where(o => o.EventId == b.EventId);
        orders = b.OnlyPaid
            ? orders.Where(o => o.Status == OrderStatus.Paid)
            : orders.Where(o => o.Status == OrderStatus.Paid || (o.Status == OrderStatus.Pending && o.ExpiresAt > now));
        if (b.AudienceDistanceId is { } d)
            orders = orders.Where(o => o.Registrations.Any(r => !r.IsCancelled && r.EventDistanceId == d));
        return orders;
    }

    private IQueryable<string> RecipientsQuery(Broadcast b, DateTime now) =>
        TargetOrders(b, now).Where(o => o.LineAccountId != null && o.LineAccount!.IsFollowing)
            .Select(o => o.LineAccount!.LineUserId).Distinct().OrderBy(u => u);

    private async Task<BroadcastAudience> AudienceAsync(Broadcast b, CancellationToken ct)
    {
        var now = Now;
        return new BroadcastAudience(await TargetOrders(b, now).CountAsync(ct), await RecipientsQuery(b, now).CountAsync(ct));
    }

    private async Task<BroadcastInfo> ToInfoAsync(Broadcast b, bool withAudience, CancellationToken ct)
    {
        var by = await db.Set<AdminUser>().AsNoTracking().Where(u => u.Id == b.CreatedById).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
        return new BroadcastInfo(b.Id, b.EventId, b.Title, b.Body, b.Channel, b.AudienceDistanceId, b.OnlyPaid, b.Status, b.ScheduledAt,
            b.SentAt, b.RecipientCount, b.SuccessCount, b.FailCount, b.CreatedAt, by, withAudience ? await AudienceAsync(b, ct) : null);
    }

    private async Task<Broadcast> LoadAsync(int id, CancellationToken ct)
    {
        var b = await db.Broadcasts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AdminErrors.BroadcastNotFound(id);
        admin.EnsureEventAccess(b.EventId);
        return b;
    }

    private async Task<(string Title, string Body)> ValidateAsync(int eventId, BroadcastInput input, CancellationToken ct)
    {
        var title = input.Title?.Trim() ?? "";
        var body = input.Body?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitle) throw AdminErrors.Validation("title", $"ต้องไม่ว่าง ไม่เกิน {MaxTitle} ตัวอักษร");
        if (body.Length is 0 or > MaxBody) throw AdminErrors.Validation("body", $"ต้องไม่ว่าง ไม่เกิน {MaxBody} ตัวอักษร");
        if (input.AudienceDistanceId is { } d && !await db.EventDistances.AnyAsync(x => x.Id == d && x.EventId == eventId, ct))
            throw AdminErrors.Validation("audienceDistanceId", "ไม่ใช่ระยะของงานนี้");
        return (title, body);
    }

    private async Task EnsureEventAsync(int eventId, CancellationToken ct)
    {
        admin.EnsureEventAccess(eventId);
        if (!await db.Events.AnyAsync(e => e.Id == eventId, ct)) throw AdminErrors.EventNotFound(eventId);
    }
}
