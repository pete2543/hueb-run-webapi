using HuebRun.Infrastructure.Line;
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
    public const string TestLineSecret = "line-channel-secret";
    public const string TestJwtSecret = "test-secret-test-secret-test-secret-123";

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));
    public FakeSlipVerifier Verifier { get; } = new();
    public InMemorySlipStorage Storage { get; } = new();
    public FakeLineMessenger Line { get; } = new();
    public FakeLineIdentityVerifier LineIdentity { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("Jobs:OrderExpiry:Enabled", jobsEnabled ? "true" : "false");
        builder.UseSetting("Jobs:PaymentRecheck:Enabled", jobsEnabled ? "true" : "false");
        builder.UseSetting("Jobs:Notifications:Enabled", "false");   // test เรียก NotificationDispatcher เอง
        builder.UseSetting("Line:ChannelSecret", TestLineSecret);
        builder.UseSetting("Line:ChannelAccessToken", "test-token");
        builder.UseSetting("Line:LoginChannelId", "test-login-channel");
        builder.UseSetting("Line:TicketUrlBase", "https://hueb.test/orders");
        // ค่าปลอมให้ผ่าน ValidateOnStart — ตัวจริงถูกแทนด้วย fake ด้านล่าง
        builder.UseSetting("SlipOk:BranchId", "test");
        builder.UseSetting("SlipOk:ApiKey", "test");
        builder.UseSetting("Minio:Endpoint", "localhost:9000");
        builder.UseSetting("Minio:AccessKey", "test");
        builder.UseSetting("Minio:SecretKey", "test");
        builder.UseSetting("Minio:Bucket", "test");
        builder.UseSetting("Admin:JwtSecret", TestJwtSecret);
        builder.UseSetting("Admin:LoginRateLimitPerMinute", "1000");
        builder.UseSetting("RateLimits:OrdersPerWindow", "1000");
        builder.UseSetting("RateLimits:SlipsPerWindow", "1000");
        builder.UseSetting("RateLimits:PromoChecksPerWindow", "1000");
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
            s.RemoveAll<ILineMessenger>();
            s.AddSingleton<ILineMessenger>(Line);
            s.RemoveAll<ILineIdentityVerifier>();
            s.AddSingleton<ILineIdentityVerifier>(LineIdentity);
        });
    }
}
