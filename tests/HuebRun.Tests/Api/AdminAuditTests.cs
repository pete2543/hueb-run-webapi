using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminAuditTests(PostgresFixture pg) : AdminTestBase(pg)
{
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<AdminUser> Seed()
    {
        var alice = await CreateUser(AdminRoles.Finance);
        var bob = await CreateUser(AdminRoles.Organizer);
        Db.AuditLogs.AddRange(
            new AuditLog { AdminUserId = alice.Id, Action = "payment.approve", EntityType = "Payment", EntityId = "10", OldValues = "{\"status\":\"NeedsReview\"}", NewValues = "{\"status\":\"Verified\"}", IpAddress = "1.2.3.4", CreatedAt = T0 },
            new AuditLog { AdminUserId = alice.Id, Action = "payment.reject", EntityType = "Payment", EntityId = "11", CreatedAt = T0.AddHours(1) },
            new AuditLog { AdminUserId = bob.Id, Action = "promo.create", EntityType = "PromoCode", EntityId = "3", NewValues = "{\"code\":\"X\"}", CreatedAt = T0.AddHours(2) },
            new AuditLog { AdminUserId = null, Action = "auth.login_failed", EntityType = "AdminUser", EntityId = "ghost@example.com", CreatedAt = T0.AddHours(3) });
        await Db.SaveChangesAsync();
        return alice;
    }

    private async Task<HttpClient> Super() => ClientWith(await LoginToken(await CreateUser(AdminRoles.SuperAdmin)));

    private static string[] Actions(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()!)];

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task Only_super_admin_can_read_audit(string role, HttpStatusCode expected) =>
        Assert.Equal(expected, (await (await ClientFor(role)).GetAsync("/api/v1/admin/audit")).StatusCode);

    [Fact]
    public async Task Anonymous_gets_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/v1/admin/audit")).StatusCode);

    [Fact]
    public async Task Lists_newest_first_with_user_names_parsed_json_and_page_shape()
    {
        await Seed();
        var page = await (await Super()).GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?action=payment.approve");
        var item = page.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("Finance", "Payment", "10", "1.2.3.4"),
            (item.GetProperty("userName").GetString(), item.GetProperty("entityType").GetString(), item.GetProperty("entityId").GetString(), item.GetProperty("ipAddress").GetString()));
        Assert.Equal("Verified", item.GetProperty("newValues").GetProperty("status").GetString());   // JSON จริง ไม่ใช่สตริง
        Assert.Equal((1, 20, 1), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt32()));

        var all = await (await Super()).GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?entityType=Payment");
        Assert.Equal(["payment.reject", "payment.approve"], Actions(all));   // ใหม่สุดก่อน
    }

    [Fact]
    public async Task Filters_by_user_action_group_entity_and_time_range()
    {
        var alice = await Seed();
        var client = await Super();

        Assert.Equal(["payment.reject", "payment.approve"], Actions(await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/audit?userId={alice.Id}")));
        Assert.Equal(["payment.reject", "payment.approve"], Actions(await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?action=payment.")));   // ทั้งกลุ่ม
        Assert.Empty(Actions(await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?action=payment")));   // ไม่มีจุด = ตรงตัว
        Assert.Equal(["promo.create"], Actions(await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?entityType=PromoCode&entityId=3")));

        var range = await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/audit?from={Uri.EscapeDataString("2026-11-01T01:00:00Z")}&to={Uri.EscapeDataString("2026-11-01T03:00:00Z")}");
        Assert.Equal(["promo.create", "payment.reject"], Actions(range));   // from รวม / to ไม่รวม
    }

    [Fact]
    public async Task Rows_without_a_user_are_listed_and_filter_by_user_excludes_them()
    {
        await Seed();
        var client = await Super();
        var failed = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?action=auth.login_failed");
        var item = failed.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, item.GetProperty("userId").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("userName").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("oldValues").ValueKind);
    }

    [Fact]
    public async Task Paginates_and_validates_dates()
    {
        await Seed();
        var client = await Super();
        var p1 = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?entityType=Payment&pageSize=1&page=1");
        var p2 = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?entityType=Payment&pageSize=1&page=2");
        Assert.Equal((1, 1, 2), (Actions(p1).Length, Actions(p2).Length, p2.GetProperty("total").GetInt32()));
        Assert.NotEqual(Actions(p1)[0], Actions(p2)[0]);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/admin/audit?from=2026-11-01T00:00:00")).StatusCode);   // ไม่มีเขตเวลา
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/admin/audit?from={Uri.EscapeDataString("2026-11-02T00:00:00Z")}&to={Uri.EscapeDataString("2026-11-01T00:00:00Z")}")).StatusCode);
    }

    [Fact]
    public async Task Actions_taken_through_the_api_show_up_in_the_audit_trail()
    {
        var admin = await CreateUser(AdminRoles.SuperAdmin);
        var client = ClientWith(await LoginToken(admin));
        await client.PostAsJsonAsync("/api/v1/admin/users", new { name = "x", email = "new@example.com", password = Password, role = "Staff" });

        var page = await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/audit?userId={admin.Id}");
        Assert.Contains("user.create", Actions(page));
        Assert.Contains("auth.login", Actions(page));
    }
}
