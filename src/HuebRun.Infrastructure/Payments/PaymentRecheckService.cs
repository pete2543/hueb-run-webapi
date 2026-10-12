using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HuebRun.Infrastructure.Payments;

/// ตรวจซ้ำ Payment ที่ Verifying และถึงเวลา (ธนาคารหน่วง / SlipOK ใช้ไม่ได้ / แอปตายก่อนตรวจ)
public sealed class PaymentRecheckService(
    AppDbContext db, PaymentService payments, ISlipVerifier verifier, ISlipStorage storage,
    TimeProvider time, ILogger<PaymentRecheckService>? logger = null)
{
    public const int BatchSize = 50;

    public async Task<int> RecheckDueAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Verifying && p.NextCheckAt <= now)
            .OrderBy(p => p.NextCheckAt)
            .Take(BatchSize)
            .Select(p => new { p.Id, p.NextCheckAt, p.SlipObjectKey, p.ExpectedAmount })
            .ToListAsync(ct);

        var checkedCount = 0;
        foreach (var p in due)
        {
            try
            {
                // จองงาน: เครื่อง/รอบอื่นที่เห็นค่าเดิมจะแก้ไม่ได้
                var claimed = await db.Payments
                    .Where(x => x.Id == p.Id && x.Status == PaymentStatus.Verifying && x.NextCheckAt == p.NextCheckAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextCheckAt, now + PaymentService.ClaimWindow), ct);
                if (claimed == 0) continue;

                // จองแล้วทำให้จบแม้แอปกำลังปิด: ผลที่ SlipOK บันทึกไว้แล้วต้องไม่หาย (ดู PaymentService.SubmitSlipAsync)
                SlipCheckResult result;
                try
                {
                    var image = SlipFile.FromBytes(await storage.GetAsync(p.SlipObjectKey!, CancellationToken.None))
                        ?? throw new InvalidOperationException($"Stored slip {p.SlipObjectKey} is not an image");
                    result = await verifier.VerifyAsync(image, p.ExpectedAmount, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogError(ex, "Recheck of payment {PaymentId} could not reach storage/verifier", p.Id);
                    result = new SlipCheckResult.Unavailable(null);
                }
                await payments.ApplyResultAsync(p.Id, result, CancellationToken.None);
                checkedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogError(ex, "Failed to recheck payment {PaymentId}; skipped", p.Id);
            }
        }
        return checkedCount;
    }
}
