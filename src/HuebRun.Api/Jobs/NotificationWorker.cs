using HuebRun.Infrastructure.Line;

namespace HuebRun.Api.Jobs;

/// ส่งข้อความใน outbox ทุก 15 วินาที (ยังไม่ตั้ง Line:ChannelAccessToken = ไม่ทำอะไร ข้อความรออยู่ในคิว)
public sealed class NotificationWorker(IServiceScopeFactory scopes, ILogger<NotificationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchDueAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Dispatched {Count} LINE notifications", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification dispatch failed; will retry next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
