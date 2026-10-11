using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Tests.Infrastructure;
using static HuebRun.Tests.Infrastructure.TestData;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class EventsEndpointTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg.ConnectionString);   // เวลา = 2026-11-15T00:00Z
    private HttpClient Client => _factory.CreateClient();
    public void Dispose() => _factory.Dispose();

    private async Task<Event> SeedEventAsync(Action<Event>? configure = null)
    {
        await using var db = pg.CreateDbContext();
        return await CreateEventAsync(db, configure);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AssertEventNotFound(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EVENT_NOT_FOUND", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_event_returns_public_fields_without_promptpay()
    {
        var ev = await SeedEventAsync();
        var res = await Client.GetAsync($"/api/v1/events/{ev.Slug}");
        var raw = await res.Content.ReadAsStringAsync();
        var body = await Json(res);

        Assert.Equal(ev.Id, body.GetProperty("id").GetInt32());
        Assert.Equal("Test Run", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("registrationOpen").GetBoolean());
        Assert.DoesNotContain("promptPay", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET ACCOUNT", raw);
    }

    [Fact]
    public async Task Event_times_are_serialized_as_utc()
    {
        var ev = await SeedEventAsync();
        var body = await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}"));
        Assert.Equal("2026-10-01T00:00:00Z", body.GetProperty("registrationOpensAt").GetString());
    }

    [Fact]
    public async Task Registration_closed_after_close_time()
    {
        var ev = await SeedEventAsync(e => e.RegistrationClosesAt = Utc(2026, 11, 1));
        var body = await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}"));
        Assert.False(body.GetProperty("registrationOpen").GetBoolean());
    }

    [Fact]
    public async Task Slug_lookup_is_case_insensitive()
    {
        var ev = await SeedEventAsync();
        var res = await Client.GetAsync($"/api/v1/events/{ev.Slug.ToUpperInvariant()}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/distances")]
    [InlineData("/shirt-sizes")]
    [InlineData("/shipping-options")]
    public async Task Unpublished_event_is_not_found_on_every_route(string suffix)
    {
        var ev = await SeedEventAsync(e => e.IsPublished = false);
        await AssertEventNotFound(await Client.GetAsync($"/api/v1/events/{ev.Slug}{suffix}"));
    }

    [Fact]
    public async Task Unknown_slug_is_not_found()
    {
        await AssertEventNotFound(await Client.GetAsync("/api/v1/events/no-such-event"));
    }

    [Fact]
    public async Task Distances_show_effective_price_and_remaining_sorted_by_km()
    {
        var ev = await SeedEventAsync(e =>
        {
            e.Distances.Add(new EventDistance { Name = "10K", DistanceKm = 10, BibPrefix = "C", Quota = 50, ReservedCount = 50, Price = 650 });
            e.Distances.Add(new EventDistance { Name = "3K", DistanceKm = 3, BibPrefix = "A", Quota = 100, ReservedCount = 30,
                Price = 450, EarlyBirdPrice = 390, EarlyBirdEndsAt = Utc(2026, 11, 30) });
        });

        var body = await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}/distances"));
        var items = body.EnumerateArray().ToList();

        Assert.Equal(["3K", "10K"], items.Select(i => i.GetProperty("name").GetString()));
        Assert.Equal(390m, items[0].GetProperty("price").GetDecimal());
        Assert.Equal(450m, items[0].GetProperty("regularPrice").GetDecimal());
        Assert.True(items[0].GetProperty("isEarlyBird").GetBoolean());
        Assert.Equal(70, items[0].GetProperty("remaining").GetInt32());
        Assert.Equal(650m, items[1].GetProperty("price").GetDecimal());
        Assert.False(items[1].GetProperty("isEarlyBird").GetBoolean());
        Assert.Equal(0, items[1].GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task Distances_use_regular_price_after_early_bird_cutoff()
    {
        var ev = await SeedEventAsync(e => e.Distances.Add(new EventDistance { Name = "5K", DistanceKm = 5, BibPrefix = "B",
            Quota = 10, Price = 550, EarlyBirdPrice = 490, EarlyBirdEndsAt = Utc(2026, 11, 30) }));
        _factory.Time.SetUtcNow(new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero));

        var item = (await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}/distances"))).EnumerateArray().Single();
        Assert.Equal(550m, item.GetProperty("price").GetDecimal());
        Assert.False(item.GetProperty("isEarlyBird").GetBoolean());
    }

    [Fact]
    public async Task Shirt_sizes_show_remaining()
    {
        var ev = await SeedEventAsync(e =>
        {
            e.ShirtSizes.Add(new EventShirtSize { Size = "S", Detail = "รอบอก 36 นิ้ว", Stock = 20, ReservedCount = 5 });
            e.ShirtSizes.Add(new EventShirtSize { Size = "M", Stock = 10, ReservedCount = 10 });
        });

        var items = (await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}/shirt-sizes"))).EnumerateArray().ToList();
        Assert.Equal(["S", "M"], items.Select(i => i.GetProperty("size").GetString()));
        Assert.Equal("รอบอก 36 นิ้ว", items[0].GetProperty("detail").GetString());
        Assert.Equal(15, items[0].GetProperty("remaining").GetInt32());
        Assert.Equal(0, items[1].GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task Shipping_options_hide_inactive_and_use_string_type()
    {
        var ev = await SeedEventAsync(e =>
        {
            e.ShippingOptions.Add(new ShippingOption { Type = ShippingType.Pickup, Name = "รับเอง", Fee = 0 });
            e.ShippingOptions.Add(new ShippingOption { Type = ShippingType.Post, Name = "EMS", Fee = 60 });
            e.ShippingOptions.Add(new ShippingOption { Type = ShippingType.Post, Name = "ปิดแล้ว", Fee = 40, IsActive = false });
        });

        var items = (await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}/shipping-options"))).EnumerateArray().ToList();
        Assert.Equal(["รับเอง", "EMS"], items.Select(i => i.GetProperty("name").GetString()));
        Assert.Equal("Pickup", items[0].GetProperty("type").GetString());
        Assert.Equal(60m, items[1].GetProperty("fee").GetDecimal());
    }
}
