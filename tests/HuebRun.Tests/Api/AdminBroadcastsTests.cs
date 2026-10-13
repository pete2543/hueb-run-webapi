using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Infrastructure.Line;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminBroadcastsTests(PostgresFixture pg) : LineTestBase(pg)
{
    private Task<HttpClient> Organizer() => AdminClient(AdminRoles.Organizer, Ev.Id);
    private string EventUrl(string path = "") => $"/api/v1/admin/events/{Ev.Id}/broadcasts{path}";

    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<string> Code(HttpResponseMessage res) => (await Body(res)).GetProperty("code").GetString()!;

    private static object Draft(Action<Dictionary<string, object?>>? change = null)
    {
        var map = new Dictionary<string, object?> { ["title"] = "แจ้งเลื่อนเวลาปล่อยตัว", ["body"] = "ปล่อยตัวเวลา 05:30 น.", ["audienceDistanceId"] = null, ["onlyPaid"] = true };
        change?.Invoke(map);
        return map;
    }

    private async Task<int> CreateDraft(HttpClient c, Action<Dictionary<string, object?>>? change = null)
    {
        var res = await c.PostAsJsonAsync(EventUrl(), Draft(change));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await Body(res)).GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> Send(HttpClient c, int id, int expected, object? scheduledAt = null) =>
        c.PostAsJsonAsync($"/api/v1/admin/broadcasts/{id}/send", new { expectedRecipients = expected, scheduledAt });

    /// ผู้รับ 1 คนที่จ่ายแล้ว ผูก LINE และยังเป็นเพื่อน
    private async Task<string> PaidFollower(EventDistance? distance = null, string firstName = "สมชาย")
    {
        var (order, user) = await LinkedOrder(Request(Runner(distance, null, firstName)));
        await PayOrder(order);
        return user;
    }

    // ---------- สิทธิ์ ----------

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task Role_matrix(string role, HttpStatusCode expected)
    {
        var c = await AdminClient(role, Ev.Id);
        Assert.Equal(expected, (await c.GetAsync(EventUrl())).StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? HttpStatusCode.Created : expected, (await c.PostAsJsonAsync(EventUrl(), Draft())).StatusCode);
    }

    [Fact]
    public async Task No_event_access_and_anonymous_are_blocked_on_every_endpoint()
    {
        var id = await CreateDraft(await Organizer());
        var other = await AdminClient(AdminRoles.Organizer);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(EventUrl())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(EventUrl(), Draft())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/v1/admin/broadcasts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"/api/v1/admin/broadcasts/{id}", Draft())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(other, id, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"/api/v1/admin/broadcasts/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(EventUrl())).StatusCode);
    }

    // ---------- ร่าง ----------

    [Fact]
    public async Task Draft_shows_the_audience_split_between_target_orders_and_reachable_recipients()
    {
        await PaidFollower();
        var (noLine, _) = (await CreateOrderOk(Request(Runner(firstName: "ไม่ผูก"))), 0);
        await PayOrder(noLine);
        var (unfollowed, _) = await LinkedOrder(Request(Runner(firstName: "เลิกติดตาม")), following: false);
        await PayOrder(unfollowed);

        var res = await (await Organizer()).PostAsJsonAsync(EventUrl(), Draft());
        var b = await Body(res);

        Assert.Equal(3, b.GetProperty("audience").GetProperty("targetOrders").GetInt32());
        Assert.Equal(1, b.GetProperty("audience").GetProperty("recipients").GetInt32());
        Assert.Equal(("Draft", "Line", true), (b.GetProperty("status").GetString(), b.GetProperty("channel").GetString(), b.GetProperty("onlyPaid").GetBoolean()));
        Assert.Equal("Organizer", b.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task One_line_user_with_two_orders_is_one_recipient_and_distance_and_unpaid_filters_apply()
    {
        var userId = NewLineUser();
        var accountId = await AddLineAccount(userId);
        var a = await CreateOrderOk(Request(Runner(FiveK, M, "เอ")));
        var b = await CreateOrderOk(Request(Runner(TenK, M, "บี")));
        var unpaid = await CreateOrderOk(Request(Runner(FiveK, M, "ยังไม่จ่าย")));
        foreach (var o in new[] { a, b, unpaid }) await LinkOrder(o.OrderId, accountId);
        await PayOrder(a);
        await PayOrder(b);
        var c = await Organizer();

        async Task<(int Orders, int Recipients)> Audience(Action<Dictionary<string, object?>> change)
        {
            var id = await CreateDraft(c, change);
            var au = (await Body(await c.GetAsync($"/api/v1/admin/broadcasts/{id}"))).GetProperty("audience");
            return (au.GetProperty("targetOrders").GetInt32(), au.GetProperty("recipients").GetInt32());
        }

        Assert.Equal((2, 1), await Audience(_ => { }));                                              // จ่ายแล้ว 2 ออเดอร์ = 1 คน
        Assert.Equal((1, 1), await Audience(m => m["audienceDistanceId"] = TenK.Id));                // เฉพาะระยะ 10K
        Assert.Equal((3, 1), await Audience(m => m["onlyPaid"] = false));                            // รวมที่รอจ่าย
    }

    [Fact]
    public async Task Create_and_update_validate_input_and_only_drafts_are_editable()
    {
        var c = await Organizer();
        foreach (var change in new Action<Dictionary<string, object?>>[]
                 {
                     m => m["title"] = " ", m => m["body"] = "", m => m["title"] = new string('x', 101),
                     m => m["body"] = new string('x', 2001), m => m["audienceDistanceId"] = 987654,
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(EventUrl(), Draft(change))).StatusCode);

        var id = await CreateDraft(c);
        var upd = await c.PutAsJsonAsync($"/api/v1/admin/broadcasts/{id}", Draft(m => { m["title"] = "ใหม่"; m["audienceDistanceId"] = FiveK.Id; }));
        Assert.Equal("ใหม่", (await Body(upd)).GetProperty("title").GetString());

        await PaidFollower();
        Assert.Equal(HttpStatusCode.OK, (await Send(c, id, (await Body(await c.GetAsync($"/api/v1/admin/broadcasts/{id}"))).GetProperty("audience").GetProperty("recipients").GetInt32())).StatusCode);
        var locked = await c.PutAsJsonAsync($"/api/v1/admin/broadcasts/{id}", Draft());
        Assert.Equal((HttpStatusCode.Conflict, "BROADCAST_WRONG_STATE"), (locked.StatusCode, await Code(locked)));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/admin/broadcasts/999999")).StatusCode);
    }

    // ---------- ส่ง ----------

    [Fact]
    public async Task Send_requires_the_confirmed_recipient_count_and_at_least_one_recipient()
    {
        var c = await Organizer();
        var empty = await CreateDraft(c);
        var none = await Send(c, empty, 0);
        Assert.Equal((HttpStatusCode.Conflict, "NO_RECIPIENTS"), (none.StatusCode, await Code(none)));

        await PaidFollower();
        var wrong = await Send(c, empty, 5);
        var wrongBody = await Body(wrong);
        Assert.Equal((HttpStatusCode.Conflict, "AUDIENCE_CHANGED"), (wrong.StatusCode, wrongBody.GetProperty("code").GetString()));
        Assert.Equal(1, wrongBody.GetProperty("details").GetProperty("current").GetInt32());
        await using var db = Pg.CreateDbContext();
        Assert.Equal(BroadcastStatus.Draft, (await db.Broadcasts.AsNoTracking().SingleAsync(b => b.Id == empty)).Status);   // ไม่ส่งอะไรเลย
    }

    [Fact]
    public async Task Send_queues_one_message_per_recipient_and_the_worker_delivers_them_all()
    {
        var u1 = await PaidFollower(firstName: "หนึ่ง");
        var u2 = await PaidFollower(firstName: "สอง");
        var c = await Organizer();
        var id = await CreateDraft(c);

        var res = await Send(c, id, 2);
        var b = await Body(res);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(("Sending", 2), (b.GetProperty("status").GetString(), b.GetProperty("recipientCount").GetInt32()));
        await Dispatch();   // order_paid ของ 2 คน + ประกาศ 2 ข้อความ

        foreach (var u in new[] { u1, u2 })
        {
            var texts = SentTo(u).ToList();
            Assert.Contains(texts, t => t.StartsWith("แจ้งเลื่อนเวลาปล่อยตัว") && t.Contains("05:30"));
        }
        var after = await Body(await c.GetAsync($"/api/v1/admin/broadcasts/{id}"));
        Assert.Equal(("Sent", 2, 0), (after.GetProperty("status").GetString(), after.GetProperty("successCount").GetInt32(), after.GetProperty("failCount").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("sentAt").ValueKind);
    }

    [Fact]
    public async Task Failures_are_counted_and_the_broadcast_still_completes()
    {
        var u1 = await PaidFollower(firstName: "ส่งได้");
        var u2 = await PaidFollower(firstName: "ส่งไม่ได้");
        await Dispatch();   // ล้าง order_paid ออกจากคิวก่อน
        var c = await Organizer();
        var id = await CreateDraft(c);
        await Send(c, id, 2);

        Factory.Line.Enqueue(new LineSendResult(LineSendOutcome.Sent), new LineSendResult(LineSendOutcome.Permanent, "HTTP 400: not a friend"));
        await Dispatch();

        var after = await Body(await c.GetAsync($"/api/v1/admin/broadcasts/{id}"));
        Assert.Equal(("Sent", 1, 1), (after.GetProperty("status").GetString(), after.GetProperty("successCount").GetInt32(), after.GetProperty("failCount").GetInt32()));
        Assert.Equal(1, new[] { u1, u2 }.Sum(u => SentTo(u).Count(t => t.Contains("05:30"))));   // ส่งถึงจริงแค่คนเดียว
    }

    [Fact]
    public async Task Retryable_failure_keeps_the_broadcast_open_until_the_message_finally_succeeds()
    {
        await PaidFollower();
        await Dispatch();
        var c = await Organizer();
        var id = await CreateDraft(c);
        await Send(c, id, 1);

        Factory.Line.Enqueue(new LineSendResult(LineSendOutcome.Retry, "HTTP 429"));
        await Dispatch();
        Assert.Equal("Sending", (await Body(await c.GetAsync($"/api/v1/admin/broadcasts/{id}"))).GetProperty("status").GetString());

        Factory.Time.Advance(TimeSpan.FromMinutes(2));
        await Dispatch();
        var done = await Body(await c.GetAsync($"/api/v1/admin/broadcasts/{id}"));
        Assert.Equal(("Sent", 1), (done.GetProperty("status").GetString(), done.GetProperty("successCount").GetInt32()));
    }

    [Fact]
    public async Task Sending_twice_or_at_the_same_time_queues_messages_only_once()
    {
        var user = await PaidFollower();
        var c = await Organizer();
        var id = await CreateDraft(c);
        var results = await Task.WhenAll(Send(c, id, 1), Send(await Organizer(), id, 1));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
        Assert.Equal(1, (await Outbox(user)).Count(n => n.BroadcastId == id));
        Assert.Equal("BROADCAST_WRONG_STATE", await Code(await Send(c, id, 1)));
    }

    [Fact]
    public async Task Scheduled_broadcast_waits_for_its_time()
    {
        var user = await PaidFollower();
        await Dispatch();
        var c = await Organizer();
        var id = await CreateDraft(c);
        var at = Factory.Time.GetUtcNow().UtcDateTime.AddHours(2);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(c, id, 1, Factory.Time.GetUtcNow().UtcDateTime.AddMinutes(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(c, id, 1, "2030-01-01T00:00:00")).StatusCode);   // ไม่มีเขตเวลา
        var res = await Send(c, id, 1, at);
        Assert.Equal("Scheduled", (await Body(res)).GetProperty("status").GetString());

        Assert.Equal(0, await Dispatch());
        Assert.DoesNotContain(SentTo(user), t => t.Contains("05:30"));
        Factory.Time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(1, await Dispatch());
        Assert.Contains(SentTo(user), t => t.Contains("05:30"));
        Assert.Equal("Sent", (await Body(await c.GetAsync($"/api/v1/admin/broadcasts/{id}"))).GetProperty("status").GetString());
    }

    // ---------- ยกเลิก ----------

    [Fact]
    public async Task Cancel_skips_queued_messages_and_a_cancelled_broadcast_cannot_be_sent_or_cancelled_again()
    {
        var user = await PaidFollower();
        await Dispatch();
        var c = await Organizer();
        var id = await CreateDraft(c);
        await Send(c, id, 1);

        var res = await c.PostAsync($"/api/v1/admin/broadcasts/{id}/cancel", null);
        Assert.Equal("Cancelled", (await Body(res)).GetProperty("status").GetString());
        Assert.Equal(0, await Dispatch());
        Assert.DoesNotContain(SentTo(user), t => t.Contains("05:30"));
        Assert.Equal(NotificationStatus.Skipped, (await Outbox(user)).Single(n => n.BroadcastId == id).Status);

        Assert.Equal("BROADCAST_WRONG_STATE", await Code(await c.PostAsync($"/api/v1/admin/broadcasts/{id}/cancel", null)));
        Assert.Equal("BROADCAST_WRONG_STATE", await Code(await Send(c, id, 1)));
    }

    [Fact]
    public async Task Send_and_cancel_are_audited_and_list_filters_by_status()
    {
        await PaidFollower();
        var c = await Organizer();
        var sent = await CreateDraft(c);
        var draft = await CreateDraft(c, m => m["title"] = "ร่างอีกอัน");
        await Send(c, sent, 1);

        var list = await Body(await c.GetAsync(EventUrl("?status=Draft")));
        Assert.Equal(draft, list.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt32());
        Assert.Equal(2, (await Body(await c.GetAsync(EventUrl()))).GetProperty("total").GetInt32());
        await using var db = Pg.CreateDbContext();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "broadcast.send" && a.EntityId == sent.ToString()));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "broadcast.create" && a.EntityId == draft.ToString()));
    }
}
