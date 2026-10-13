using System.Collections.Concurrent;
using HuebRun.Infrastructure.Line;

namespace HuebRun.Tests.Infrastructure;

public sealed class FakeLineMessenger : ILineMessenger
{
    private readonly ConcurrentQueue<LineSendResult> _results = new();
    public ConcurrentQueue<(string UserId, string Text)> Sent { get; } = new();

    /// ผลที่จะตอบครั้งต่อไป (คิวว่าง = Sent)
    public void Enqueue(params LineSendResult[] results)
    {
        foreach (var r in results) _results.Enqueue(r);
    }

    public Task<LineSendResult> PushTextAsync(string lineUserId, string text, CancellationToken ct)
    {
        var result = _results.TryDequeue(out var r) ? r : new LineSendResult(LineSendOutcome.Sent);
        if (result.Outcome == LineSendOutcome.Sent) Sent.Enqueue((lineUserId, text));
        return Task.FromResult(result);
    }
}

public sealed class FakeLineIdentityVerifier : ILineIdentityVerifier
{
    public Dictionary<string, LineIdentity> Tokens { get; } = new();

    public Task<LineIdentity?> VerifyIdTokenAsync(string idToken, CancellationToken ct) =>
        Task.FromResult(Tokens.TryGetValue(idToken, out var i) ? i : null);
}
