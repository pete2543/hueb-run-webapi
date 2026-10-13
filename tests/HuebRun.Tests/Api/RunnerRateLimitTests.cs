using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class RunnerRateLimitTests(PostgresFixture pg) : OrderTestBase(pg)
{
    protected override IReadOnlyDictionary<string, string>? Settings => new Dictionary<string, string>
    {
        ["RateLimits:OrdersPerWindow"] = "2",
        ["RateLimits:SlipsPerWindow"] = "3",
        ["RateLimits:PromoChecksPerWindow"] = "4",
        ["RateLimits:WindowMinutes"] = "10",
    };

    private Task<HttpResponseMessage> PostOrderFrom(string ip, string firstName)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{Ev.Slug}/orders") { Content = JsonContent.Create(Request(Runner(firstName: firstName)), options: Json) };
        req.Headers.Add("X-Forwarded-For", ip);
        return Client.SendAsync(req);
    }

    private Task<HttpResponseMessage> UploadFrom(string ip, Guid orderId, int seed)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/payment/slip")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(Slips.Png(seed)), "file", "slip.png" } },
        };
        req.Headers.Add("X-Forwarded-For", ip);
        return Client.SendAsync(req);
    }

    [Fact]
    public async Task Creating_orders_is_limited_per_ip_with_429_and_retry_after()
    {
        Assert.Equal(HttpStatusCode.Created, (await PostOrderFrom("203.0.113.1", "a")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostOrderFrom("203.0.113.1", "b")).StatusCode);

        var blocked = await PostOrderFrom("203.0.113.1", "c");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("TOO_MANY_REQUESTS", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.InRange(int.Parse(blocked.Headers.GetValues("Retry-After").Single()), 1, 600);
    }

    [Fact]
    public async Task Another_ip_has_its_own_budget()
    {
        for (var i = 0; i < 3; i++) await PostOrderFrom("203.0.113.1", "x" + i);
        Assert.Equal(HttpStatusCode.Created, (await PostOrderFrom("203.0.113.2", "y")).StatusCode);
    }

    [Fact]
    public async Task Blocked_requests_do_not_create_orders_or_reserve_quota()
    {
        for (var i = 0; i < 5; i++) await PostOrderFrom("203.0.113.9", "z" + i);
        await using var db = Pg.CreateDbContext();
        Assert.Equal(2, db.Orders.Count(o => o.EventId == Ev.Id));
        Assert.Equal(2, db.EventDistances.Single(d => d.Id == FiveK.Id).ReservedCount);
    }

    [Fact]
    public async Task Slip_uploads_are_limited_per_ip_before_reaching_the_verifier()
    {
        var order = await CreateOrderOk();   // ไม่นับ: ใช้ IP อื่นของ client เริ่มต้น
        for (var i = 0; i < 3; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await UploadFrom("198.51.100.7", order.OrderId, i)).StatusCode);
        var callsBefore = Factory.Verifier.Calls;

        var blocked = await UploadFrom("198.51.100.7", order.OrderId, 99);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(callsBefore, Factory.Verifier.Calls);
    }

    [Fact]
    public async Task Reading_orders_and_payment_info_is_not_limited()
    {
        var order = await CreateOrderOk();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/v1/orders/{order.OrderId}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/v1/orders/{order.OrderId}/payment")).StatusCode);
        }
    }

    private Task<HttpResponseMessage> ValidatePromoFrom(string ip, string code)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{Ev.Slug}/promo/validate")
            { Content = JsonContent.Create(new { code, distanceIds = new[] { FiveK.Id } }) };
        req.Headers.Add("X-Forwarded-For", ip);
        return Client.SendAsync(req);
    }

    [Fact]
    public async Task Guessing_promo_codes_is_limited_per_ip_and_does_not_eat_the_order_budget()
    {
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ValidatePromoFrom("192.0.2.5", "GUESS" + i)).StatusCode);   // ไม่มีโค้ดนี้ แต่ยังไม่ถูกบล็อก

        var blocked = await ValidatePromoFrom("192.0.2.5", "GUESS-LAST");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("TOO_MANY_REQUESTS", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.True(blocked.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.Created, (await PostOrderFrom("192.0.2.5", "ยังสมัครได้")).StatusCode);   // คนละถังกับสร้างออเดอร์
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await ValidatePromoFrom("192.0.2.6", "OTHER")).StatusCode);   // IP อื่นไม่โดน
    }
}
