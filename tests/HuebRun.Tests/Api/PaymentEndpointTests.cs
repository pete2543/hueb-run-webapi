using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class PaymentEndpointTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private Task<HttpResponseMessage> Upload(Guid orderId, byte[] bytes, string name = "slip.png") =>
        Client.PostAsync($"/api/v1/orders/{orderId}/payment/slip",
            new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } });

    [Fact]
    public async Task Get_payment_returns_qr_payload_and_amount()
    {
        var created = await CreateOrderOk();
        var res = await Client.GetAsync($"/api/v1/orders/{created.OrderId}/payment");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(created.PayableAmount, body.GetProperty("amount").GetDecimal());
        Assert.StartsWith("000201010212", body.GetProperty("qrPayload").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("payment").ValueKind);
        Assert.False(body.TryGetProperty("promptPayId", out _));
    }

    [Fact]
    public async Task Valid_slip_returns_200_and_order_shows_paid_payment()
    {
        var created = await CreateOrderOk();
        Factory.Verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));

        var res = await Upload(created.OrderId, Slips.Png(1));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Verified", "Paid"), (body.GetProperty("status").GetString(), body.GetProperty("orderStatus").GetString()));

        var order = await (await Client.GetAsync($"/api/v1/orders/{created.OrderId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Paid", order.GetProperty("status").GetString());
        Assert.Equal("Verified", order.GetProperty("payment").GetProperty("status").GetString());
        Assert.Single(Factory.Storage.Objects);
    }

    [Fact]
    public async Task Delayed_slip_returns_202_verifying()
    {
        var created = await CreateOrderOk();
        Factory.Verifier.Enqueue(new SlipCheckResult.Retry(TimeSpan.FromMinutes(8), "{}"));
        var res = await Upload(created.OrderId, Slips.Png(1));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal("Verifying", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Rejected_slip_returns_422_with_reason()
    {
        var created = await CreateOrderOk();
        Factory.Verifier.Enqueue(new SlipCheckResult.Rejected(PaymentRejectReason.AmountMismatch, "{}"));
        var body = await AssertError(await Upload(created.OrderId, Slips.Png(1)), HttpStatusCode.UnprocessableEntity, "SLIP_REJECTED");
        Assert.Equal("AMOUNT_MISMATCH", body.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Missing_or_invalid_file_is_400()
    {
        var created = await CreateOrderOk();
        var empty = await Client.PostAsync($"/api/v1/orders/{created.OrderId}/payment/slip", new MultipartFormDataContent());
        await AssertError(empty, HttpStatusCode.BadRequest, "VALIDATION_ERROR");

        var gif = await AssertError(await Upload(created.OrderId, [.. "GIF89a"u8, 1], "slip.jpg"), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.True(gif.GetProperty("details").TryGetProperty("file", out _));
        Assert.Equal(0, Factory.Verifier.Calls);
    }

    [Fact]
    public async Task Free_order_payment_page_is_already_paid()
    {
        await AddPromoAsync(p => { p.Code = "FREE100"; p.Value = 100; });
        var created = await CreateOrderOk(Request() with { PromoCode = "FREE100" });
        await AssertError(await Client.GetAsync($"/api/v1/orders/{created.OrderId}/payment"), HttpStatusCode.Conflict, "ORDER_ALREADY_PAID");
    }
}
