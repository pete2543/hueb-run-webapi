using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class OrderQueryTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private async Task<(JsonElement Body, string Raw)> GetOrder(Guid id)
    {
        var res = await Client.GetAsync($"/api/v1/orders/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var raw = await res.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(raw).RootElement, raw);
    }

    [Fact]
    public async Task Returns_order_summary_without_sensitive_fields()
    {
        var created = await CreateOrderOk(Request(Runner(FiveK, M, "เอ"), Runner(TenK, L, "บี")));
        var (body, raw) = await GetOrder(created.OrderId);

        Assert.Equal(created.OrderNo, body.GetProperty("orderNo").GetString());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(1100m, body.GetProperty("total").GetDecimal());
        Assert.Equal(created.PayableAmount, body.GetProperty("payableAmount").GetDecimal());
        Assert.Equal("0812345678", body.GetProperty("contact").GetProperty("phone").GetString());
        Assert.Equal("Pickup", body.GetProperty("shipping").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("shipping").GetProperty("address").ValueKind);

        var runners = body.GetProperty("runners").EnumerateArray().ToList();
        Assert.Equal(["เอ", "บี"], runners.Select(r => r.GetProperty("firstName").GetString()));
        Assert.Equal("10K", runners[1].GetProperty("distance").GetProperty("name").GetString());
        Assert.Equal("L", runners[1].GetProperty("shirtSize").GetProperty("size").GetString());
        Assert.Equal(JsonValueKind.Null, runners[0].GetProperty("bibNumber").ValueKind);

        Assert.DoesNotContain("medical", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("แพ้ยา", raw);
        Assert.DoesNotContain("emergency", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("สมหญิง", raw);
        Assert.DoesNotContain("checkInToken", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pending_order_past_expiry_shows_expired()
    {
        var created = await CreateOrderOk();
        Factory.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("Expired", (await GetOrder(created.OrderId)).Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_order_is_not_found()
    {
        await AssertError(await Client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "ORDER_NOT_FOUND");
        await AssertError(await Client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}/tickets"), HttpStatusCode.NotFound, "ORDER_NOT_FOUND");
    }

    [Fact]
    public async Task Tickets_require_paid_order()
    {
        var created = await CreateOrderOk();
        var body = await AssertError(await Client.GetAsync($"/api/v1/orders/{created.OrderId}/tickets"),
            HttpStatusCode.Conflict, "ORDER_NOT_PAID");
        Assert.Equal("Pending", body.GetProperty("details").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Paid_order_returns_tickets_with_bib_and_checkin_token()
    {
        var created = await CreateOrderOk(Request(Runner(FiveK, M, "เอ"), Runner(TenK, M, "บี")));
        await using (var db = Pg.CreateDbContext())
            await new OrderPaymentService(db).MarkPaidAsync(created.OrderId, TestData.Utc(2026, 11, 15));

        var res = await Client.GetAsync($"/api/v1/orders/{created.OrderId}/tickets");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var tickets = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(["B0001", "C0001"], tickets.Select(t => t.GetProperty("bibNumber").GetString()));
        Assert.Equal("5K", tickets[0].GetProperty("distanceName").GetString());
        Assert.All(tickets, t => Assert.True(t.GetProperty("checkInToken").GetString()!.Length >= 40));

        Assert.Equal("B0001", (await GetOrder(created.OrderId)).Body.GetProperty("runners")[0].GetProperty("bibNumber").GetString());
    }
}
