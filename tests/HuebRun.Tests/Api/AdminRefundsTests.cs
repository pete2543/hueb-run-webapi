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
public sealed class AdminRefundsTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private int _seed;
    private Task<HttpClient> Finance() => AdminClient(AdminRoles.Finance, Ev.Id);

    private async Task<CreatedOrder> PaidOrder(CreateOrderRequest? request = null)
    {
        var created = await CreateOrderOk(request);
        Factory.Verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));
        var res = await Client.PostAsync($"/api/v1/orders/{created.OrderId}/payment/slip",
            new MultipartFormDataContent { { new ByteArrayContent(Slips.Png(5000 + ++_seed)), "file", "slip.png" } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return created;
    }

    private async Task<int> ReviewPayment(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        var p = new Payment
        {
            OrderId = orderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.NeedsReview, RejectReason = PaymentRejectReason.AlreadyPaid,
            ExpectedAmount = order.PayableAmount, SlipHash = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<string> Code(HttpResponseMessage res) => (await Body(res)).GetProperty("code").GetString()!;
    private static Task<HttpResponseMessage> Create(HttpClient c, Guid orderId, object? body = null) =>
        c.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/refunds", body ?? new { reason = "ลูกค้าขอยกเลิก" });
    private static Task<HttpResponseMessage> Act(HttpClient c, int id, string action, object? body = null) =>
        c.PostAsJsonAsync($"/api/v1/admin/refunds/{id}/{action}", body ?? new { });
    private static async Task<int> RefundId(HttpResponseMessage res) => (await Body(res)).GetProperty("refundId").GetInt32();

    private async Task<(Order Order, EventDistance Five, EventShirtSize Shirt)> State(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        return (await db.Orders.AsNoTracking().Include(o => o.Registrations).SingleAsync(o => o.Id == orderId),
            await db.EventDistances.AsNoTracking().SingleAsync(d => d.Id == FiveK.Id),
            await db.EventShirtSizes.AsNoTracking().SingleAsync(s => s.Id == M.Id));
    }

    // ---------- สิทธิ์ ----------

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.Created)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.Created)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task Create_role_matrix(string role, HttpStatusCode expected)
    {
        var order = await PaidOrder();
        Assert.Equal(expected, (await Create(await AdminClient(role, Ev.Id), order.OrderId)).StatusCode);
    }

    [Theory]
    [InlineData(AdminRoles.Organizer)]
    [InlineData(AdminRoles.Staff)]
    public async Task Other_roles_cannot_list_or_act(string role)
    {
        var order = await PaidOrder();
        var id = await RefundId(await Create(await Finance(), order.OrderId));
        var c = await AdminClient(role, Ev.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/v1/admin/events/{Ev.Id}/refunds")).StatusCode);
        foreach (var a in new[] { "approve", "reject", "complete" })
            Assert.Equal(HttpStatusCode.Forbidden, (await Act(c, id, a, new { note = "x", externalRef = "x" })).StatusCode);
    }

    [Fact]
    public async Task Finance_without_access_and_anonymous_are_blocked()
    {
        var order = await PaidOrder();
        var id = await RefundId(await Create(await Finance(), order.OrderId));
        var other = await AdminClient(AdminRoles.Finance);
        Assert.Equal(HttpStatusCode.Forbidden, (await Create(other, order.OrderId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/v1/admin/events/{Ev.Id}/refunds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(other, id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Create(Client, order.OrderId)).StatusCode);
        Assert.Equal(RefundStatus.Requested, (await Pg.CreateDbContext().Refunds.AsNoTracking().SingleAsync(r => r.Id == id)).Status);
    }

    // ---------- สร้างคำขอ ----------

    [Fact]
    public async Task Create_for_a_paid_order_defaults_to_the_paid_amount_and_is_audited()
    {
        var order = await PaidOrder();
        var res = await Create(await Finance(), order.OrderId);
        var body = await Body(res);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(("Order", "Requested", order.PayableAmount, order.OrderNo),
            (body.GetProperty("kind").GetString(), body.GetProperty("status").GetString(), body.GetProperty("amount").GetDecimal(), body.GetProperty("orderNo").GetString()));
        Assert.Equal("Finance", body.GetProperty("requestedBy").GetString());
        await using var db = Pg.CreateDbContext();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "refund.create" && a.EntityId == body.GetProperty("refundId").GetInt32().ToString()));
    }

    [Fact]
    public async Task Create_validates_reason_amount_and_state()
    {
        var paid = await PaidOrder();
        var unpaid = await CreateOrderOk();
        var f = await Finance();

        Assert.Equal(HttpStatusCode.BadRequest, (await Create(f, paid.OrderId, new { reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(f, paid.OrderId, new { reason = "x", amount = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(f, paid.OrderId, new { reason = "x", amount = paid.PayableAmount + 1 })).StatusCode);
        var notPaid = await Create(f, unpaid.OrderId);
        Assert.Equal((HttpStatusCode.Conflict, "REFUND_NOT_ALLOWED"), (notPaid.StatusCode, await Code(notPaid)));
        Assert.Equal(HttpStatusCode.NotFound, (await Create(f, Guid.NewGuid())).StatusCode);
        var partial = await Create(f, paid.OrderId, new { reason = "หักค่าธรรมเนียม", amount = 1m });
        Assert.Equal(1m, (await Body(partial)).GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task A_payment_can_have_only_one_active_refund_but_a_rejected_one_can_be_retried()
    {
        var order = await PaidOrder();
        var f = await Finance();
        var first = await RefundId(await Create(f, order.OrderId));
        var dup = await Create(f, order.OrderId);
        Assert.Equal((HttpStatusCode.Conflict, "REFUND_EXISTS"), (dup.StatusCode, await Code(dup)));

        Assert.Equal(HttpStatusCode.OK, (await Act(f, first, "reject", new { note = "ไม่เข้าเงื่อนไข" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Create(f, order.OrderId)).StatusCode);
    }

    // ---------- approve / complete (คืนทั้งออเดอร์) ----------

    [Fact]
    public async Task Approve_cancels_runners_releases_quota_shirts_and_promo_but_order_stays_paid_until_complete()
    {
        var promo = await AddPromoAsync();
        var order = await PaidOrder(Request(Runner(FiveK, M, "เอ"), Runner(FiveK, M, "บี")) with { PromoCode = "early10" });
        var before = await State(order.OrderId);
        Assert.Equal((2, 2), (before.Five.ReservedCount, before.Shirt.ReservedCount));
        var f = await Finance();
        var id = await RefundId(await Create(f, order.OrderId));

        var res = await Act(f, id, "approve");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Approved", (await Body(res)).GetProperty("status").GetString());
        var after = await State(order.OrderId);
        Assert.Equal(OrderStatus.Paid, after.Order.Status);
        Assert.All(after.Order.Registrations, r => Assert.True(r.IsCancelled));
        Assert.Equal((0, 0), (after.Five.ReservedCount, after.Shirt.ReservedCount));
        await using var db = Pg.CreateDbContext();
        Assert.Equal(0, (await db.PromoCodes.AsNoTracking().SingleAsync(p => p.Id == promo.Id)).UsedCount);
        Assert.All(after.Order.Registrations, r => Assert.NotNull(r.BibNumber));   // เลขบิบเดิมเก็บไว้เป็นประวัติ ไม่ถูกนำกลับมาใช้
    }

    [Fact]
    public async Task Cancelled_runner_cannot_check_in_after_approve()
    {
        var order = await PaidOrder();
        await using var db = Pg.CreateDbContext();
        var reg = await db.Registrations.AsNoTracking().SingleAsync(r => r.OrderId == order.OrderId);
        var f = await Finance();
        await Act(f, await RefundId(await Create(f, order.OrderId)), "approve");

        var res = await (await AdminClient(AdminRoles.Staff, Ev.Id)).PostAsJsonAsync($"/api/v1/admin/checkin/{reg.Id}/confirm", new { });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Complete_requires_a_reference_and_marks_the_order_refunded()
    {
        var order = await PaidOrder();
        var f = await Finance();
        var id = await RefundId(await Create(f, order.OrderId));
        await Act(f, id, "approve");

        Assert.Equal(HttpStatusCode.BadRequest, (await Act(f, id, "complete", new { externalRef = " " })).StatusCode);
        var res = await Act(f, id, "complete", new { externalRef = "TRF-20261115-001", note = "โอนคืนทาง PromptPay" });
        var body = await Body(res);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(("Completed", "TRF-20261115-001"), (body.GetProperty("status").GetString(), body.GetProperty("externalRef").GetString()));
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("processedAt").ValueKind);
        Assert.Equal(OrderStatus.Refunded, (await State(order.OrderId)).Order.Status);
        Assert.Equal("Refunded", (await (await AdminClient(AdminRoles.Organizer, Ev.Id)).GetFromJsonAsync<JsonElement>($"/api/v1/admin/orders/{order.OrderId}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task State_machine_blocks_skipping_or_repeating_steps_and_releases_only_once()
    {
        var order = await PaidOrder();
        var f = await Finance();
        var id = await RefundId(await Create(f, order.OrderId));

        var early = await Act(f, id, "complete", new { externalRef = "X" });
        Assert.Equal((HttpStatusCode.Conflict, "REFUND_WRONG_STATE"), (early.StatusCode, await Code(early)));
        Assert.Equal(HttpStatusCode.OK, (await Act(f, id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, id, "reject", new { note = "x" })).StatusCode);
        Assert.Equal(0, (await State(order.OrderId)).Five.ReservedCount);   // ไม่ลบซ้ำจนติดลบ
        Assert.Equal(HttpStatusCode.OK, (await Act(f, id, "complete", new { externalRef = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, id, "complete", new { externalRef = "Y" })).StatusCode);
    }

    [Fact]
    public async Task Two_approvals_at_once_release_inventory_once()
    {
        var order = await PaidOrder();
        var id = await RefundId(await Create(await Finance(), order.OrderId));
        var results = await Task.WhenAll(Act(await Finance(), id, "approve"), Act(await Finance(), id, "approve"));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
        Assert.Equal(0, (await State(order.OrderId)).Five.ReservedCount);
    }

    [Fact]
    public async Task Reject_requires_a_note_and_leaves_the_order_untouched()
    {
        var order = await PaidOrder();
        var f = await Finance();
        var id = await RefundId(await Create(f, order.OrderId));

        Assert.Equal(HttpStatusCode.BadRequest, (await Act(f, id, "reject", new { note = "" })).StatusCode);
        var res = await Act(f, id, "reject", new { note = "เลยกำหนดขอคืนเงิน" });
        var body = await Body(res);

        Assert.Equal(("Rejected", "เลยกำหนดขอคืนเงิน"), (body.GetProperty("status").GetString(), body.GetProperty("note").GetString()));
        var s = await State(order.OrderId);
        Assert.Equal((OrderStatus.Paid, 1), (s.Order.Status, s.Five.ReservedCount));
        Assert.All(s.Order.Registrations, r => Assert.False(r.IsCancelled));
    }

    // ---------- คืนสลิปที่ไม่ใช่ของออเดอร์ (ซ้ำซ้อน / โควตาเต็ม) ----------

    [Fact]
    public async Task Refunding_a_duplicate_review_payment_clears_the_review_queue_without_touching_the_paid_order()
    {
        var order = await PaidOrder();
        var dup = await ReviewPayment(order.OrderId);
        var f = await Finance();

        var created = await Create(f, order.OrderId, new { paymentId = dup, reason = "โอนซ้ำ" });
        var createdBody = await Body(created);
        Assert.Equal("Payment", createdBody.GetProperty("kind").GetString());
        var id = createdBody.GetProperty("refundId").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await Act(f, id, "approve")).StatusCode);

        var s = await State(order.OrderId);
        Assert.Equal(OrderStatus.Paid, s.Order.Status);
        Assert.All(s.Order.Registrations, r => Assert.False(r.IsCancelled));
        Assert.Equal(1, s.Five.ReservedCount);
        await using var db = Pg.CreateDbContext();
        Assert.Equal(PaymentStatus.Rejected, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == dup)).Status);

        Assert.Equal(HttpStatusCode.OK, (await Act(f, id, "complete", new { externalRef = "TRF-DUP" })).StatusCode);
        Assert.Equal(OrderStatus.Paid, (await State(order.OrderId)).Order.Status);   // ออเดอร์ที่จ่ายถูกต้องไม่กลายเป็น Refunded
    }

    [Fact]
    public async Task Refunding_a_review_payment_of_an_expired_order_works()
    {
        var created = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
            await db.Orders.Where(o => o.Id == created.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired));
        var payment = await ReviewPayment(created.OrderId);
        var f = await Finance();

        var id = await RefundId(await Create(f, created.OrderId, new { paymentId = payment, reason = "โควตาเต็ม" }));
        Assert.Equal(HttpStatusCode.OK, (await Act(f, id, "approve")).StatusCode);
        Assert.Equal(OrderStatus.Expired, (await State(created.OrderId)).Order.Status);
    }

    [Fact]
    public async Task Payment_of_another_order_cannot_be_used()
    {
        var a = await PaidOrder();
        var b = await PaidOrder(Request(Runner(firstName: "คนอื่น")));
        await using var db = Pg.CreateDbContext();
        var otherPayment = await db.Payments.AsNoTracking().Where(p => p.OrderId == b.OrderId).Select(p => p.Id).SingleAsync();
        var res = await Create(await Finance(), a.OrderId, new { paymentId = otherPayment, reason = "x" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    // ---------- list ----------

    [Fact]
    public async Task List_filters_by_status_with_the_common_page_shape()
    {
        var o1 = await PaidOrder();
        var o2 = await PaidOrder(Request(Runner(firstName: "สอง")));
        var f = await Finance();
        var r1 = await RefundId(await Create(f, o1.OrderId));
        await Create(f, o2.OrderId);
        await Act(f, r1, "reject", new { note = "x" });

        var all = await f.GetFromJsonAsync<JsonElement>($"/api/v1/admin/events/{Ev.Id}/refunds");
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        var requested = await f.GetFromJsonAsync<JsonElement>($"/api/v1/admin/events/{Ev.Id}/refunds?status=Requested");
        Assert.Equal(o2.OrderNo, requested.GetProperty("items").EnumerateArray().Single().GetProperty("orderNo").GetString());
    }
}
