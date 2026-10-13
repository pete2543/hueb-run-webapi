using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Line;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class LineLinkTests(PostgresFixture pg) : LineTestBase(pg)
{
    private Task<HttpResponseMessage> Link(Guid orderId, string? idToken) =>
        Client.PostAsJsonAsync($"/api/v1/orders/{orderId}/line", new { idToken });

    private async Task<string> Token(string? name = "สมชาย")
    {
        var user = NewLineUser();
        var token = "tok-" + user;
        Factory.LineIdentity.Tokens[token] = new LineIdentity(user, name, "https://img.test/p.png");
        return token;
    }

    private async Task<(int? AccountId, string? UserId, string? Name)> LinkedAccount(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        var o = await db.Orders.AsNoTracking().Include(x => x.LineAccount).SingleAsync(x => x.Id == orderId);
        return (o.LineAccountId, o.LineAccount?.LineUserId, o.LineAccount?.DisplayName);
    }

    [Fact]
    public async Task Valid_token_links_the_order_and_stores_the_profile_without_marking_it_following()
    {
        var order = await CreateOrderOk();
        var res = await Link(order.OrderId, await Token());
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var linked = await LinkedAccount(order.OrderId);
        Assert.NotNull(linked.AccountId);
        Assert.Equal("สมชาย", linked.Name);
        await using var db = Pg.CreateDbContext();
        Assert.False((await db.LineAccounts.AsNoTracking().SingleAsync(a => a.Id == linked.AccountId)).IsFollowing);   // รู้จาก webhook เท่านั้น
    }

    [Fact]
    public async Task Linking_again_with_the_same_line_user_is_fine_and_a_different_user_is_refused()
    {
        var order = await CreateOrderOk();
        var token = await Token();
        Assert.Equal(HttpStatusCode.NoContent, (await Link(order.OrderId, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Link(order.OrderId, token)).StatusCode);

        var other = await Link(order.OrderId, await Token("คนอื่น"));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("LINE_ALREADY_LINKED", (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task One_line_user_can_link_several_orders_with_a_single_account_row()
    {
        var a = await CreateOrderOk();
        var b = await CreateOrderOk(Request(Runner(firstName: "สอง")));
        var token = await Token();
        await Link(a.OrderId, token);
        await Link(b.OrderId, token);
        Assert.Equal((await LinkedAccount(a.OrderId)).AccountId, (await LinkedAccount(b.OrderId)).AccountId);
    }

    [Fact]
    public async Task Bad_token_unknown_order_and_closed_orders_are_refused()
    {
        var order = await CreateOrderOk();
        foreach (var bad in new string?[] { null, "", "  ", "unknown-token" })
        {
            var res = await Link(order.OrderId, bad);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
        Assert.Null((await LinkedAccount(order.OrderId)).AccountId);
        Assert.Equal(HttpStatusCode.NotFound, (await Link(Guid.NewGuid(), await Token())).StatusCode);

        await using (var db = Pg.CreateDbContext())
            await db.Orders.Where(o => o.Id == order.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Cancelled));
        var closed = await Link(order.OrderId, await Token());
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
    }

    // ---------- สถานะ LINE ใน GET /orders/{id} ----------

    private async Task<(bool Linked, bool Following)> LineStatus(Guid orderId)
    {
        var line = (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{orderId}")).GetProperty("line");
        return (line.GetProperty("linked").GetBoolean(), line.GetProperty("following").GetBoolean());
    }

    [Fact]
    public async Task Order_shows_line_status_without_exposing_the_line_user_id()
    {
        var order = await CreateOrderOk();
        Assert.Equal((false, false), await LineStatus(order.OrderId));

        var token = await Token();
        await Link(order.OrderId, token);
        Assert.Equal((true, false), await LineStatus(order.OrderId));   // ผูกแล้วแต่ยังไม่เป็นเพื่อน

        await using (var db = Pg.CreateDbContext())
            await db.LineAccounts.Where(a => a.LineUserId == Factory.LineIdentity.Tokens[token].UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsFollowing, true));
        Assert.Equal((true, true), await LineStatus(order.OrderId));

        var raw = await Client.GetStringAsync($"/api/v1/orders/{order.OrderId}");
        Assert.DoesNotContain(Factory.LineIdentity.Tokens[token].UserId, raw);
    }

    // ---------- ส่ง "จ่ายสำเร็จ" ย้อนหลัง ----------

    [Fact]
    public async Task Linking_a_paid_order_while_following_sends_order_paid_late()
    {
        var order = await CreateOrderOk();
        await PayOrder(order);   // จ่ายตอนยังไม่ผูก → ยังไม่มีข้อความ
        var token = await Token();
        var userId = Factory.LineIdentity.Tokens[token].UserId;
        await AddLineAccount(userId, following: true);

        Assert.Equal(HttpStatusCode.NoContent, (await Link(order.OrderId, token)).StatusCode);

        var n = Assert.Single(await Outbox(userId));
        Assert.Equal(("order_paid", $"order_paid:{order.OrderId}"), (n.TemplateKey, n.DedupeKey));
        await Dispatch();
        Assert.Contains(order.OrderNo, Assert.Single(SentTo(userId)));
    }

    [Fact]
    public async Task Following_after_linking_a_paid_order_sends_order_paid_once()
    {
        var order = await CreateOrderOk();
        await PayOrder(order);
        var token = await Token();
        var userId = Factory.LineIdentity.Tokens[token].UserId;
        await Link(order.OrderId, token);   // ยังไม่เป็นเพื่อน → ยังส่งไม่ได้
        Assert.Empty(await Outbox(userId));

        for (var i = 0; i < 2; i++)   // follow ซ้ำ (บล็อก/ปลดบล็อก) ต้องไม่ส่งซ้ำ
            Assert.Equal(HttpStatusCode.OK, (await Follow(userId)).StatusCode);

        var n = Assert.Single(await Outbox(userId));
        Assert.Equal("order_paid", n.TemplateKey);
    }

    [Fact]
    public async Task Linking_or_following_for_an_unpaid_order_sends_nothing()
    {
        var order = await CreateOrderOk();
        var token = await Token();
        var userId = Factory.LineIdentity.Tokens[token].UserId;
        await Link(order.OrderId, token);
        await Follow(userId);
        Assert.Empty(await Outbox(userId));
    }

    [Fact]
    public async Task Paying_after_linking_and_then_relinking_does_not_duplicate_order_paid()
    {
        var order = await CreateOrderOk();
        var token = await Token();
        var userId = Factory.LineIdentity.Tokens[token].UserId;
        await AddLineAccount(userId, following: true);
        await Link(order.OrderId, token);
        await PayOrder(order);   // ส่งตามปกติตอนจ่าย
        await Link(order.OrderId, token);   // ผูกซ้ำ
        Assert.Single(await Outbox(userId));
    }

    private Task<HttpResponseMessage> Follow(string userId)
    {
        var body = $$"""{ "events": [ { "type": "follow", "source": { "type": "user", "userId": "{{userId}}" } } ] }""";
        var sig = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(ApiFactory.TestLineSecret), System.Text.Encoding.UTF8.GetBytes(body)));
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/line/webhook") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Line-Signature", sig);
        return Client.SendAsync(req);
    }
}
