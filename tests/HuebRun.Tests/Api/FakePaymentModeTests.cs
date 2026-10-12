using System.Net;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HuebRun.Tests.Api;

[Collection("db")]
public class FakePaymentModeTests(PostgresFixture pg)
{
    // ไม่มี key ของ SlipOK/MinIO เลย
    private sealed class Factory(string connectionString, string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("Jobs:OrderExpiry:Enabled", "false");
            builder.UseSetting("Jobs:PaymentRecheck:Enabled", "false");
            builder.UseSetting("Payments:Mode", "Fake");
            foreach (var key in new[] { "SlipOk:BranchId", "SlipOk:ApiKey", "Minio:AccessKey", "Minio:SecretKey" })
                builder.UseSetting(key, "");
        }
    }

    [Fact]
    public async Task Development_starts_without_keys_and_uses_fake_providers()
    {
        using var factory = new Factory(pg.ConnectionString, "Development");
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health")).StatusCode);
        Assert.IsType<DevSlipVerifier>(factory.Services.GetRequiredService<ISlipVerifier>());
        Assert.IsType<LocalSlipStorage>(factory.Services.GetRequiredService<ISlipStorage>());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Fake_mode_outside_development_refuses_to_start(string environment)
    {
        using var factory = new Factory(pg.ConnectionString, environment);
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Payments:Mode", ex.ToString());
    }
}
