using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminDashboardTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private string Url => $"/api/v1/admin/events/{Ev.Id}/dashboard";

    private async Task<JsonElement> Get(string role = AdminRoles.Organizer) =>
        await (await AdminClient(role, Ev.Id)).GetFromJsonAsync<JsonElement>(Url);

    private async Task SetStatus(Guid orderId, OrderStatus status)
    {
        await using var db = Pg.CreateDbContext();
        await db.Orders.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, status)
            .SetProperty(o => o.PaidAt, status == OrderStatus.Paid ? DateTime.UtcNow : (DateTime?)null));
    }

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task Role_matrix(string role, HttpStatusCode expected) =>
        Assert.Equal(expected, (await (await AdminClient(role, Ev.Id)).GetAsync(Url)).StatusCode);

    [Fact]
    public async Task Anonymous_401_no_access_403_unknown_event_404()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await AdminClient(AdminRoles.Organizer)).GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await AdminClient(AdminRoles.SuperAdmin)).GetAsync("/api/v1/admin/events/999999/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Empty_event_has_zeroes_and_full_capacity()
    {
        var d = await Get();
        Assert.Equal(0, d.GetProperty("orders").GetProperty("total").GetInt32());
        Assert.Equal(0m, d.GetProperty("revenue").GetProperty("paid").GetDecimal());
        var five = d.GetProperty("distances").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "5K");
        Assert.Equal((100, 0, 100), (five.GetProperty("quota").GetInt32(), five.GetProperty("reserved").GetInt32(), five.GetProperty("remaining").GetInt32()));
    }

    [Fact]
    public async Task Counts_orders_runners_revenue_and_capacity_by_status()
    {
        var paid = await CreateOrderOk(Request(Runner(FiveK, M), Runner(TenK, L, "สอง")));
        var pending = await CreateOrderOk(Request(Runner(FiveK, M, "สาม")));
        var cancelled = await CreateOrderOk(Request(Runner(FiveK, M, "สี่")));
        await SetStatus(paid.OrderId, OrderStatus.Paid);
        await SetStatus(cancelled.OrderId, OrderStatus.Cancelled);
        await using (var db = Pg.CreateDbContext())
        {
            db.Payments.AddRange(
                new Payment { OrderId = pending.OrderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.NeedsReview, ExpectedAmount = 1, CreatedAt = DateTime.UtcNow },
                new Payment { OrderId = paid.OrderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.Verified, ExpectedAmount = 1, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var d = await Get(AdminRoles.Finance);

        var o = d.GetProperty("orders");
        Assert.Equal((3, 1, 1, 0, 1), (o.GetProperty("total").GetInt32(), o.GetProperty("paid").GetInt32(), o.GetProperty("pending").GetInt32(),
            o.GetProperty("expired").GetInt32(), o.GetProperty("cancelled").GetInt32()));
        Assert.Equal((2, 1), (d.GetProperty("paidRunners").GetInt32(), d.GetProperty("pendingRunners").GetInt32()));
        Assert.Equal((1, 0), (d.GetProperty("payments").GetProperty("needsReview").GetInt32(), d.GetProperty("payments").GetProperty("verifying").GetInt32()));
        Assert.Equal(paid.PayableAmount, d.GetProperty("revenue").GetProperty("paid").GetDecimal());
        Assert.Equal(pending.PayableAmount, d.GetProperty("revenue").GetProperty("pendingAmount").GetDecimal());

        var five = d.GetProperty("distances").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "5K");
        Assert.Equal((1, 1), (five.GetProperty("paidRunners").GetInt32(), five.GetProperty("pendingRunners").GetInt32()));
        Assert.Equal(3, five.GetProperty("reserved").GetInt32());   // ค่าจองจริงในตาราง (ไม่ได้คืนตอนตั้ง Cancelled ตรง ๆ)
        var m = d.GetProperty("shirts").EnumerateArray().Single(x => x.GetProperty("size").GetString() == "M");
        Assert.Equal(1, m.GetProperty("paidRunners").GetInt32());
    }

    [Fact]
    public async Task Overdue_pending_orders_count_as_expired_not_pending()
    {
        var created = await CreateOrderOk();
        Factory.Time.Advance(TimeSpan.FromMinutes(31));
        var d = await Get();
        Assert.Equal((0, 1), (d.GetProperty("orders").GetProperty("pending").GetInt32(), d.GetProperty("orders").GetProperty("expired").GetInt32()));
        Assert.Equal(0, d.GetProperty("pendingRunners").GetInt32());
        Assert.Equal(0m, d.GetProperty("revenue").GetProperty("pendingAmount").GetDecimal());
        Assert.NotEqual(Guid.Empty, created.OrderId);
    }

    [Fact]
    public async Task Does_not_include_other_events_orders()
    {
        await using (var db = Pg.CreateDbContext())
            await TestData.CreateRegistrationEventAsync(db);   // งานอื่น
        await CreateOrderOk();
        Assert.Equal(1, (await Get()).GetProperty("orders").GetProperty("total").GetInt32());
    }
}
