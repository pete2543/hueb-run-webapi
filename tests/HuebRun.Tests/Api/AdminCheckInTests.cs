using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminCheckInTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private int _seed;

    private async Task<(Guid OrderId, int RegistrationId, string Token)> PaidRunner(string firstName = "สมชาย")
    {
        var created = await CreateOrderOk(Request(Runner(FiveK, M, firstName)));
        Factory.Verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));
        var res = await Client.PostAsync($"/api/v1/orders/{created.OrderId}/payment/slip",
            new MultipartFormDataContent { { new ByteArrayContent(Slips.Png(900 + ++_seed)), "file", "slip.png" } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await using var db = Pg.CreateDbContext();
        var reg = await db.Registrations.AsNoTracking().SingleAsync(r => r.OrderId == created.OrderId);
        return (created.OrderId, reg.Id, reg.CheckInToken);
    }

    private Task<HttpClient> Staff() => AdminClient(AdminRoles.Staff, Ev.Id);
    private static Task<HttpResponseMessage> Confirm(HttpClient c, int id, object? body = null) =>
        c.PostAsJsonAsync($"/api/v1/admin/checkin/{id}/confirm", body ?? new { station = "โต๊ะ 1" });
    private static Task<HttpResponseMessage> Scan(HttpClient c, string token) =>
        c.PostAsJsonAsync("/api/v1/admin/checkin/scan", new { token });
    private static async Task<string> Code(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    // ---------- สิทธิ์ ----------

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.Forbidden)]
    public async Task Scan_confirm_and_list_role_matrix(string role, HttpStatusCode expected)
    {
        var r = await PaidRunner();
        var client = await AdminClient(role, Ev.Id);
        Assert.Equal(expected, (await Scan(client, r.Token)).StatusCode);
        Assert.Equal(expected, (await Confirm(client, r.RegistrationId)).StatusCode);
        Assert.Equal(expected, (await client.GetAsync($"/api/v1/admin/events/{Ev.Id}/checkins")).StatusCode);
    }

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.Forbidden)]
    public async Task Undo_is_for_organizer_and_super_admin_only(string role, HttpStatusCode expected)
    {
        var r = await PaidRunner();
        await Confirm(await Staff(), r.RegistrationId);
        var res = await (await AdminClient(role, Ev.Id)).PostAsJsonAsync($"/api/v1/admin/checkin/{r.RegistrationId}/undo", new { note = "กดผิด" });
        Assert.Equal(expected, res.StatusCode);
    }

    [Fact]
    public async Task Staff_without_access_to_the_event_is_forbidden_and_anonymous_is_401()
    {
        var r = await PaidRunner();
        var other = await AdminClient(AdminRoles.Staff);
        Assert.Equal(HttpStatusCode.Forbidden, (await Scan(other, r.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Confirm(other, r.RegistrationId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/v1/admin/events/{Ev.Id}/checkins")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Scan(Client, r.Token)).StatusCode);
        await using var db = Pg.CreateDbContext();
        Assert.Null((await db.Registrations.AsNoTracking().SingleAsync(x => x.Id == r.RegistrationId)).CheckedInAt);
    }

    // ---------- scan ----------

    [Fact]
    public async Task Scan_returns_runner_info_without_health_or_contact_data()
    {
        var r = await PaidRunner();
        var res = await Scan(await Staff(), r.Token);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(("สมชาย", "5K", "M", "B0001", true), (body.GetProperty("firstName").GetString(), body.GetProperty("distance").GetString(),
            body.GetProperty("shirtSize").GetString(), body.GetProperty("bibNumber").GetString(), body.GetProperty("eligible").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("checkedInAt").ValueKind);
        Assert.DoesNotContain("แพ้ยา", body.ToString());
        Assert.DoesNotContain("0812345678", body.ToString());
    }

    [Fact]
    public async Task Scan_unknown_or_empty_token()
    {
        var staff = await Staff();
        var res = await Scan(staff, "nope");
        Assert.Equal((HttpStatusCode.NotFound, "CHECKIN_TOKEN_NOT_FOUND"), (res.StatusCode, await Code(res)));
        Assert.Equal(HttpStatusCode.BadRequest, (await Scan(staff, "  ")).StatusCode);
    }

    [Fact]
    public async Task Scan_of_an_unpaid_runner_says_not_eligible()
    {
        var created = await CreateOrderOk();
        await using var db = Pg.CreateDbContext();
        var token = (await db.Registrations.AsNoTracking().SingleAsync(x => x.OrderId == created.OrderId)).CheckInToken;
        var body = await (await Scan(await Staff(), token)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("eligible").GetBoolean());
    }

    // ---------- confirm ----------

    [Fact]
    public async Task Confirm_checks_in_logs_and_audits()
    {
        var r = await PaidRunner();
        var res = await Confirm(await Staff(), r.RegistrationId, new { station = "โต๊ะ 1", note = "รับเสื้อแล้ว" });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(body.GetProperty("alreadyCheckedIn").GetBoolean());
        var runner = body.GetProperty("runner");
        Assert.Equal(Factory.Time.GetUtcNow().UtcDateTime, runner.GetProperty("checkedInAt").GetDateTime());
        Assert.Equal("Staff", runner.GetProperty("checkedInBy").GetString());   // DisplayName ของผู้ใช้ทดสอบ = ชื่อบทบาท

        await using var db = Pg.CreateDbContext();
        var log = await db.CheckInLogs.AsNoTracking().SingleAsync(l => l.RegistrationId == r.RegistrationId);
        Assert.Equal((CheckInAction.CheckIn, "โต๊ะ 1", "รับเสื้อแล้ว"), (log.Action, log.Station, log.Note));
        Assert.NotNull(log.StaffUserId);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "checkin.confirm" && a.EntityId == r.RegistrationId.ToString()));
    }

    [Fact]
    public async Task Confirming_twice_is_not_an_error_reports_the_original_time_and_logs_once()
    {
        var r = await PaidRunner();
        var staff = await Staff();
        var first = await (await Confirm(staff, r.RegistrationId)).Content.ReadFromJsonAsync<JsonElement>();
        Factory.Time.Advance(TimeSpan.FromMinutes(5));

        var again = await Confirm(await Staff(), r.RegistrationId);
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(body.GetProperty("alreadyCheckedIn").GetBoolean());
        Assert.Equal(first.GetProperty("runner").GetProperty("checkedInAt").GetDateTime(), body.GetProperty("runner").GetProperty("checkedInAt").GetDateTime());
        await using var db = Pg.CreateDbContext();
        Assert.Equal(1, await db.CheckInLogs.CountAsync(l => l.RegistrationId == r.RegistrationId));
    }

    [Fact]
    public async Task Two_stations_confirming_at_once_produce_one_check_in()
    {
        var r = await PaidRunner();
        var (a, b) = (await Staff(), await Staff());
        var results = await Task.WhenAll(Confirm(a, r.RegistrationId), Confirm(b, r.RegistrationId));
        var flags = await Task.WhenAll(results.Select(async x => (await x.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alreadyCheckedIn").GetBoolean()));

        Assert.All(results, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        Assert.Equal([false, true], flags.Order());
        await using var db = Pg.CreateDbContext();
        Assert.Equal(1, await db.CheckInLogs.CountAsync(l => l.RegistrationId == r.RegistrationId));
    }

    [Fact]
    public async Task Confirm_is_blocked_for_unpaid_and_unknown_runners()
    {
        var created = await CreateOrderOk();
        await using var db = Pg.CreateDbContext();
        var id = (await db.Registrations.AsNoTracking().SingleAsync(x => x.OrderId == created.OrderId)).Id;
        var staff = await Staff();

        var res = await Confirm(staff, id);
        Assert.Equal((HttpStatusCode.Conflict, "NOT_ELIGIBLE"), (res.StatusCode, await Code(res)));
        Assert.Equal(HttpStatusCode.NotFound, (await Confirm(staff, 999999)).StatusCode);
        Assert.Equal(0, await db.CheckInLogs.CountAsync(l => l.RegistrationId == id));
    }

    // ---------- undo ----------

    [Fact]
    public async Task Undo_clears_check_in_requires_a_note_and_can_check_in_again()
    {
        var r = await PaidRunner();
        await Confirm(await Staff(), r.RegistrationId);
        var org = await AdminClient(AdminRoles.Organizer, Ev.Id);
        var url = $"/api/v1/admin/checkin/{r.RegistrationId}/undo";

        Assert.Equal(HttpStatusCode.BadRequest, (await org.PostAsJsonAsync(url, new { note = " " })).StatusCode);
        var ok = await org.PostAsJsonAsync(url, new { note = "กดผิดคน" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checkedInAt").ValueKind);

        var again = await org.PostAsJsonAsync(url, new { note = "x" });
        Assert.Equal((HttpStatusCode.Conflict, "NOT_CHECKED_IN"), (again.StatusCode, await Code(again)));
        Assert.False((await (await Confirm(await Staff(), r.RegistrationId)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alreadyCheckedIn").GetBoolean());

        await using var db = Pg.CreateDbContext();
        var actions = await db.CheckInLogs.AsNoTracking().Where(l => l.RegistrationId == r.RegistrationId).OrderBy(l => l.Id).Select(l => l.Action).ToListAsync();
        Assert.Equal([CheckInAction.CheckIn, CheckInAction.UndoCheckIn, CheckInAction.CheckIn], actions);
    }

    // ---------- list ----------

    [Fact]
    public async Task List_has_summary_filters_and_only_paid_runners()
    {
        var a = await PaidRunner("เอ");
        await PaidRunner("บี");
        await CreateOrderOk(Request(Runner(FiveK, M, "ยังไม่จ่าย")));
        await Confirm(await Staff(), a.RegistrationId);
        var staff = await Staff();
        string Url(string q = "") => $"/api/v1/admin/events/{Ev.Id}/checkins{q}";

        var all = await staff.GetFromJsonAsync<JsonElement>(Url());
        Assert.Equal((2, 1), (all.GetProperty("summary").GetProperty("paid").GetInt32(), all.GetProperty("summary").GetProperty("checkedIn").GetInt32()));
        Assert.Equal(2, all.GetProperty("runners").GetProperty("total").GetInt32());

        var done = await staff.GetFromJsonAsync<JsonElement>(Url("?checkedIn=true"));
        Assert.Equal("เอ", done.GetProperty("runners").GetProperty("items").EnumerateArray().Single().GetProperty("firstName").GetString());
        var todo = await staff.GetFromJsonAsync<JsonElement>(Url("?checkedIn=false"));
        Assert.Equal("บี", todo.GetProperty("runners").GetProperty("items").EnumerateArray().Single().GetProperty("firstName").GetString());
        Assert.Equal(1, (await staff.GetFromJsonAsync<JsonElement>(Url("?q=B0002"))).GetProperty("runners").GetProperty("total").GetInt32());
        Assert.DoesNotContain("แพ้ยา", all.ToString());
    }
}
