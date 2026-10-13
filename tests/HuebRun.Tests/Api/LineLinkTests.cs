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
}
