using System.Net;
using System.Security.Cryptography;
using System.Text;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class LineWebhookTests(PostgresFixture pg) : LineTestBase(pg)
{
    private static string Sign(string body, string secret = ApiFactory.TestLineSecret) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    private Task<HttpResponseMessage> Post(string body, string? signature)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/line/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (signature is not null) req.Headers.Add("X-Line-Signature", signature);
        return Client.SendAsync(req);
    }

    private static string Events(string type, string userId) =>
        $$"""{ "destination": "Uxxx", "events": [ { "type": "{{type}}", "source": { "type": "user", "userId": "{{userId}}" } } ] }""";

    private async Task<(bool Following, DateTime? Followed, DateTime? Unfollowed, int Count)> Account(string userId)
    {
        await using var db = Pg.CreateDbContext();
        var rows = await db.LineAccounts.AsNoTracking().Where(a => a.LineUserId == userId).ToListAsync();
        var a = rows.SingleOrDefault();
        return (a?.IsFollowing ?? false, a?.FollowedAt, a?.UnfollowedAt, rows.Count);
    }

    [Fact]
    public async Task Missing_wrong_or_malformed_signature_is_rejected_and_changes_nothing()
    {
        var user = NewLineUser();
        var body = Events("follow", user);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(body, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(body, Sign(body, "wrong-secret"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(body, "not-base64!!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(body, Sign(body + " "))).StatusCode);   // body ถูกแก้
        Assert.Equal(0, (await Account(user)).Count);
    }

    [Fact]
    public async Task Follow_creates_the_account_as_following_and_unfollow_flips_it()
    {
        var user = NewLineUser();
        var follow = Events("follow", user);
        Assert.Equal(HttpStatusCode.OK, (await Post(follow, Sign(follow))).StatusCode);
        var after = await Account(user);
        Assert.True(after.Following);
        Assert.NotNull(after.Followed);

        var unfollow = Events("unfollow", user);
        Assert.Equal(HttpStatusCode.OK, (await Post(unfollow, Sign(unfollow))).StatusCode);
        var gone = await Account(user);
        Assert.False(gone.Following);
        Assert.NotNull(gone.Unfollowed);

        Assert.Equal(HttpStatusCode.OK, (await Post(follow, Sign(follow))).StatusCode);   // กลับมาเป็นเพื่อนอีกครั้ง
        Assert.True((await Account(user)).Following);
    }

    [Fact]
    public async Task Repeated_and_concurrent_follow_events_keep_a_single_row()
    {
        var user = NewLineUser();
        var body = Events("follow", user);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Post(body, Sign(body))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, (await Account(user)).Count);
    }

    [Fact]
    public async Task Unfollow_of_an_unknown_user_is_recorded_without_error()
    {
        var user = NewLineUser();
        var body = Events("unfollow", user);
        Assert.Equal(HttpStatusCode.OK, (await Post(body, Sign(body))).StatusCode);
        Assert.False((await Account(user)).Following);
    }

    [Theory]
    [InlineData("""{ "destination": "Uxxx", "events": [] }""")]                                    // ปุ่ม Verify ของ LINE
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{ "events": [ "x", 5, null, { "type": "message" }, { "type": "follow" }, { "type": "follow", "source": { "userId": 5 } } ] }""")]
    public async Task Valid_signature_with_odd_bodies_is_acknowledged(string body) =>
        Assert.Equal(HttpStatusCode.OK, (await Post(body, Sign(body))).StatusCode);
}
