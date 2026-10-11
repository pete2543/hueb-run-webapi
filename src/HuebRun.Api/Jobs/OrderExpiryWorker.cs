using HuebRun.Infrastructure.Orders;

namespace HuebRun.Api.Jobs;

/// เรียก OrderExpiryService ทุก 1 นาที (ถ้ารันหลายเครื่องก็ปลอดภัย เพราะทุกการเปลี่ยนสถานะเป็น update แบบมีเงื่อนไข)
public sealed class OrderExpiryWorker(IServiceScopeFactory scopes, ILogger<OrderExpiryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var expired = await scope.ServiceProvider.GetRequiredService<OrderExpiryService>()
                    .ExpireDueOrdersAsync(stoppingToken);
                if (expired > 0) logger.LogInformation("Expired {Count} unpaid orders", expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Order expiry run failed; will retry next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
