using System.Text.Json;
using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HuebRun.Infrastructure.Line;

/// ส่งข้อความใน outbox ที่ถึงเวลา (รันหลายเครื่องได้: จองแต่ละข้อความด้วย update แบบมีเงื่อนไข)
public sealed class NotificationDispatcher(
    AppDbContext db, ILineMessenger line, IOptions<LineOptions> options, TimeProvider time, ILogger<NotificationDispatcher>? logger = null)
{
    public const int BatchSize = 50;
    public const int MaxAttempts = 5;
    public static readonly TimeSpan ClaimWindow = TimeSpan.FromMinutes(5);
    private static readonly int[] BackoffMinutes = [1, 5, 15, 60, 180];

    public async Task<int> DispatchDueAsync(CancellationToken ct = default)
    {
        if (!options.Value.CanPush) return 0;   // ยังไม่ตั้ง token: ข้อความรออยู่ในคิว ไม่นับว่าล้มเหลว
        var now = time.GetUtcNow().UtcDateTime;
        var due = await db.Notifications.AsNoTracking()
            .Where(n => n.Status == NotificationStatus.Pending && n.Channel == NotificationChannel.Line && n.ScheduledAt <= now)
            .OrderBy(n => n.ScheduledAt).Take(BatchSize)
            .Select(n => new { n.Id, n.ScheduledAt, n.Recipient, n.Payload, n.BroadcastId, n.Attempts })
            .ToListAsync(ct);

        var processed = 0;
        foreach (var n in due)
        {
            try
            {
                var claimed = await db.Notifications
                    .Where(x => x.Id == n.Id && x.Status == NotificationStatus.Pending && x.ScheduledAt == n.ScheduledAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ScheduledAt, now + ClaimWindow), ct);
                if (claimed == 0) continue;

                if (n.BroadcastId is { } bid)
                    await db.Broadcasts.Where(b => b.Id == bid && b.Status == BroadcastStatus.Scheduled)
                        .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BroadcastStatus.Sending), ct);

                var text = ReadText(n.Payload);
                // จองแล้วต้องทำให้จบ: ข้อความที่ LINE รับไปแล้วต้องไม่ถูกส่งซ้ำเพราะแอปปิดกลางทาง
                var result = text is null
                    ? new LineSendResult(LineSendOutcome.Permanent, "payload has no text")
                    : await line.PushTextAsync(n.Recipient, text, CancellationToken.None);
                await FinishAsync(n.Id, n.BroadcastId, n.Attempts, result, now);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogError(ex, "Failed to dispatch notification {Id}; will be retried after the claim window", n.Id);
            }
        }
        return processed;
    }

    private async Task FinishAsync(long id, int? broadcastId, int attempts, LineSendResult result, DateTime now)
    {
        var attempt = attempts + 1;
        var terminal = result.Outcome != LineSendOutcome.Retry || attempt >= MaxAttempts;
        if (result.Outcome == LineSendOutcome.Sent)
            await db.Notifications.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, NotificationStatus.Sent).SetProperty(x => x.SentAt, now)
                .SetProperty(x => x.Attempts, attempt).SetProperty(x => x.LastError, (string?)null));
        else if (terminal)
            await db.Notifications.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, NotificationStatus.Failed).SetProperty(x => x.Attempts, attempt).SetProperty(x => x.LastError, result.Error));
        else
            await db.Notifications.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ScheduledAt, now.AddMinutes(BackoffMinutes[Math.Min(attempts, BackoffMinutes.Length - 1)]))
                .SetProperty(x => x.Attempts, attempt).SetProperty(x => x.LastError, result.Error));

        if (terminal && broadcastId is { } bid)
            await CountAsync(bid, result.Outcome == LineSendOutcome.Sent, now);
    }

    // นับผลรายคนของประกาศ แล้วปิดเป็น Sent เมื่อครบทุกคน
    private async Task CountAsync(int broadcastId, bool success, DateTime now)
    {
        if (success)
            await db.Broadcasts.Where(b => b.Id == broadcastId).ExecuteUpdateAsync(s => s.SetProperty(b => b.SuccessCount, b => b.SuccessCount + 1));
        else
            await db.Broadcasts.Where(b => b.Id == broadcastId).ExecuteUpdateAsync(s => s.SetProperty(b => b.FailCount, b => b.FailCount + 1));
        await db.Broadcasts.Where(b => b.Id == broadcastId && b.Status == BroadcastStatus.Sending && b.SuccessCount + b.FailCount >= b.RecipientCount)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BroadcastStatus.Sent).SetProperty(b => b.SentAt, now));
    }

    private static string? ReadText(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("text", out var t)
                   && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s ? s : null;
        }
        catch (JsonException) { return null; }
    }
}
