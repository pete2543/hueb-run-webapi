using HuebRun.Infrastructure.Payments;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace HuebRun.Tests.Infrastructure;

public sealed class ApiFactory(string connectionString, bool jobsEnabled = false, IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));
    public FakeSlipVerifier Verifier { get; } = new();
    public InMemorySlipStorage Storage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("Jobs:OrderExpiry:Enabled", jobsEnabled ? "true" : "false");
        builder.UseSetting("Jobs:PaymentRecheck:Enabled", jobsEnabled ? "true" : "false");
        // ค่าปลอมให้ผ่าน ValidateOnStart — ตัวจริงถูกแทนด้วย fake ด้านล่าง
        builder.UseSetting("SlipOk:BranchId", "test");
        builder.UseSetting("SlipOk:ApiKey", "test");
        builder.UseSetting("Minio:Endpoint", "localhost:9000");
        builder.UseSetting("Minio:AccessKey", "test");
        builder.UseSetting("Minio:SecretKey", "test");
        builder.UseSetting("Minio:Bucket", "test");
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
            s.RemoveAll<ISlipVerifier>();
            s.AddSingleton<ISlipVerifier>(Verifier);
            s.RemoveAll<ISlipStorage>();
            s.AddSingleton<ISlipStorage>(Storage);
        });
    }
}
