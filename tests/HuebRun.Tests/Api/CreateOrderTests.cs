using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class CreateOrderTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private const int PhraBoromMahaRatchawang = 100101;   // ตำบลใน seed geo รหัส 10200

    private AddressInput Address(string postalCode = "10200") =>
        new("สมชาย ใจดี", "0812345678", "99/1 ถนนหน้าพระลาน", PhraBoromMahaRatchawang, postalCode);

    [Fact]
    public async Task Creates_group_order_with_server_side_pricing_and_reservations()
    {
        var promo = await AddPromoAsync();
        var request = Request(Runner(FiveK, M, "เอ"), Runner(TenK, M, "บี")) with
        {
            ShippingOptionId = Post.Id,
            ShippingAddress = Address(),
            PromoCode = "early10",
        };

        var res = await PostOrder(request);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = (await res.Content.ReadFromJsonAsync<CreatedOrder>(Json))!;
        Assert.Equal($"/api/v1/orders/{created.OrderId}", res.Headers.Location!.OriginalString);
        Assert.Matches(new Regex(@"^HR27-\d{6}$"), created.OrderNo);
        Assert.Equal(1050m, created.Total);                     // (400 + 700) − 10% + 60
        Assert.Equal(1050.01m, created.PayableAmount);
        Assert.Equal(new DateTime(2026, 11, 15, 0, 30, 0, DateTimeKind.Utc), created.ExpiresAt);

        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.Include(o => o.Registrations).SingleAsync(o => o.Id == created.OrderId);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal((1100m, 110m, 60m), (order.SubTotal, order.DiscountAmount, order.ShippingFee));
        Assert.Equal("0812345678", order.Phone);
        Assert.Equal(("กรุงเทพมหานคร", "เขตพระนคร", "พระบรมมหาราชวัง", "10200"),
            (order.ShipProvinceText, order.ShipDistrictText, order.ShipSubdistrictText, order.ShipPostalCode));
        Assert.Equal([400m, 700m], order.Registrations.OrderBy(r => r.Id).Select(r => r.PricePaid));
        Assert.All(order.Registrations, r => Assert.True(r.CheckInToken.Length >= 40));
        Assert.Equal(2, order.Registrations.Select(r => r.CheckInToken).Distinct().Count());

        Assert.Equal(1, (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
        Assert.Equal(1, (await db.EventDistances.SingleAsync(d => d.Id == TenK.Id)).ReservedCount);
        Assert.Equal(2, (await db.EventShirtSizes.SingleAsync(s => s.Id == M.Id)).ReservedCount);
        Assert.Equal(1, (await db.PromoCodes.SingleAsync(p => p.Id == promo.Id)).UsedCount);
    }

    [Fact]
    public async Task Pickup_order_has_no_shipping_snapshot()
    {
        var created = await CreateOrderOk();
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.SingleAsync(o => o.Id == created.OrderId);
        Assert.Null(order.ShipSubdistrictId);
        Assert.Null(order.ShipAddress);
        Assert.Equal(400m, created.Total);
    }

    [Fact]
    public async Task Unpublished_event_is_not_found()
    {
        await using (var db = Pg.CreateDbContext())
            await db.Events.Where(e => e.Id == Ev.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.IsPublished, false));
        await AssertError(await PostOrder(Request()), HttpStatusCode.NotFound, "EVENT_NOT_FOUND");
    }

    [Fact]
    public async Task Closed_registration_is_rejected()
    {
        await using (var db = Pg.CreateDbContext())
            await db.Events.Where(e => e.Id == Ev.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.RegistrationClosesAt, TestData.Utc(2026, 11, 1)));
        await AssertError(await PostOrder(Request()), HttpStatusCode.BadRequest, "REGISTRATION_CLOSED");
    }

    [Fact]
    public async Task Full_distance_is_conflict_and_nothing_is_reserved()
    {
        await using (var db = Pg.CreateDbContext())
            await db.EventDistances.Where(d => d.Id == TenK.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Quota, 1));

        var body = await AssertError(await PostOrder(Request(Runner(FiveK), Runner(TenK), Runner(TenK))),
            HttpStatusCode.Conflict, "QUOTA_FULL");
        Assert.Equal(TenK.Id, body.GetProperty("details").GetProperty("distanceId").GetInt32());

        await using var check = Pg.CreateDbContext();
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
        Assert.Equal(0, (await check.EventShirtSizes.SingleAsync(s => s.Id == M.Id)).ReservedCount);
        Assert.False(await check.Orders.AnyAsync(o => o.EventId == Ev.Id));
    }

    [Fact]
    public async Task Out_of_stock_shirt_is_conflict_and_distance_reservation_rolls_back()
    {
        await using (var db = Pg.CreateDbContext())
            await db.EventShirtSizes.Where(s => s.Id == L.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, 0));

        var body = await AssertError(await PostOrder(Request(Runner(FiveK, L))), HttpStatusCode.Conflict, "SHIRT_OUT_OF_STOCK");
        Assert.Equal("L", body.GetProperty("details").GetProperty("size").GetString());

        await using var check = Pg.CreateDbContext();
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
    }

    [Fact]
    public async Task Exhausted_promo_is_conflict()
    {
        await AddPromoAsync(p => { p.MaxUses = 1; p.UsedCount = 1; });
        await AssertError(await PostOrder(Request() with { PromoCode = "EARLY10" }), HttpStatusCode.Conflict, "PROMO_EXHAUSTED");
    }

    [Fact]
    public async Task Expired_promo_is_unprocessable()
    {
        await AddPromoAsync(p => p.EndsAt = TestData.Utc(2026, 11, 1));
        var body = await AssertError(await PostOrder(Request() with { PromoCode = "EARLY10" }),
            HttpStatusCode.UnprocessableEntity, "PROMO_INVALID");
        Assert.Equal("EXPIRED", body.GetProperty("details").GetProperty("reason").GetString());
    }

    private async Task AssertValidationError(CreateOrderRequest request, string field)
    {
        var body = await AssertError(await PostOrder(request), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.True(body.GetProperty("details").TryGetProperty(field, out _),
            $"expected details.{field} in {body.GetProperty("details")}");
    }

    [Fact]
    public Task Pdpa_must_be_accepted() => AssertValidationError(Request() with { AcceptedPdpa = false }, "acceptedPdpa");

    [Fact]
    public Task At_least_one_runner() => AssertValidationError(Request() with { Runners = [] }, "runners");

    [Fact]
    public Task At_most_ten_runners() =>
        AssertValidationError(Request() with { Runners = [.. Enumerable.Range(0, 11).Select(_ => Runner())] }, "runners");

    [Fact]
    public async Task Distance_of_another_event_is_rejected()
    {
        int otherDistanceId;
        await using (var db = Pg.CreateDbContext())
            otherDistanceId = (await TestData.CreateRegistrationEventAsync(db)).Distances[0].Id;
        await AssertValidationError(Request(Runner() with { DistanceId = otherDistanceId }), "runners[0].distanceId");
    }

    [Fact]
    public Task Contact_phone_must_be_9_or_10_digits() =>
        AssertValidationError(Request() with { Contact = new ContactInput("ก", "12345", null) }, "contact.phone");

    [Fact]
    public Task Birth_date_cannot_be_in_future() =>
        AssertValidationError(Request(Runner() with { BirthDate = new DateOnly(2027, 1, 1) }), "runners[0].birthDate");

    [Fact]
    public Task Emergency_contact_is_required() =>
        AssertValidationError(Request(Runner() with { EmergencyContact = null }), "runners[0].emergencyContact");

    [Fact]
    public Task Post_requires_address() =>
        AssertValidationError(Request() with { ShippingOptionId = Post.Id }, "shippingAddress");

    [Fact]
    public Task Postal_code_must_match_subdistrict() =>
        AssertValidationError(Request() with { ShippingOptionId = Post.Id, ShippingAddress = Address("10500") },
            "shippingAddress.postalCode");

    [Fact]
    public async Task Inactive_shipping_option_is_rejected()
    {
        await using (var db = Pg.CreateDbContext())
            await db.ShippingOptions.Where(s => s.Id == Pickup.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await AssertValidationError(Request(), "shippingOptionId");
    }

    [Fact]
    public async Task Empty_body_is_validation_error()
    {
        var res = await Client.PostAsync($"/api/v1/events/{Ev.Slug}/orders",
            new StringContent("", System.Text.Encoding.UTF8, "application/json"));
        await AssertError(res, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
    }
}
