using System.Collections.Concurrent;
using HuebRun.Infrastructure.Payments;

namespace HuebRun.Tests.Infrastructure;

public sealed class InMemorySlipStorage : ISlipStorage
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public bool FailPut { get; set; }

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        if (FailPut) throw new IOException("storage down");
        Objects[key] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]> GetAsync(string key, CancellationToken ct) =>
        Objects.TryGetValue(key, out var v) ? Task.FromResult(v) : throw new FileNotFoundException(key);

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
