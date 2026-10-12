using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HuebRun.Tests.Infrastructure;

/// ฐานของ test การสมัคร: สร้างงานใหม่ต่อ test (slug ไม่ซ้ำ) และ helper สร้างคำขอ เวลา = 2026-11-15T00:00Z
public abstract class OrderTestBase(PostgresFixture pg) : IAsyncLifetime
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected PostgresFixture Pg { get; } = pg;
    protected ApiFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;
    protected Event Ev { get; private set; } = null!;
    protected EventDistance FiveK => Ev.Distances.Single(d => d.Name == "5K");
    protected EventDistance TenK => Ev.Distances.Single(d => d.Name == "10K");
    protected EventShirtSize M => Ev.ShirtSizes.Single(s => s.Size == "M");
    protected EventShirtSize L => Ev.ShirtSizes.Single(s => s.Size == "L");
    protected ShippingOption Pickup => Ev.ShippingOptions.Single(s => s.Type == ShippingType.Pickup);
    protected ShippingOption Post => Ev.ShippingOptions.Single(s => s.Type == ShippingType.Post);
    protected int ThaiId { get; private set; }

    protected virtual void ConfigureEvent(Event e) { }

    public async Task InitializeAsync()
    {
        Factory = new ApiFactory(Pg.ConnectionString);
        Client = Factory.CreateClient();   // เริ่มแอป → seed lookups/geo
        await using var db = Pg.CreateDbContext();
        Ev = await TestData.CreateRegistrationEventAsync(db, ConfigureEvent);
        ThaiId = await db.Nationalities.Where(n => n.NameEn == "Thai").Select(n => n.Id).SingleAsync();
    }

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }

    protected RunnerInput Runner(EventDistance? distance = null, EventShirtSize? shirt = null, string firstName = "สมชาย") => new(
        (distance ?? FiveK).Id, (shirt ?? M).Id,
        TitleId: null, FirstName: firstName, LastName: "ใจดี", Gender: Gender.Male,
        BirthDate: new DateOnly(1990, 1, 1), NationalityId: ThaiId, BloodTypeId: null,
        Phone: null, Email: null, TeamName: null,
        EmergencyContact: new EmergencyContactInput(null, "สมหญิง ใจดี", "0812345678"),
        MedicalNote: "แพ้ยาเพนิซิลลิน");

    protected CreateOrderRequest Request(params RunnerInput[] runners) => new(
        new ContactInput("สมชาย ใจดี", "081-234-5678", "somchai@example.com"),
        runners.Length == 0 ? [Runner()] : [.. runners],
        Pickup.Id, ShippingAddress: null, PromoCode: null, AcceptedPdpa: true);

    /// client ที่ login เป็นแอดมินบทบาท/งานที่ระบุ
    protected const string Password = "correct-horse-battery";

    protected async Task<HttpClient> AdminClient(string role, params int[] eventIds)
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
            var user = new AdminUser
            {
                Id = Guid.NewGuid(), UserName = email, Email = email, DisplayName = role, IsActive = true, LockoutEnabled = true,
                CreatedAt = DateTime.UtcNow, EventAccesses = [.. eventIds.Select(e => new AdminEventAccess { EventId = e })],
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            var login = await Factory.CreateClient().PostAsJsonAsync("/api/v1/admin/auth/login", new { email, password = Password });
            var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
    }

    protected Task<HttpResponseMessage> PostOrder(CreateOrderRequest request) =>
        Client.PostAsJsonAsync($"/api/v1/events/{Ev.Slug}/orders", request, Json);

    protected async Task<CreatedOrder> CreateOrderOk(CreateOrderRequest? request = null)
    {
        var res = await PostOrder(request ?? Request());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<CreatedOrder>(Json))!;
    }

    protected async Task<PromoCode> AddPromoAsync(Action<PromoCode>? configure = null)
    {
        await using var db = Pg.CreateDbContext();
        var promo = new PromoCode { EventId = Ev.Id, Code = "EARLY10", Type = DiscountType.Percent, Value = 10, IsActive = true };
        configure?.Invoke(promo);
        db.PromoCodes.Add(promo);
        await db.SaveChangesAsync();
        return promo;
    }

    protected static async Task<JsonElement> AssertError(HttpResponseMessage res, HttpStatusCode status, string code)
    {
        Assert.Equal(status, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }
}
