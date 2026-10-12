using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminEventSettingsTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private string Url(string path = "") => $"/api/v1/admin/events/{Ev.Id}{path}";

    private Task<HttpClient> Organizer() => AdminClient(AdminRoles.Organizer, Ev.Id);

    private static async Task<string> Code(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    private async Task<List<JsonElement>> GetList(HttpClient client, string path) =>
        [.. (await client.GetFromJsonAsync<JsonElement>(Url(path))).EnumerateArray()];

    private static object Dist(JsonElement d, Action<Dictionary<string, object?>>? change = null)
    {
        var map = new Dictionary<string, object?>
        {
            ["id"] = d.GetProperty("id").GetInt32(), ["name"] = d.GetProperty("name").GetString(),
            ["distanceKm"] = d.GetProperty("distanceKm").GetDecimal(), ["bibPrefix"] = d.GetProperty("bibPrefix").GetString(),
            ["quota"] = d.GetProperty("quota").GetInt32(), ["price"] = d.GetProperty("price").GetDecimal(),
            ["earlyBirdPrice"] = d.GetProperty("earlyBirdPrice").ValueKind == JsonValueKind.Null ? null : d.GetProperty("earlyBirdPrice").GetDecimal(),
            ["earlyBirdEndsAt"] = d.GetProperty("earlyBirdEndsAt").ValueKind == JsonValueKind.Null ? null : d.GetProperty("earlyBirdEndsAt").GetDateTime(),
        };
        change?.Invoke(map);
        return map;
    }

    private static object[] AllDists(List<JsonElement> list, Func<JsonElement, Action<Dictionary<string, object?>>?>? change = null) =>
        [.. list.Select(d => Dist(d, change?.Invoke(d)))];

    private static object NewEventBody(Action<Dictionary<string, object?>>? change = null)
    {
        var map = new Dictionary<string, object?>
        {
            ["name"] = "Hueb Run 2028", ["slug"] = "hueb-run-" + Guid.NewGuid().ToString("N")[..8], ["year"] = 2028,
            ["eventDate"] = "2028-02-13T22:00:00Z", ["registrationOpensAt"] = "2027-10-01T00:00:00Z",
            ["registrationClosesAt"] = "2028-01-31T00:00:00Z", ["promptPayId"] = "061-785-7545",
            ["promptPayAccountName"] = "Hueb Run", ["reservationMinutes"] = 30,
        };
        change?.Invoke(map);
        return map;
    }

    private object UpdateBody(Action<Dictionary<string, object?>>? change = null)
    {
        var map = new Dictionary<string, object?>
        {
            ["name"] = Ev.Name, ["eventDate"] = Ev.EventDate, ["registrationOpensAt"] = Ev.RegistrationOpensAt,
            ["registrationClosesAt"] = Ev.RegistrationClosesAt, ["promptPayId"] = Ev.PromptPayId,
            ["promptPayAccountName"] = Ev.PromptPayAccountName, ["reservationMinutes"] = Ev.ReservationMinutes, ["isPublished"] = true,
        };
        change?.Invoke(map);
        return map;
    }

    // ---------- สิทธิ์ ----------

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task Role_matrix_for_reading_settings(string role, HttpStatusCode expected)
    {
        var client = await AdminClient(role, Ev.Id);
        foreach (var path in new[] { "", "/distances", "/shirt-sizes", "/shipping-options" })
            Assert.Equal(expected, (await client.GetAsync(Url(path))).StatusCode);
        Assert.Equal(expected, (await client.PutAsJsonAsync(Url(), UpdateBody())).StatusCode);
    }

    [Fact]
    public async Task Organizer_without_access_to_the_event_is_forbidden_and_anonymous_is_401()
    {
        var other = await AdminClient(AdminRoles.Organizer);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Url("/distances"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync(Url("/distances"), Array.Empty<object>())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync(Url(), UpdateBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(Url())).StatusCode);
    }

    [Theory]
    [InlineData(AdminRoles.Organizer)]
    [InlineData(AdminRoles.Finance)]
    [InlineData(AdminRoles.Staff)]
    public async Task Only_super_admin_creates_events(string role)
    {
        var res = await (await AdminClient(role, Ev.Id)).PostAsJsonAsync("/api/v1/admin/events", NewEventBody());
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---------- สร้าง / แก้ไขงาน ----------

    [Fact]
    public async Task Super_admin_creates_an_unpublished_event_and_it_is_audited()
    {
        var res = await (await AdminClient(AdminRoles.SuperAdmin)).PostAsJsonAsync("/api/v1/admin/events", NewEventBody());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isPublished").GetBoolean());
        Assert.Equal("0617857545", body.GetProperty("promptPayId").GetString());   // เก็บเฉพาะตัวเลข

        await using var db = Pg.CreateDbContext();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "event.create" && a.EntityId == body.GetProperty("id").GetInt32().ToString()));
    }

    [Fact]
    public async Task Create_validates_input_and_slug_uniqueness()
    {
        var client = await AdminClient(AdminRoles.SuperAdmin);
        var dup = await client.PostAsJsonAsync("/api/v1/admin/events", NewEventBody(m => m["slug"] = Ev.Slug));
        Assert.Equal((HttpStatusCode.Conflict, "SLUG_TAKEN"), (dup.StatusCode, await Code(dup)));

        var bad = await client.PostAsJsonAsync("/api/v1/admin/events", NewEventBody(m =>
        {
            m["slug"] = "Bad Slug"; m["promptPayId"] = "12345"; m["promptPayAccountName"] = " ";
            m["registrationClosesAt"] = "2027-01-01T00:00:00Z";
        }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var details = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("details");
        foreach (var field in new[] { "slug", "promptPayId", "promptPayAccountName" })
            Assert.True(details.TryGetProperty(field, out _), field);

        var noZone = await client.PostAsJsonAsync("/api/v1/admin/events", NewEventBody(m => m["eventDate"] = "2028-02-13T22:00:00"));
        Assert.Equal(HttpStatusCode.BadRequest, noZone.StatusCode);
    }

    [Fact]
    public async Task Update_event_saves_audits_old_and_new_and_warns_when_promptpay_changes()
    {
        var res = await (await Organizer()).PutAsJsonAsync(Url(), UpdateBody(m =>
        {
            m["name"] = "ชื่อใหม่"; m["promptPayId"] = "0899999999"; m["reservationMinutes"] = 45;
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ชื่อใหม่", body.GetProperty("event").GetProperty("name").GetString());
        Assert.Contains("SlipOK", body.GetProperty("warnings")[0].GetString());

        await using var db = Pg.CreateDbContext();
        var saved = await db.Events.AsNoTracking().SingleAsync(e => e.Id == Ev.Id);
        Assert.Equal(("ชื่อใหม่", "0899999999", 45), (saved.Name, saved.PromptPayId, saved.ReservationMinutes));
        var log = await db.AuditLogs.SingleAsync(a => a.Action == "event.update" && a.EntityId == Ev.Id.ToString());
        Assert.Contains(Ev.PromptPayId, log.OldValues);
        Assert.Contains("0899999999", log.NewValues);
    }

    [Fact]
    public async Task Update_without_promptpay_change_has_no_warning_and_validates()
    {
        var client = await Organizer();
        var ok = await (await client.PutAsJsonAsync(Url(), UpdateBody())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, ok.GetProperty("warnings").GetArrayLength());

        foreach (var change in new Action<Dictionary<string, object?>>[]
                 {
                     m => m["promptPayId"] = "123", m => m["reservationMinutes"] = 1, m => m["name"] = "",
                     m => m["registrationClosesAt"] = m["registrationOpensAt"],
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Url(), UpdateBody(change))).StatusCode);
    }

    [Fact]
    public async Task Cannot_publish_an_event_without_distances_shirts_and_shipping()
    {
        var created = await (await (await AdminClient(AdminRoles.SuperAdmin)).PostAsJsonAsync("/api/v1/admin/events", NewEventBody()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        var client = await AdminClient(AdminRoles.SuperAdmin);
        var body = new Dictionary<string, object?>
        {
            ["name"] = "x", ["eventDate"] = "2028-02-13T22:00:00Z", ["registrationOpensAt"] = "2027-10-01T00:00:00Z",
            ["registrationClosesAt"] = "2028-01-31T00:00:00Z", ["promptPayId"] = "0617857545", ["promptPayAccountName"] = "x",
            ["reservationMinutes"] = 30, ["isPublished"] = true,
        };

        var res = await client.PutAsJsonAsync($"/api/v1/admin/events/{id}", body);
        Assert.Equal((HttpStatusCode.Conflict, "NOT_READY_TO_PUBLISH"), (res.StatusCode, await Code(res)));

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/admin/events/{id}/distances",
            new[] { new { name = "5K", distanceKm = 5, bibPrefix = "A", quota = 10, price = 100 } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/admin/events/{id}/shirt-sizes",
            new[] { new { size = "M", stock = 10 } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/admin/events/{id}/shipping-options",
            new[] { new { type = "Pickup", name = "รับเอง", fee = 0, isActive = true } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/admin/events/{id}", body)).StatusCode);
    }

    // ---------- ระยะ ----------

    [Fact]
    public async Task Get_distances_lists_quota_reserved_and_bib_counter()
    {
        await CreateOrderOk();
        var list = await GetList(await Organizer(), "/distances");
        var five = list.Single(d => d.GetProperty("name").GetString() == "5K");
        Assert.Equal((100, 1, 0), (five.GetProperty("quota").GetInt32(), five.GetProperty("reservedCount").GetInt32(), five.GetProperty("lastBibNumber").GetInt32()));
    }

    [Fact]
    public async Task Put_distances_updates_price_and_quota_and_audits()
    {
        var client = await Organizer();
        var list = await GetList(client, "/distances");
        var res = await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => d.GetProperty("name").GetString() == "5K"
            ? m => { m["price"] = 600m; m["quota"] = 150; m["earlyBirdPrice"] = null; m["earlyBirdEndsAt"] = null; }
            : null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var five = (await GetList(client, "/distances")).Single(d => d.GetProperty("name").GetString() == "5K");
        Assert.Equal((600m, 150, JsonValueKind.Null), (five.GetProperty("price").GetDecimal(), five.GetProperty("quota").GetInt32(), five.GetProperty("earlyBirdPrice").ValueKind));
        await using var db = Pg.CreateDbContext();
        var log = await db.AuditLogs.SingleAsync(a => a.Action == "event.distances.update" && a.EntityId == Ev.Id.ToString());
        Assert.Contains("500", log.OldValues);
        Assert.Contains("600", log.NewValues);
    }

    [Fact]
    public async Task New_price_applies_only_to_new_orders()
    {
        var old = await CreateOrderOk();
        var client = await Organizer();
        var list = await GetList(client, "/distances");
        await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => d.GetProperty("name").GetString() == "5K"
            ? m => { m["price"] = 900m; m["earlyBirdPrice"] = null; m["earlyBirdEndsAt"] = null; }
            : null));

        await using var db = Pg.CreateDbContext();
        Assert.Equal(old.PayableAmount, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == old.OrderId)).PayableAmount);
        var fresh = await CreateOrderOk(Request(Runner(firstName: "ใหม่")));
        Assert.True(fresh.PayableAmount >= 900m);
    }

    [Fact]
    public async Task Cannot_lower_quota_below_reserved_and_nothing_changes()
    {
        await CreateOrderOk(Request(Runner(), Runner(firstName: "สอง"), Runner(firstName: "สาม")));   // จอง 5K ไป 3
        var client = await Organizer();
        var list = await GetList(client, "/distances");

        var res = await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => d.GetProperty("name").GetString() == "5K"
            ? m => { m["quota"] = 2; m["price"] = 999m; }
            : null));

        Assert.Equal((HttpStatusCode.Conflict, "QUOTA_BELOW_RESERVED"), (res.StatusCode, await Code(res)));
        var five = (await GetList(client, "/distances")).Single(d => d.GetProperty("name").GetString() == "5K");
        Assert.Equal((100, 500m), (five.GetProperty("quota").GetInt32(), five.GetProperty("price").GetDecimal()));

        var equal = await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => d.GetProperty("name").GetString() == "5K" ? m => m["quota"] = 3 : null));
        Assert.Equal(HttpStatusCode.OK, equal.StatusCode);   // เท่ากับที่จองไว้ได้
    }

    [Fact]
    public async Task Bib_prefix_can_change_before_any_bib_but_not_after()
    {
        var client = await Organizer();
        var list = await GetList(client, "/distances");
        Func<JsonElement, Action<Dictionary<string, object?>>?> toZ = d => d.GetProperty("name").GetString() == "5K" ? m => m["bibPrefix"] = "z" : null;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Url("/distances"), AllDists(list, toZ))).StatusCode);
        Assert.Equal("Z", (await GetList(client, "/distances")).Single(d => d.GetProperty("name").GetString() == "5K").GetProperty("bibPrefix").GetString());

        await using (var db = Pg.CreateDbContext())   // ออกบิบไปแล้ว
            await db.EventDistances.Where(d => d.EventId == Ev.Id && d.Name == "5K").ExecuteUpdateAsync(s => s.SetProperty(d => d.LastBibNumber, 5));
        list = await GetList(client, "/distances");
        var res = await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => d.GetProperty("name").GetString() == "5K" ? m => m["bibPrefix"] = "Q" : null));
        Assert.Equal((HttpStatusCode.Conflict, "BIB_PREFIX_LOCKED"), (res.StatusCode, await Code(res)));

        var same = await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => d.GetProperty("name").GetString() == "5K" ? m => m["price"] = 510m : null));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);   // ไม่เปลี่ยน prefix ก็แก้อย่างอื่นได้
    }

    [Fact]
    public async Task Distances_can_be_added_and_unused_ones_removed_but_not_used_ones()
    {
        await CreateOrderOk();   // 5K ถูกใช้
        var client = await Organizer();
        var list = await GetList(client, "/distances");
        var five = Dist(list.Single(d => d.GetProperty("name").GetString() == "5K"));
        var added = new { name = "21K", distanceKm = 21, bibPrefix = "H", quota = 50, price = 1200 };

        var res = await client.PutAsJsonAsync(Url("/distances"), new object[] { five, added });   // 10K ไม่มีคนสมัคร → ลบได้
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(["5K", "21K"], (await GetList(client, "/distances")).Select(d => d.GetProperty("name").GetString()));   // เรียงตามระยะทาง

        var drop = await client.PutAsJsonAsync(Url("/distances"), new object[] { added });   // 5K มีผู้สมัคร
        Assert.Equal((HttpStatusCode.Conflict, "IN_USE"), (drop.StatusCode, await Code(drop)));
    }

    [Fact]
    public async Task Distances_validation()
    {
        var client = await Organizer();
        var list = await GetList(client, "/distances");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => m => m["name"] = "same"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => m => m["bibPrefix"] = "B"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => d.GetProperty("name").GetString() == "10K" ? m => m["earlyBirdPrice"] = 100m : null))).StatusCode);   // มีราคาไม่มีวันตัดรอบ
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => d.GetProperty("name").GetString() == "10K" ? m => m["price"] = -1m : null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Url("/distances"), AllDists(list, d => m => m["id"] = 987654))).StatusCode);   // id ไม่ใช่ของงานนี้
    }

    // ---------- ไซซ์เสื้อ ----------

    [Fact]
    public async Task Shirt_sizes_update_add_remove_and_stock_cannot_go_below_reserved()
    {
        await CreateOrderOk(Request(Runner(shirt: M), Runner(shirt: M, firstName: "สอง")));   // M จอง 2
        var client = await Organizer();
        var list = await GetList(client, "/shirt-sizes");
        object Size(JsonElement s, int stock) => new { id = s.GetProperty("id").GetInt32(), size = s.GetProperty("size").GetString(), detail = "รอบอก 38", stock };
        var m = list.Single(s => s.GetProperty("size").GetString() == "M");
        var l = list.Single(s => s.GetProperty("size").GetString() == "L");

        var low = await client.PutAsJsonAsync(Url("/shirt-sizes"), new[] { Size(m, 1), Size(l, 100) });
        Assert.Equal((HttpStatusCode.Conflict, "QUOTA_BELOW_RESERVED"), (low.StatusCode, await Code(low)));

        var ok = await client.PutAsJsonAsync(Url("/shirt-sizes"), new object[] { Size(m, 2), new { size = "XL", stock = 30 } });   // L ไม่มีคนใช้ → ลบ
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(["M", "XL"], (await GetList(client, "/shirt-sizes")).Select(s => s.GetProperty("size").GetString()));

        var drop = await client.PutAsJsonAsync(Url("/shirt-sizes"), new object[] { new { size = "XL", stock = 30 } });
        Assert.Equal("IN_USE", await Code(drop));
        var dup = await client.PutAsJsonAsync(Url("/shirt-sizes"), new object[] { new { size = "xl", stock = 1 }, new { size = "XL", stock = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
    }

    // ---------- วิธีรับของ ----------

    [Fact]
    public async Task Shipping_options_update_fee_add_and_deactivate_but_used_ones_cannot_be_deleted()
    {
        await CreateOrderOk();   // ใช้ Pickup
        var client = await Organizer();
        var list = await GetList(client, "/shipping-options");
        object Opt(JsonElement o, Action<Dictionary<string, object?>>? change = null)
        {
            var map = new Dictionary<string, object?>
            {
                ["id"] = o.GetProperty("id").GetInt32(), ["type"] = o.GetProperty("type").GetString(), ["name"] = o.GetProperty("name").GetString(),
                ["fee"] = o.GetProperty("fee").GetDecimal(), ["isActive"] = o.GetProperty("isActive").GetBoolean(),
            };
            change?.Invoke(map);
            return map;
        }
        var pickup = list.Single(o => o.GetProperty("type").GetString() == "Pickup");
        var post = list.Single(o => o.GetProperty("type").GetString() == "Post");

        var ok = await client.PutAsJsonAsync(Url("/shipping-options"), new[] { Opt(pickup), Opt(post, m => m["fee"] = 80m) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(80m, (await GetList(client, "/shipping-options")).Single(o => o.GetProperty("type").GetString() == "Post").GetProperty("fee").GetDecimal());

        var drop = await client.PutAsJsonAsync(Url("/shipping-options"), new[] { Opt(post) });   // Pickup ถูกออเดอร์ใช้
        Assert.Equal((HttpStatusCode.Conflict, "IN_USE"), (drop.StatusCode, await Code(drop)));

        var off = await client.PutAsJsonAsync(Url("/shipping-options"), new[] { Opt(pickup, m => m["isActive"] = false), Opt(post, m => m["isActive"] = false) });
        Assert.Equal("NOT_READY_TO_PUBLISH", await Code(off));   // งานที่เปิดอยู่ต้องมีวิธีรับของที่ใช้ได้

        var off1 = await client.PutAsJsonAsync(Url("/shipping-options"), new[] { Opt(pickup, m => m["isActive"] = false), Opt(post) });
        Assert.Equal(HttpStatusCode.OK, off1.StatusCode);

        var added = await client.PutAsJsonAsync(Url("/shipping-options"), new object[] { Opt(pickup), Opt(post), new { type = "Post", name = "Kerry", fee = 100, isActive = true } });
        Assert.Equal(3, (await GetList(client, "/shipping-options")).Count);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
    }
}
