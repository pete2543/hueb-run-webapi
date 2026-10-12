using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Infrastructure.Admin;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminAuthTests(PostgresFixture pg) : AdminTestBase(pg)
{
    private Task<HttpResponseMessage> Login(string email, string password) =>
        Client.PostAsJsonAsync("/api/v1/admin/auth/login", new { email, password });

    [Fact]
    public async Task Login_returns_token_and_user_then_me_works()
    {
        var user = await CreateUser(AdminRoles.Finance);
        var res = await Login(user.Email!, Password);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Finance", body.GetProperty("user").GetProperty("role").GetString());
        Assert.True(body.GetProperty("expiresAt").GetDateTime() > Factory.Time.GetUtcNow().UtcDateTime);

        var me = await ClientWith(body.GetProperty("token").GetString()!).GetAsync("/api/v1/admin/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(user.Email, (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("wrong-password-123")]
    [InlineData(null)]   // อีเมลที่ไม่มีในระบบ
    public async Task Bad_credentials_return_the_same_401(string? wrongPassword)
    {
        var user = await CreateUser(AdminRoles.Staff);
        var res = wrongPassword is null ? await Login("nobody@example.com", Password) : await Login(user.Email!, wrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Five_failures_lock_the_account_even_for_the_right_password()
    {
        var user = await CreateUser(AdminRoles.Staff);
        for (var i = 0; i < 5; i++) await Login(user.Email!, "wrong-password-123");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(user.Email!, Password)).StatusCode);
    }

    [Fact]
    public async Task Disabled_user_cannot_login()
    {
        var user = await CreateUser(AdminRoles.Staff, active: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(user.Email!, Password)).StatusCode);
    }

    [Fact]
    public async Task Missing_or_garbage_token_gets_401_with_error_body()
    {
        var none = await Client.GetAsync("/api/v1/admin/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("UNAUTHORIZED", (await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientWith("garbage").GetAsync("/api/v1/admin/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Token_stops_working_after_the_account_is_disabled()
    {
        var admin = await CreateUser(AdminRoles.SuperAdmin);
        var staff = await CreateUser(AdminRoles.Staff);
        var staffClient = ClientWith(await LoginToken(staff));
        Assert.Equal(HttpStatusCode.OK, (await staffClient.GetAsync("/api/v1/admin/auth/me")).StatusCode);

        var res = await ClientWith(await LoginToken(admin)).PatchAsJsonAsync($"/api/v1/admin/users/{staff.Id}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await staffClient.GetAsync("/api/v1/admin/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Change_password_revokes_old_tokens_and_checks_current_password()
    {
        var user = await CreateUser(AdminRoles.Organizer);
        var client = ClientWith(await LoginToken(user));

        var wrong = await client.PostAsJsonAsync("/api/v1/admin/auth/change-password", new { currentPassword = "nope-nope-nope", newPassword = "brand-new-password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var weak = await client.PostAsJsonAsync("/api/v1/admin/auth/change-password", new { currentPassword = Password, newPassword = "short" });
        Assert.Equal("WEAK_PASSWORD", (await weak.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var ok = await client.PostAsJsonAsync("/api/v1/admin/auth/change-password", new { currentPassword = Password, newPassword = "brand-new-password" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(user.Email!, "brand-new-password")).StatusCode);
    }

    [Fact]
    public async Task Login_success_and_failure_are_audited()
    {
        var user = await CreateUser(AdminRoles.Staff);
        await Login(user.Email!, "wrong-password-123");
        await Login(user.Email!, Password);

        var actions = await Db.AuditLogs.Where(a => a.EntityId == user.Id.ToString()).Select(a => a.Action).ToListAsync();
        Assert.Contains("auth.login_failed", actions);
        Assert.Contains("auth.login", actions);
    }
}
