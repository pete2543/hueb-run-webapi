using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminOrdersTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private string ListUrl(string query = "") => $"/api/v1/admin/events/{Ev.Id}/orders{query}";

    private async Task<JsonElement> List(HttpClient client, string query = "")
    {
        var res = await client.GetAsync(ListUrl(query));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] OrderNos(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("orderNo").GetString()!)];

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task Role_matrix_for_list_and_detail(string role, HttpStatusCode expected)
    {
        var created = await CreateOrderOk();
        var client = await AdminClient(role, Ev.Id);
        Assert.Equal(expected, (await client.GetAsync(ListUrl())).StatusCode);
        Assert.Equal(expected, (await client.GetAsync($"/api/v1/admin/orders/{created.OrderId}")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_gets_401_and_other_events_get_403()
    {
        var created = await CreateOrderOk();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(ListUrl())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/api/v1/admin/orders/{created.OrderId}")).StatusCode);

        var noAccess = await AdminClient(AdminRoles.Organizer);
        Assert.Equal(HttpStatusCode.Forbidden, (await noAccess.GetAsync(ListUrl())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noAccess.GetAsync($"/api/v1/admin/orders/{created.OrderId}")).StatusCode);
    }

    [Fact]
    public async Task Unknown_order_is_404()
    {
        var res = await (await AdminClient(AdminRoles.Organizer, Ev.Id)).GetAsync($"/api/v1/admin/orders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task List_shows_orders_newest_first_with_the_common_page_shape()
    {
        var a = await CreateOrderOk();
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var b = await CreateOrderOk(Request(Runner(), Runner(firstName: "สมหญิง")));
        var page = await List(await AdminClient(AdminRoles.Organizer, Ev.Id));

        Assert.Equal((1, 20, 2), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt32()));
        Assert.Equal([b.OrderNo, a.OrderNo], OrderNos(page));
        var first = page.GetProperty("items")[0];
        Assert.Equal(2, first.GetProperty("runnerCount").GetInt32());
        Assert.Equal("Pending", first.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("latestPaymentStatus").ValueKind);
    }

    [Fact]
    public async Task Pagination_splits_results()
    {
        for (var i = 0; i < 3; i++)
        {
            await CreateOrderOk();
            Factory.Time.Advance(TimeSpan.FromMinutes(1));
        }
        var client = await AdminClient(AdminRoles.Organizer, Ev.Id);
        var p1 = await List(client, "?page=1&pageSize=2");
        var p2 = await List(client, "?page=2&pageSize=2");
        Assert.Equal((2, 1, 3), (OrderNos(p1).Length, OrderNos(p2).Length, p2.GetProperty("total").GetInt32()));
        Assert.Empty(OrderNos(p1).Intersect(OrderNos(p2)));
    }

    [Fact]
    public async Task Search_by_order_no_contact_name_phone_runner_name_and_bib()
    {
        var plain = await CreateOrderOk();
        var other = await CreateOrderOk(new CreateOrderRequest(
            new ContactInput("วิภา รักวิ่ง", "089-999-0000", "w@example.com"),
            [Runner(firstName: "มานะ")], Pickup.Id, null, null, true));
        var client = await AdminClient(AdminRoles.Organizer, Ev.Id);

        Assert.Equal([other.OrderNo], OrderNos(await List(client, $"?q={other.OrderNo}")));
        Assert.Equal([other.OrderNo], OrderNos(await List(client, "?q=" + Uri.EscapeDataString("วิภา"))));
        Assert.Equal([other.OrderNo], OrderNos(await List(client, "?q=089-999")));
        Assert.Equal([other.OrderNo], OrderNos(await List(client, "?q=" + Uri.EscapeDataString("มานะ"))));
        Assert.Equal(2, OrderNos(await List(client, "?q=" + Uri.EscapeDataString("ใจดี"))).Length);   // นามสกุลนักวิ่งทั้งสองออเดอร์
        Assert.Empty(OrderNos(await List(client, "?q=%25")));   // % ไม่ใช่ wildcard
        Assert.Contains(plain.OrderNo, OrderNos(await List(client)));
    }

    [Fact]
    public async Task Status_filter_treats_overdue_pending_as_expired()
    {
        var overdue = await CreateOrderOk();
        Factory.Time.Advance(TimeSpan.FromMinutes(31));   // ล่วงเวลา 30 นาที แต่ job ยังไม่ปิด
        var fresh = await CreateOrderOk(Request(Runner(firstName: "ใหม่")));
        var client = await AdminClient(AdminRoles.Organizer, Ev.Id);

        Assert.Equal([fresh.OrderNo], OrderNos(await List(client, "?status=Pending")));
        var expired = await List(client, "?status=Expired");
        Assert.Equal([overdue.OrderNo], OrderNos(expired));
        Assert.Equal("Expired", expired.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Empty(OrderNos(await List(client, "?status=Paid")));
    }

    [Fact]
    public async Task List_shows_the_latest_payment_status()
    {
        var created = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
        {
            db.Payments.AddRange(
                new Payment { OrderId = created.OrderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.Rejected, RejectReason = "WRONG_RECEIVER", ExpectedAmount = created.PayableAmount, CreatedAt = DateTime.UtcNow },
                new Payment { OrderId = created.OrderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.NeedsReview, RejectReason = "VERIFY_TIMEOUT", ExpectedAmount = created.PayableAmount, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var item = (await List(await AdminClient(AdminRoles.Finance, Ev.Id))).GetProperty("items")[0];
        Assert.Equal(("NeedsReview", "VERIFY_TIMEOUT"), (item.GetProperty("latestPaymentStatus").GetString(), item.GetProperty("latestPaymentReason").GetString()));
    }

    [Fact]
    public async Task Detail_for_organizer_has_runner_details_and_all_payments()
    {
        var created = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
        {
            db.Payments.Add(new Payment { OrderId = created.OrderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.Rejected, RejectReason = "WRONG_RECEIVER", ExpectedAmount = created.PayableAmount, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var res = await (await AdminClient(AdminRoles.Organizer, Ev.Id)).GetAsync($"/api/v1/admin/orders/{created.OrderId}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(created.OrderNo, body.GetProperty("orderNo").GetString());
        Assert.Equal("somchai@example.com", body.GetProperty("email").GetString());
        var runner = body.GetProperty("registrations").EnumerateArray().Single();
        Assert.Equal(("5K", "M"), (runner.GetProperty("distance").GetString(), runner.GetProperty("shirtSize").GetString()));
        Assert.Equal("แพ้ยาเพนิซิลลิน", runner.GetProperty("medicalNote").GetString());
        Assert.Equal("สมหญิง ใจดี", runner.GetProperty("emergencyContactName").GetString());
        Assert.Equal("WRONG_RECEIVER", body.GetProperty("payments").EnumerateArray().Single().GetProperty("rejectReason").GetString());
    }

    [Fact]
    public async Task Finance_does_not_see_sensitive_fields()
    {
        var created = await CreateOrderOk();
        var res = await (await AdminClient(AdminRoles.Finance, Ev.Id)).GetAsync($"/api/v1/admin/orders/{created.OrderId}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("สมชาย ใจดี", body.GetProperty("contactName").GetString());
        Assert.Equal(created.PayableAmount, body.GetProperty("payableAmount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("email").ValueKind);
        var runner = body.GetProperty("registrations").EnumerateArray().Single();
        Assert.Equal("สมชาย", runner.GetProperty("firstName").GetString());
        foreach (var field in new[] { "medicalNote", "emergencyContactName", "emergencyContactPhone", "birthDate", "idCardOrPassport", "email", "phone", "gender" })
            Assert.Equal(JsonValueKind.Null, runner.GetProperty(field).ValueKind);
        Assert.DoesNotContain("แพ้ยา", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Shipping_address_is_visible_to_organizer_but_not_finance()
    {
        var created = await CreateOrderOk(Request() with
        {
            ShippingOptionId = Post.Id,
            ShippingAddress = new AddressInput("สมชาย ใจดี", "0812345678", "99/1 ถนนหน้าพระลาน", 100101, "10200"),
        });
        var url = $"/api/v1/admin/orders/{created.OrderId}";

        var org = (await (await AdminClient(AdminRoles.Organizer, Ev.Id)).GetFromJsonAsync<JsonElement>(url)).GetProperty("shipping");
        Assert.Contains("99/1 ถนนหน้าพระลาน", org.GetProperty("address").GetString());
        Assert.Equal("0812345678", org.GetProperty("shipPhone").GetString());

        var fin = (await (await AdminClient(AdminRoles.Finance, Ev.Id)).GetFromJsonAsync<JsonElement>(url)).GetProperty("shipping");
        Assert.Equal("Post", fin.GetProperty("type").GetString());
        foreach (var field in new[] { "address", "shipPhone", "shipName" })
            Assert.Equal(JsonValueKind.Null, fin.GetProperty(field).ValueKind);
    }
}
