using System.Globalization;
using System.Text.Json;
using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HuebRun.Infrastructure.Line;

/// เขียนข้อความลง outbox (ตาราง Notifications) — ไม่ SaveChanges เอง ให้อยู่ transaction เดียวกับเหตุการณ์ที่ทำให้เกิดข้อความ
/// ข้ามเงียบ ๆ ถ้าออเดอร์ยังไม่ได้ผูก LINE / เลิกเป็นเพื่อน / ไม่มีเทมเพลต
public sealed class NotificationEnqueuer(AppDbContext db, IOptions<LineOptions> options, TimeProvider time)
{
    public const int MaxTextLength = 4500;   // LINE จำกัด 5000

    public async Task<bool> EnqueueForOrderAsync(Guid orderId, string templateKey, IReadOnlyDictionary<string, string>? vars,
        string dedupeKey, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Where(o => o.Id == orderId && o.LineAccountId != null)
            .Select(o => new
            {
                o.Id, o.OrderNo, o.ContactName, o.PayableAmount, o.ExpiresAt, EventName = o.Event.Name,
                LineUserId = o.LineAccount!.LineUserId, Following = o.LineAccount.IsFollowing,
            }).SingleOrDefaultAsync(ct);
        if (order is not { Following: true }) return false;

        var template = await db.NotificationTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Key == templateKey && t.Channel == NotificationChannel.Line && t.IsActive, ct);
        if (template is null) return false;
        if (await db.Notifications.AnyAsync(n => n.DedupeKey == dedupeKey, ct)) return false;

        var all = new Dictionary<string, string>
        {
            ["Name"] = order.ContactName, ["OrderNo"] = order.OrderNo, ["EventName"] = order.EventName,
            ["Total"] = order.PayableAmount.ToString("0.00", CultureInfo.InvariantCulture),
            ["ExpiresAt"] = order.ExpiresAt.AddHours(7).ToString("d MMM yyyy HH:mm", CultureInfo.GetCultureInfo("th-TH")) + " น.",   // เวลาไทย
            ["TicketUrl"] = string.IsNullOrWhiteSpace(options.Value.TicketUrlBase) ? "" : $"{options.Value.TicketUrlBase.TrimEnd('/')}/{order.Id}",
        };
        foreach (var (k, v) in vars ?? new Dictionary<string, string>()) all[k] = v;

        var now = time.GetUtcNow().UtcDateTime;
        db.Notifications.Add(new Notification
        {
            Channel = NotificationChannel.Line, Recipient = order.LineUserId, TemplateKey = templateKey, OrderId = order.Id,
            Payload = JsonSerializer.Serialize(new { text = Render(template.Body, all) }), DedupeKey = dedupeKey,
            Status = NotificationStatus.Pending, ScheduledAt = now, CreatedAt = now,
        });
        return true;
    }

    /// ส่ง "จ่ายสำเร็จ" ให้ออเดอร์ที่จ่ายแล้วแต่ผูก LINE/เป็นเพื่อนทีหลัง (dedupe กันส่งซ้ำ) แล้วบันทึกเลย
    public async Task<int> BackfillOrderPaidAsync(IEnumerable<Guid> orderIds, CancellationToken ct)
    {
        var queued = 0;
        foreach (var id in orderIds)
            if (await EnqueueForOrderAsync(id, "order_paid", null, $"order_paid:{id}", ct)) queued++;
        if (queued > 0) await SaveIgnoringDuplicatesAsync(ct);
        return queued;
    }

    /// เหตุการณ์สองทางพร้อมกัน (ผูก LINE กับ follow) อาจใส่ข้อความเดียวกันชน unique DedupeKey — ถือว่าอีกทางใส่ไปแล้ว
    public async Task SaveIgnoringDuplicatesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
        }
    }

    public static string Render(string template, IReadOnlyDictionary<string, string> vars)
    {
        var text = template;
        foreach (var (k, v) in vars) text = text.Replace("{{" + k + "}}", v);
        return text.Length > MaxTextLength ? text[..MaxTextLength] : text;
    }
}
