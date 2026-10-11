using HuebRun.Infrastructure.Payments;

namespace HuebRun.Api.Jobs;

/// เรียก PaymentRecheckService ทุก 1 นาที (รันหลายเครื่องได้ เพราะจองงานด้วย update แบบมีเงื่อนไข)
public sealed class PaymentRecheckWorker(IServiceScopeFactory scopes, ILogger<PaymentRecheckWorker> logger) : BackgroundService
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
                var count = await scope.ServiceProvider.GetRequiredService<PaymentRecheckService>()
                    .RecheckDueAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Rechecked {Count} payment slips", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Payment recheck run failed; will retry next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
