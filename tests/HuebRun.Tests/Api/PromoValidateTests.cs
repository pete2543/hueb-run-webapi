using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class PromoValidateTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private Task<HttpResponseMessage> Validate(string? code, params int[] distanceIds) =>
        Client.PostAsJsonAsync($"/api/v1/events/{Ev.Slug}/promo/validate", new ValidatePromoRequest(code, [.. distanceIds]), Json);

    [Fact]
    public async Task Valid_code_returns_discount_for_server_computed_subtotal()
    {
        await AddPromoAsync();
        var res = await Validate("EARLY10", FiveK.Id, FiveK.Id, TenK.Id);   // 400 + 400 + 700
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var quote = (await res.Content.ReadFromJsonAsync<PromoQuote>(Json))!;
        Assert.Equal(new PromoQuote("EARLY10", 1500m, 150m), quote);
    }

    [Fact]
    public async Task Code_is_trimmed_and_case_insensitive()
    {
        await AddPromoAsync();
        var quote = (await (await Validate("  early10 ", FiveK.Id)).Content.ReadFromJsonAsync<PromoQuote>(Json))!;
        Assert.Equal("EARLY10", quote.Code);
        Assert.Equal(40m, quote.DiscountAmount);
    }

    [Fact]
    public async Task Validate_does_not_consume_a_use()
    {
        var promo = await AddPromoAsync(p => p.MaxUses = 1);
        await Validate("EARLY10", FiveK.Id);
        await using var db = Pg.CreateDbContext();
        Assert.Equal(0, (await db.PromoCodes.SingleAsync(p => p.Id == promo.Id)).UsedCount);
    }

    [Fact]
    public async Task Unknown_code_is_invalid_not_found()
    {
        var body = await AssertError(await Validate("NOPE", FiveK.Id), HttpStatusCode.UnprocessableEntity, "PROMO_INVALID");
        Assert.Equal("NOT_FOUND", body.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Code_from_another_event_is_not_found()
    {
        await using (var db = Pg.CreateDbContext())
        {
            var other = await TestData.CreateEventAsync(db);
            db.PromoCodes.Add(new PromoCode { EventId = other.Id, Code = "OTHER", Type = DiscountType.Fixed, Value = 50, IsActive = true });
            await db.SaveChangesAsync();
        }
        var body = await AssertError(await Validate("OTHER", FiveK.Id), HttpStatusCode.UnprocessableEntity, "PROMO_INVALID");
        Assert.Equal("NOT_FOUND", body.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Min_order_not_met()
    {
        await AddPromoAsync(p => p.MinOrderAmount = 1000);
        var body = await AssertError(await Validate("EARLY10", FiveK.Id), HttpStatusCode.UnprocessableEntity, "PROMO_INVALID");
        Assert.Equal("MIN_ORDER_NOT_MET", body.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Exhausted_code_is_conflict()
    {
        await AddPromoAsync(p => { p.MaxUses = 2; p.UsedCount = 2; });
        await AssertError(await Validate("EARLY10", FiveK.Id), HttpStatusCode.Conflict, "PROMO_EXHAUSTED");
    }

    [Fact]
    public async Task Distance_of_another_event_is_validation_error()
    {
        await AddPromoAsync();
        int otherDistanceId;
        await using (var db = Pg.CreateDbContext())
            otherDistanceId = (await TestData.CreateRegistrationEventAsync(db)).Distances[0].Id;
        var body = await AssertError(await Validate("EARLY10", otherDistanceId), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.True(body.GetProperty("details").TryGetProperty("distanceIds", out _));
    }

    [Fact]
    public async Task Empty_code_is_validation_error()
    {
        await AssertError(await Validate("  ", FiveK.Id), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
    }

    [Fact]
    public async Task Unknown_event_is_not_found()
    {
        var res = await Client.PostAsJsonAsync("/api/v1/events/no-such-event/promo/validate",
            new ValidatePromoRequest("EARLY10", [1]), Json);
        await AssertError(res, HttpStatusCode.NotFound, "EVENT_NOT_FOUND");
    }
}
