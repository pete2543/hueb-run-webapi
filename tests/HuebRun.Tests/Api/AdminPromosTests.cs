using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminPromosTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private string Url(string path = "") => $"/api/v1/admin/events/{Ev.Id}/promos{path}";
    private Task<HttpClient> Organizer() => AdminClient(AdminRoles.Organizer, Ev.Id);

    private static async Task<string> Code(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    private static Dictionary<string, object?> Body(Action<Dictionary<string, object?>>? change = null)
    {
        var map = new Dictionary<string, object?>
        {
            ["code"] = "Early10", ["type"] = "Percent", ["value"] = 10m, ["minOrderAmount"] = null,
            ["startsAt"] = null, ["endsAt"] = null, ["maxUses"] = null, ["isActive"] = true,
        };
        change?.Invoke(map);
        return map;
    }

    private async Task<int> CreateOk(HttpClient client, Action<Dictionary<string, object?>>? change = null)
    {
        var res = await client.PostAsJsonAsync(Url(), Body(change));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task Role_matrix(string role, HttpStatusCode expected)
    {
        var client = await AdminClient(role, Ev.Id);
        Assert.Equal(expected, (await client.GetAsync(Url())).StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? HttpStatusCode.Created : expected,
            (await client.PostAsJsonAsync(Url(), Body(m => m["code"] = "R" + role))).StatusCode);
    }

    [Fact]
    public async Task Anonymous_401_and_no_access_to_the_event_403_everywhere()
    {
        var id = await CreateOk(await Organizer());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(Url())).StatusCode);
        var other = await AdminClient(AdminRoles.Organizer);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Url())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url(), Body(m => m["code"] = "ZZZ"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync(Url($"/{id}"), Body())).StatusCode);
    }

    [Fact]
    public async Task Create_stores_uppercase_code_audits_and_the_code_works_for_runners()
    {
        var client = await Organizer();
        var res = await client.PostAsJsonAsync(Url(), Body(m => { m["minOrderAmount"] = 100m; m["maxUses"] = 5; m["endsAt"] = "2027-01-01T00:00:00Z"; }));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((HttpStatusCode.Created, "EARLY10", 0), (res.StatusCode, body.GetProperty("code").GetString(), body.GetProperty("usedCount").GetInt32()));

        await using (var db = Pg.CreateDbContext())
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "promo.create" && a.EntityId == body.GetProperty("id").GetInt32().ToString()));

        var quote = await Client.PostAsJsonAsync($"/api/v1/events/{Ev.Slug}/promo/validate", new { code = "early10", distanceIds = new[] { FiveK.Id } });
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
    }

    [Fact]
    public async Task Code_must_be_unique_ignoring_case_within_the_event_but_may_repeat_in_another_event()
    {
        var client = await Organizer();
        await CreateOk(client);
        var dup = await client.PostAsJsonAsync(Url(), Body(m => m["code"] = "early10"));
        Assert.Equal((HttpStatusCode.Conflict, "PROMO_CODE_TAKEN"), (dup.StatusCode, await Code(dup)));

        await using var db = Pg.CreateDbContext();
        var other = await TestData.CreateRegistrationEventAsync(db);
        var res = await (await AdminClient(AdminRoles.SuperAdmin)).PostAsJsonAsync($"/api/v1/admin/events/{other.Id}/promos", Body());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Create_validation()
    {
        var client = await Organizer();
        foreach (var change in new Action<Dictionary<string, object?>>[]
                 {
                     m => m["code"] = "ab", m => m["code"] = "has space", m => m["value"] = 0m, m => m["value"] = 101m,
                     m => { m["type"] = "Fixed"; m["value"] = -5m; }, m => m["minOrderAmount"] = -1m, m => m["maxUses"] = 0,
                     m => { m["startsAt"] = "2027-01-01T00:00:00Z"; m["endsAt"] = "2026-12-01T00:00:00Z"; },
                     m => m["startsAt"] = "2027-01-01T00:00:00",
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Url(), Body(change))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Url(), Body(m => { m["type"] = "Fixed"; m["value"] = 50m; }))).StatusCode);   // ส่วนลดแบบจำนวนเงินคงที่ใช้ได้
    }

    [Fact]
    public async Task List_returns_promos_with_used_count_for_this_event_only()
    {
        var client = await Organizer();
        await CreateOk(client);
        await CreateOk(client, m => m["code"] = "SUMMER");
        await using (var db = Pg.CreateDbContext())
        {
            var other = await TestData.CreateRegistrationEventAsync(db);
            db.PromoCodes.Add(new PromoCode { EventId = other.Id, Code = "OTHER", Type = DiscountType.Fixed, Value = 1 });
            await db.SaveChangesAsync();
        }
        var list = (await client.GetFromJsonAsync<JsonElement>(Url())).EnumerateArray().Select(p => p.GetProperty("code").GetString()).ToList();
        Assert.Equal(["EARLY10", "SUMMER"], list);
    }

    [Fact]
    public async Task Update_changes_fields_but_not_the_code_and_audits_old_and_new()
    {
        var client = await Organizer();
        var id = await CreateOk(client);
        var res = await client.PutAsJsonAsync(Url($"/{id}"), Body(m => { m["code"] = "CHANGED"; m["value"] = 25m; m["maxUses"] = 50; }));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(("EARLY10", 25m, 50), (body.GetProperty("code").GetString(), body.GetProperty("value").GetDecimal(), body.GetProperty("maxUses").GetInt32()));
        await using var db = Pg.CreateDbContext();
        var log = await db.AuditLogs.SingleAsync(a => a.Action == "promo.update" && a.EntityId == id.ToString());
        Assert.Contains("10", log.OldValues);
        Assert.Contains("25", log.NewValues);
    }

    [Fact]
    public async Task Deactivating_stops_the_code_for_runners_and_it_can_be_reactivated()
    {
        var client = await Organizer();
        var id = await CreateOk(client);
        Task<HttpResponseMessage> Validate() => Client.PostAsJsonAsync($"/api/v1/events/{Ev.Slug}/promo/validate", new { code = "EARLY10", distanceIds = new[] { FiveK.Id } });

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Url($"/{id}"), Body(m => m["isActive"] = false))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Validate()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Url($"/{id}"), Body())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Validate()).StatusCode);
    }

    [Fact]
    public async Task Max_uses_cannot_go_below_used_count_and_nothing_changes()
    {
        var client = await Organizer();
        var id = await CreateOk(client, m => m["maxUses"] = 10);
        await using (var db = Pg.CreateDbContext())
            await db.PromoCodes.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UsedCount, 4));

        var low = await client.PutAsJsonAsync(Url($"/{id}"), Body(m => { m["maxUses"] = 3; m["value"] = 99m; }));
        Assert.Equal((HttpStatusCode.Conflict, "MAX_USES_BELOW_USED"), (low.StatusCode, await Code(low)));
        await using (var db = Pg.CreateDbContext())
            Assert.Equal((10, 10m), (db.PromoCodes.Single(p => p.Id == id).MaxUses, db.PromoCodes.Single(p => p.Id == id).Value));

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Url($"/{id}"), Body(m => m["maxUses"] = 4))).StatusCode);   // เท่ากับที่ใช้ไปได้
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Url($"/{id}"), Body())).StatusCode);   // ไม่จำกัด
    }

    [Fact]
    public async Task Unknown_promo_and_promo_of_another_event_are_404()
    {
        var client = await Organizer();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Url("/999999"), Body())).StatusCode);
        int otherId;
        await using (var db = Pg.CreateDbContext())
        {
            var other = await TestData.CreateRegistrationEventAsync(db);
            var promo = new PromoCode { EventId = other.Id, Code = "OTHER", Type = DiscountType.Fixed, Value = 1 };
            db.PromoCodes.Add(promo);
            await db.SaveChangesAsync();
            otherId = promo.Id;
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Url($"/{otherId}"), Body())).StatusCode);
    }
}
