using System.Collections.Concurrent;
using HuebRun.Infrastructure.Payments;

namespace HuebRun.Tests.Infrastructure;

/// คืนผลตามคิวที่กำหนด; คิวว่าง = Unavailable
public sealed class FakeSlipVerifier : ISlipVerifier
{
    private readonly ConcurrentQueue<SlipCheckResult> _results = new();
    private int _calls;

    public int Calls => _calls;
    /// เรียกตอนตรวจ (เช่น จำลองลูกค้าตัดการเชื่อมต่อระหว่างรอผล)
    public Action? OnVerify { get; set; }

    public FakeSlipVerifier Enqueue(params SlipCheckResult[] results)
    {
        foreach (var r in results) _results.Enqueue(r);
        return this;
    }

    public Task<SlipCheckResult> VerifyAsync(SlipImage image, decimal expectedAmount, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        OnVerify?.Invoke();
        return Task.FromResult(_results.TryDequeue(out var r) ? r : new SlipCheckResult.Unavailable(null));
    }

    public static SlipCheckResult Ok(decimal amount, string? transRef = null) =>
        new SlipCheckResult.Verified(transRef ?? Guid.NewGuid().ToString("N"), amount, new DateTime(2026, 11, 15, 0, 5, 0, DateTimeKind.Utc), "{}");
}
