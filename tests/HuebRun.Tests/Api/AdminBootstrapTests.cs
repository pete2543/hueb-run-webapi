using System.Net;
using System.Net.Http.Json;
using HuebRun.Infrastructure.Admin;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminBootstrapTests(PostgresFixture pg) : AdminTestBase(pg, new Dictionary<string, string>
{
    ["Admin:BootstrapEmail"] = "boss@example.com",
    ["Admin:BootstrapPassword"] = Password,
})
{
    [Fact]
    public async Task First_start_creates_a_super_admin_who_can_login()
    {
        var res = await Client.PostAsJsonAsync("/api/v1/admin/auth/login", new { email = "boss@example.com", password = Password });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("SuperAdmin", await res.Content.ReadAsStringAsync());
        Assert.Equal(1, await Db.AuditLogs.CountAsync(a => a.Action == "user.bootstrap"));
    }

    [Fact]
    public async Task Bootstrap_does_nothing_when_a_super_admin_already_exists()
    {
        await AdminBootstrap.RunAsync(Factory.Services.CreateScope().ServiceProvider);
        await AdminBootstrap.RunAsync(Factory.Services.CreateScope().ServiceProvider);
        Assert.Equal(1, await Db.Users.CountAsync());
    }
}
