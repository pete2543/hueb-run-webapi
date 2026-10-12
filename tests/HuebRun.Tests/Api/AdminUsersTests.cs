using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminUsersTests(PostgresFixture pg) : AdminTestBase(pg)
{
    private async Task<(HttpClient Client, AdminUser User)> SuperAdmin()
    {
        var user = await CreateUser(AdminRoles.SuperAdmin);
        return (ClientWith(await LoginToken(user)), user);
    }

    private static async Task<string> Code(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task Only_super_admin_can_list_users(string role, HttpStatusCode expected)
    {
        var res = await (await ClientFor(role)).GetAsync("/api/v1/admin/users");
        Assert.Equal(expected, res.StatusCode);
        if (expected == HttpStatusCode.Forbidden) Assert.Equal("FORBIDDEN", await Code(res));
    }

    [Fact]
    public async Task Anonymous_cannot_use_user_endpoints()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/api/v1/admin/users", new { })).StatusCode);
    }

    [Fact]
    public async Task Create_user_can_login_and_is_audited()
    {
        var (admin, adminUser) = await SuperAdmin();
        var res = await admin.PostAsJsonAsync("/api/v1/admin/users",
            new { name = "สมหญิง", email = "somying@example.com", password = Password, role = "Finance", eventIds = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Finance", body.GetProperty("role").GetString());

        var login = await Client.PostAsJsonAsync("/api/v1/admin/auth/login", new { email = "somying@example.com", password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var log = await Db.AuditLogs.SingleAsync(a => a.Action == "user.create");
        Assert.Equal(adminUser.Id, log.AdminUserId);
        Assert.Equal(body.GetProperty("id").GetString(), log.EntityId);
        Assert.DoesNotContain(Password, log.NewValues);
    }

    [Fact]
    public async Task Create_rejects_duplicate_email_weak_password_bad_role_and_unknown_event()
    {
        var (admin, _) = await SuperAdmin();
        async Task<HttpResponseMessage> Create(string email, string password, string role, int[]? events = null) =>
            await admin.PostAsJsonAsync("/api/v1/admin/users", new { name = "x", email, password, role, eventIds = events });

        Assert.Equal(HttpStatusCode.Created, (await Create("dup@example.com", Password, "Staff")).StatusCode);
        var dup = await Create("DUP@example.com", Password, "Staff");
        Assert.Equal((HttpStatusCode.Conflict, "EMAIL_TAKEN"), (dup.StatusCode, await Code(dup)));
        Assert.Equal("WEAK_PASSWORD", await Code(await Create("a@example.com", "short", "Staff")));
        Assert.Equal("VALIDATION_ERROR", await Code(await Create("b@example.com", Password, "Wizard")));
        Assert.Equal("VALIDATION_ERROR", await Code(await Create("not-an-email", Password, "Staff")));
        Assert.Equal("VALIDATION_ERROR", await Code(await Create("c@example.com", Password, "Staff", [999_999])));
    }

    [Fact]
    public async Task List_returns_users_with_roles()
    {
        var (admin, _) = await SuperAdmin();
        var staff = await CreateUser(AdminRoles.Staff);
        var items = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users");
        Assert.Contains(items.EnumerateArray(), i => i.GetProperty("id").GetString() == staff.Id.ToString() && i.GetProperty("role").GetString() == "Staff");
    }

    [Fact]
    public async Task Changing_role_updates_role_revokes_tokens_and_audits_old_and_new()
    {
        var (admin, _) = await SuperAdmin();
        var staff = await CreateUser(AdminRoles.Staff);
        var staffClient = ClientWith(await LoginToken(staff));

        var res = await admin.PatchAsJsonAsync($"/api/v1/admin/users/{staff.Id}", new { role = "Organizer" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Organizer", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await staffClient.GetAsync("/api/v1/admin/auth/me")).StatusCode);
        var log = await Db.AuditLogs.SingleAsync(a => a.Action == "user.update");
        Assert.Contains("Staff", log.OldValues);
        Assert.Contains("Organizer", log.NewValues);
    }

    [Fact]
    public async Task Admin_password_reset_lets_user_login_with_new_password_only()
    {
        var (admin, _) = await SuperAdmin();
        var staff = await CreateUser(AdminRoles.Staff);
        var res = await admin.PatchAsJsonAsync($"/api/v1/admin/users/{staff.Id}", new { newPassword = "reset-by-admin-123" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/api/v1/admin/auth/login", new { email = staff.Email, password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/api/v1/admin/auth/login", new { email = staff.Email, password = "reset-by-admin-123" })).StatusCode);
        Assert.DoesNotContain("reset-by-admin-123", (await Db.AuditLogs.SingleAsync(a => a.Action == "user.update")).NewValues);
    }

    [Fact]
    public async Task Weak_password_reset_is_rejected_and_keeps_the_old_password()
    {
        var (admin, _) = await SuperAdmin();
        var staff = await CreateUser(AdminRoles.Staff);
        var res = await admin.PatchAsJsonAsync($"/api/v1/admin/users/{staff.Id}", new { newPassword = "short" });
        Assert.Equal("WEAK_PASSWORD", await Code(res));
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/api/v1/admin/auth/login", new { email = staff.Email, password = Password })).StatusCode);
    }

    [Fact]
    public async Task Patch_unknown_user_is_404()
    {
        var (admin, _) = await SuperAdmin();
        var res = await admin.PatchAsJsonAsync($"/api/v1/admin/users/{Guid.NewGuid()}", new { name = "x" });
        Assert.Equal((HttpStatusCode.NotFound, "USER_NOT_FOUND"), (res.StatusCode, await Code(res)));
    }

    [Fact]
    public async Task Cannot_disable_or_demote_yourself()
    {
        var (admin, me) = await SuperAdmin();
        await CreateUser(AdminRoles.SuperAdmin);   // มีคนอื่นอยู่ จึงเป็นกติกา "ตัวเอง" ไม่ใช่ "คนสุดท้าย"
        var disable = await admin.PatchAsJsonAsync($"/api/v1/admin/users/{me.Id}", new { isActive = false });
        Assert.Equal((HttpStatusCode.Conflict, "CANNOT_CHANGE_SELF"), (disable.StatusCode, await Code(disable)));
        var demote = await admin.PatchAsJsonAsync($"/api/v1/admin/users/{me.Id}", new { role = "Staff" });
        Assert.Equal("CANNOT_CHANGE_SELF", await Code(demote));
    }

    [Fact]
    public async Task Event_access_is_saved_and_returned()
    {
        var (admin, _) = await SuperAdmin();
        var ev = await TestData.CreateRegistrationEventAsync(Db);
        var staff = await CreateUser(AdminRoles.Staff);
        var res = await admin.PatchAsJsonAsync($"/api/v1/admin/users/{staff.Id}", new { eventIds = new[] { ev.Id } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var ids = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventIds").EnumerateArray().Select(e => e.GetInt32());
        Assert.Equal([ev.Id], ids);
    }
}
