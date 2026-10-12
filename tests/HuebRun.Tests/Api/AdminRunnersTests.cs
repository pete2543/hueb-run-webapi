using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminRunnersTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private string Base => $"/api/v1/admin/events/{Ev.Id}";
    private int _seed;

    /// สร้างออเดอร์แล้วจ่ายผ่านสลิปจริง (ได้ Paid + เลขบิบ)
    private async Task<CreatedOrder> PaidOrder(CreateOrderRequest? request = null)
    {
        var created = await CreateOrderOk(request);
        Factory.Verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));
        var res = await Client.PostAsync($"/api/v1/orders/{created.OrderId}/payment/slip",
            new MultipartFormDataContent { { new ByteArrayContent(Slips.Png(++_seed)), "file", "slip.png" } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return created;
    }

    private static async Task<string> Code(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    // ---------- รายชื่อนักวิ่ง ----------

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task List_role_matrix(string role, HttpStatusCode expected) =>
        Assert.Equal(expected, (await (await AdminClient(role, Ev.Id)).GetAsync(Base + "/registrations")).StatusCode);

    [Fact]
    public async Task List_shows_only_paid_runners_and_filters_by_distance_and_search()
    {
        await PaidOrder(Request(Runner(FiveK, M, "สมชาย"), Runner(TenK, L, "วิภา")));
        await CreateOrderOk(Request(Runner(FiveK, M, "ยังไม่จ่าย")));
        var client = await AdminClient(AdminRoles.Organizer, Ev.Id);

        var all = await client.GetFromJsonAsync<JsonElement>(Base + "/registrations");
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        Assert.DoesNotContain("ยังไม่จ่าย", all.ToString());

        var ten = await client.GetFromJsonAsync<JsonElement>($"{Base}/registrations?distance={TenK.Id}");
        Assert.Equal("วิภา", ten.GetProperty("items").EnumerateArray().Single().GetProperty("firstName").GetString());

        var byBib = await client.GetFromJsonAsync<JsonElement>($"{Base}/registrations?q=B0001");
        Assert.Equal("สมชาย", byBib.GetProperty("items").EnumerateArray().Single().GetProperty("firstName").GetString());
        var byName = await client.GetFromJsonAsync<JsonElement>($"{Base}/registrations?q=" + Uri.EscapeDataString("วิภา"));
        Assert.Equal(1, byName.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Finance_does_not_see_sensitive_runner_fields_but_organizer_does()
    {
        await PaidOrder();
        var org = await (await AdminClient(AdminRoles.Organizer, Ev.Id)).GetFromJsonAsync<JsonElement>(Base + "/registrations");
        Assert.Equal("แพ้ยาเพนิซิลลิน", org.GetProperty("items")[0].GetProperty("medicalNote").GetString());

        var res = await (await AdminClient(AdminRoles.Finance, Ev.Id)).GetAsync(Base + "/registrations");
        var item = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal("B0001", item.GetProperty("bibNumber").GetString());
        foreach (var f in new[] { "medicalNote", "emergencyContactName", "emergencyContactPhone", "birthDate", "gender", "phone", "email" })
            Assert.Equal(JsonValueKind.Null, item.GetProperty(f).ValueKind);
        Assert.DoesNotContain("แพ้ยา", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task List_is_scoped_to_the_event_access()
    {
        await PaidOrder();
        Assert.Equal(HttpStatusCode.Forbidden, (await (await AdminClient(AdminRoles.Organizer)).GetAsync(Base + "/registrations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(Base + "/registrations")).StatusCode);
    }

    // ---------- export ----------

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task Export_role_matrix(string role, HttpStatusCode expected) =>
        Assert.Equal(expected, (await (await AdminClient(role, Ev.Id)).GetAsync(Base + "/orders/export")).StatusCode);

    [Fact]
    public async Task Export_is_an_xlsx_with_paid_runners_only_text_cells_and_is_audited()
    {
        await PaidOrder(Request(Runner(FiveK, M, "=1+1"), Runner(TenK, L, "วิภา")));
        await CreateOrderOk(Request(Runner(FiveK, M, "ยังไม่จ่าย")));

        var res = await (await AdminClient(AdminRoles.Organizer, Ev.Id)).GetAsync(Base + "/orders/export");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", res.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".xlsx", res.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        using var wb = new XLWorkbook(await res.Content.ReadAsStreamAsync());
        var sheet = wb.Worksheet(1);
        Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());   // หัวตาราง + 2 คนที่จ่ายแล้ว
        var names = new[] { sheet.Cell(2, 8).GetString(), sheet.Cell(3, 8).GetString() };
        Assert.Contains("=1+1", names);
        Assert.Contains("วิภา", names);
        Assert.False(sheet.Cell(2, 8).HasFormula || sheet.Cell(3, 8).HasFormula);
        Assert.Equal("0812345678", sheet.Cell(2, 16).GetString());   // เบอร์ฉุกเฉินเป็นข้อความ ไม่ถูกตัด 0 นำหน้า

        await using var db = Pg.CreateDbContext();
        var log = await db.AuditLogs.SingleAsync(a => a.Action == "export.runners" && a.EntityId == Ev.Id.ToString());
        Assert.Contains("2", log.NewValues);
    }

    // ---------- เลขพัสดุ ----------

    private static AddressInput Address() => new("สมชาย ใจดี", "0812345678", "99/1 ถนนหน้าพระลาน", 100101, "10200");

    [Fact]
    public async Task Tracking_can_be_set_changed_and_cleared_on_a_paid_post_order_and_is_audited()
    {
        var created = await PaidOrder(Request() with { ShippingOptionId = Post.Id, ShippingAddress = Address() });
        var client = await AdminClient(AdminRoles.Organizer, Ev.Id);
        Task<HttpResponseMessage> Patch(string? no) => client.PatchAsJsonAsync($"/api/v1/admin/orders/{created.OrderId}/tracking", new { trackingNo = no });

        var set = await Patch("  EX123456789TH ");
        Assert.Equal("EX123456789TH", (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("trackingNo").GetString());
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/orders/{created.OrderId}");
        Assert.Equal("EX123456789TH", detail.GetProperty("shipping").GetProperty("trackingNo").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Patch("")).StatusCode);   // ล้าง
        await using var db = Pg.CreateDbContext();
        Assert.Null((await db.Orders.AsNoTracking().SingleAsync(o => o.Id == created.OrderId)).TrackingNo);
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.Action == "order.tracking" && a.EntityId == created.OrderId.ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new string('x', 51))).StatusCode);
    }

    [Fact]
    public async Task Tracking_is_rejected_for_pickup_or_unpaid_orders()
    {
        var pickup = await PaidOrder();
        var unpaidPost = await CreateOrderOk(Request() with { ShippingOptionId = Post.Id, ShippingAddress = Address() });
        var client = await AdminClient(AdminRoles.Organizer, Ev.Id);

        foreach (var id in new[] { pickup.OrderId, unpaidPost.OrderId })
        {
            var res = await client.PatchAsJsonAsync($"/api/v1/admin/orders/{id}/tracking", new { trackingNo = "X1" });
            Assert.Equal((HttpStatusCode.Conflict, "TRACKING_NOT_ALLOWED"), (res.StatusCode, await Code(res)));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/v1/admin/orders/{Guid.NewGuid()}/tracking", new { trackingNo = "X" })).StatusCode);
    }

    [Theory]
    [InlineData(AdminRoles.Finance)]
    [InlineData(AdminRoles.Staff)]
    public async Task Tracking_is_forbidden_for_finance_and_staff(string role)
    {
        var created = await PaidOrder(Request() with { ShippingOptionId = Post.Id, ShippingAddress = Address() });
        var res = await (await AdminClient(role, Ev.Id)).PatchAsJsonAsync($"/api/v1/admin/orders/{created.OrderId}/tracking", new { trackingNo = "X" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var other = await AdminClient(AdminRoles.Organizer);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PatchAsJsonAsync($"/api/v1/admin/orders/{created.OrderId}/tracking", new { trackingNo = "X" })).StatusCode);
    }
}
