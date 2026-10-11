using System.Net;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public class HostTests(PostgresFixture pg)
{
    [Fact]
    public async Task Health_is_ok_when_database_is_reachable()
    {
        using var factory = new ApiFactory(pg.ConnectionString);
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Healthy", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cors_allows_configured_frontend_origin()
    {
        using var factory = new ApiFactory(pg.ConnectionString);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/lookups");
        req.Headers.Add("Origin", "http://localhost:3000");
        var res = await factory.CreateClient().SendAsync(req);
        Assert.Equal("http://localhost:3000", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Cors_rejects_unknown_origin()
    {
        using var factory = new ApiFactory(pg.ConnectionString);
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("Origin", "https://evil.example");
        var res = await factory.CreateClient().SendAsync(req);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

public class HostWithoutDatabaseTests
{
    [Fact]
    public async Task App_starts_and_health_is_503_when_database_is_unreachable()
    {
        using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2");
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }
}
