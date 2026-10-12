using HuebRun.Domain;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Infrastructure;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HuebRun.Tests.Infrastructure;

/// ฐานของ test admin API: ฐานข้อมูลใหม่ว่างต่อ test (จำนวน SuperAdmin ต้องคุมได้) + helper สร้างผู้ใช้/login
public abstract class AdminTestBase(PostgresFixture pg, IReadOnlyDictionary<string, string>? settings = null) : IAsyncLifetime
{
    public const string Password = "correct-horse-battery";

    protected ApiFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;
    protected AppDbContext Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var (db, connectionString) = await pg.CreateFreshDatabaseWithConnectionAsync();
        Db = db;
        Factory = new ApiFactory(connectionString, settings: settings);
        Client = Factory.CreateClient();   // เริ่มแอป → seed roles
    }

    public async Task DisposeAsync()
    {
        Factory.Dispose();
        await Db.DisposeAsync();
    }

    protected async Task<AdminUser> CreateUser(string role, string? email = null, string password = Password, bool active = true)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        email ??= $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        var user = new AdminUser
        {
            Id = Guid.NewGuid(), UserName = email, Email = email, DisplayName = role, IsActive = active,
            LockoutEnabled = true, CreatedAt = DateTime.UtcNow,
        };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    protected async Task<string> LoginToken(AdminUser user, string password = Password)
    {
        var res = await Client.PostAsJsonAsync("/api/v1/admin/auth/login", new { email = user.Email, password });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    protected async Task<HttpClient> ClientFor(string role)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginToken(await CreateUser(role)));
        return client;
    }

    protected HttpClient ClientWith(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
