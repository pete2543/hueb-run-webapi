using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Infrastructure.Orders;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class AdminPaymentsTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private const string Password = "correct-horse-battery";

    private async Task<HttpClient> AdminClient(string role, params int[] eventIds)
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

    private Task<HttpClient> Finance() => AdminClient(AdminRoles.Finance, Ev.Id);

    /// สร้าง Payment รอตรวจ (มีรูปสลิปใน storage) บนออเดอร์ที่ระบุ
    private async Task<int> NeedsReview(Guid orderId, string reason, PaymentStatus status = PaymentStatus.NeedsReview)
    {
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        var payment = new Payment
        {
            OrderId = orderId, Provider = PaymentProvider.SlipOk, Status = status, ExpectedAmount = order.PayableAmount,
            SlipHash = Guid.NewGuid().ToString("N"), RejectReason = reason, CreatedAt = Factory.Time.GetUtcNow().UtcDateTime,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        payment.SlipObjectKey = $"slips/{Ev.Id}/{orderId}/{payment.Id}.png";
        Factory.Storage.Objects[payment.SlipObjectKey] = Slips.Png(payment.Id);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private async Task ExpireOrder(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        var later = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 3, 0, 0, TimeSpan.Zero));
        Assert.True(await new OrderExpiryService(db, later).ExpireDueOrdersAsync() >= 1);
        Assert.Equal(OrderStatus.Expired, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status);
    }

    private async Task<(Order Order, Payment Payment, EventDistance Distance)> Load(Guid orderId, int paymentId)
    {
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.AsNoTracking().Include(o => o.Registrations).SingleAsync(o => o.Id == orderId);
        return (order, await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId),
            await db.EventDistances.AsNoTracking().SingleAsync(d => d.Id == FiveK.Id));
    }

    private static async Task<string> Code(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    // ---------- สิทธิ์ ----------

    [Theory]
    [InlineData(AdminRoles.SuperAdmin, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Finance, HttpStatusCode.OK)]
    [InlineData(AdminRoles.Organizer, HttpStatusCode.Forbidden)]
    [InlineData(AdminRoles.Staff, HttpStatusCode.Forbidden)]
    public async Task Only_finance_and_super_admin_use_payment_endpoints(string role, HttpStatusCode expected)
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        var client = await AdminClient(role, Ev.Id);

        Assert.Equal(expected, (await client.GetAsync($"/api/v1/admin/events/{Ev.Id}/payments")).StatusCode);
        Assert.Equal(expected, (await client.GetAsync($"/api/v1/admin/payments/{id}/slip")).StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? HttpStatusCode.OK : expected,
            (await client.PostAsJsonAsync($"/api/v1/admin/payments/{id}/reject", new { note = "x" })).StatusCode);
        if (expected == HttpStatusCode.Forbidden)
        {
            Assert.Equal(expected, (await client.PostAsync($"/api/v1/admin/payments/{id}/approve", null)).StatusCode);
            Assert.Equal(expected, (await client.PostAsync($"/api/v1/admin/payments/{id}/recheck", null)).StatusCode);
        }
    }

    [Fact]
    public async Task Anonymous_gets_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/api/v1/admin/events/{Ev.Id}/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsync("/api/v1/admin/payments/1/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Finance_without_access_to_the_event_gets_403_everywhere()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        var client = await AdminClient(AdminRoles.Finance);   // ไม่มี event access เลย

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/admin/events/{Ev.Id}/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/admin/payments/{id}/slip")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/admin/payments/{id}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/admin/payments/{id}/reject", new { note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/admin/payments/{id}/recheck", null)).StatusCode);
        Assert.Equal(PaymentStatus.NeedsReview, (await Load(created.OrderId, id)).Payment.Status);
    }

    [Fact]
    public async Task Super_admin_reaches_any_event()
    {
        var res = await (await AdminClient(AdminRoles.SuperAdmin)).GetAsync($"/api/v1/admin/events/{Ev.Id}/payments");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---------- รายการ / รูปสลิป ----------

    [Fact]
    public async Task List_filters_by_status_and_uses_the_common_page_shape()
    {
        var a = await CreateOrderOk();
        var b = await CreateOrderOk();
        var review = await NeedsReview(a.OrderId, PaymentRejectReason.VerifyTimeout);
        await NeedsReview(b.OrderId, PaymentRejectReason.WrongReceiver, PaymentStatus.Rejected);
        var client = await Finance();

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/events/{Ev.Id}/payments?status=NeedsReview&page=1&pageSize=10");
        Assert.Equal((1, 10, 1), (body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(), body.GetProperty("total").GetInt32()));
        var item = body.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(review, item.GetProperty("paymentId").GetInt32());
        Assert.Equal("VERIFY_TIMEOUT", item.GetProperty("rejectReason").GetString());
        Assert.Equal(a.OrderNo, item.GetProperty("orderNo").GetString());

        var all = await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/events/{Ev.Id}/payments");
        Assert.Equal(2, all.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Slip_link_is_short_lived_and_missing_payment_is_404()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        var client = await Finance();

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/payments/{id}/slip");
        Assert.EndsWith("ttl=300", body.GetProperty("url").GetString());
        Assert.Equal(Factory.Time.GetUtcNow().UtcDateTime.AddMinutes(5), body.GetProperty("expiresAt").GetDateTime());

        Assert.Equal("PAYMENT_NOT_FOUND", await Code(await client.GetAsync("/api/v1/admin/payments/999999/slip")));
    }

    // ---------- approve ----------

    [Fact]
    public async Task Approve_pays_the_order_issues_bib_and_audits()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        var res = await (await Finance()).PostAsync($"/api/v1/admin/payments/{id}/approve", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (order, payment, _) = await Load(created.OrderId, id);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal("B0001", order.Registrations.Single().BibNumber);
        Assert.Equal((PaymentStatus.Verified, (decimal?)created.PayableAmount), (payment.Status, payment.PaidAmount));
        Assert.NotNull(payment.ReviewedBy);
        await using var db = Pg.CreateDbContext();
        Assert.Equal(payment.ReviewedBy, (await db.AuditLogs.SingleAsync(a => a.Action == "payment.approve" && a.EntityId == id.ToString())).AdminUserId.ToString());
    }

    [Fact]
    public async Task Second_approve_gets_409_and_does_not_issue_another_bib()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        var client = await Finance();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/admin/payments/{id}/approve", null)).StatusCode);

        var again = await client.PostAsync($"/api/v1/admin/payments/{id}/approve", null);
        Assert.Equal((HttpStatusCode.Conflict, "PAYMENT_NOT_REVIEWABLE"), (again.StatusCode, await Code(again)));
        Assert.Equal("B0001", (await Load(created.OrderId, id)).Order.Registrations.Single().BibNumber);
    }

    [Fact]
    public async Task Two_admins_approving_at_once_only_one_wins()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        var (c1, c2) = (await Finance(), await Finance());

        var results = await Task.WhenAll(
            c1.PostAsync($"/api/v1/admin/payments/{id}/approve", null),
            c2.PostAsync($"/api/v1/admin/payments/{id}/approve", null));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
        var (order, _, _) = await Load(created.OrderId, id);
        Assert.Equal("B0001", order.Registrations.Single().BibNumber);
    }

    [Fact]
    public async Task Approving_after_the_order_expired_reserves_quota_again()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.OrderExpired);
        await ExpireOrder(created.OrderId);
        Assert.Equal(0, (await Load(created.OrderId, id)).Distance.ReservedCount);

        var res = await (await Finance()).PostAsync($"/api/v1/admin/payments/{id}/approve", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (order, payment, distance) = await Load(created.OrderId, id);
        Assert.Equal((OrderStatus.Paid, PaymentStatus.Verified, 1), (order.Status, payment.Status, distance.ReservedCount));
        Assert.NotNull(order.Registrations.Single().BibNumber);
    }

    [Fact]
    public async Task Approving_an_expired_order_when_quota_is_full_is_blocked_and_changes_nothing()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.OrderExpired);
        await ExpireOrder(created.OrderId);
        await using (var db = Pg.CreateDbContext())   // คนอื่นจองเต็มโควตาไปแล้ว
            await db.EventDistances.Where(d => d.Id == FiveK.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ReservedCount, d => d.Quota));

        var res = await (await Finance()).PostAsync($"/api/v1/admin/payments/{id}/approve", null);

        Assert.Equal((HttpStatusCode.Conflict, "CAPACITY_FULL"), (res.StatusCode, await Code(res)));
        var (order, payment, distance) = await Load(created.OrderId, id);
        Assert.Equal((OrderStatus.Expired, PaymentStatus.NeedsReview), (order.Status, payment.Status));
        Assert.Equal(distance.Quota, distance.ReservedCount);
        Assert.Null(payment.ReviewedBy);
    }

    [Fact]
    public async Task Approve_is_blocked_when_the_order_is_already_paid()
    {
        var created = await CreateOrderOk();
        var first = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        var second = await NeedsReview(created.OrderId, PaymentRejectReason.AlreadyPaid);
        var client = await Finance();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/admin/payments/{first}/approve", null)).StatusCode);

        var res = await client.PostAsync($"/api/v1/admin/payments/{second}/approve", null);

        Assert.Equal((HttpStatusCode.Conflict, "APPROVE_NOT_ALLOWED"), (res.StatusCode, await Code(res)));
        Assert.Equal(PaymentStatus.NeedsReview, (await Load(created.OrderId, second)).Payment.Status);
    }

    [Fact]
    public async Task Approve_is_blocked_for_a_cancelled_order()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        await using (var db = Pg.CreateDbContext())
            await db.Orders.Where(o => o.Id == created.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Cancelled));

        var res = await (await Finance()).PostAsync($"/api/v1/admin/payments/{id}/approve", null);

        Assert.Equal((HttpStatusCode.Conflict, "ORDER_NOT_PAYABLE"), (res.StatusCode, await Code(res)));
        Assert.Equal(PaymentStatus.NeedsReview, (await Load(created.OrderId, id)).Payment.Status);
    }

    // ---------- reject ----------

    [Fact]
    public async Task Reject_needs_a_note()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        var client = await Finance();

        foreach (var note in new[] { "", "   ", null, new string('x', 501) })
        {
            var res = await client.PostAsJsonAsync($"/api/v1/admin/payments/{id}/reject", new { note });
            Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_ERROR"), (res.StatusCode, await Code(res)));
        }
        Assert.Equal(PaymentStatus.NeedsReview, (await Load(created.OrderId, id)).Payment.Status);
    }

    [Fact]
    public async Task Reject_keeps_the_order_open_for_a_new_slip_and_shows_the_note_to_the_runner()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        var res = await (await Finance()).PostAsJsonAsync($"/api/v1/admin/payments/{id}/reject", new { note = "ยอดไม่ตรงกับบัญชี" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var (order, payment, _) = await Load(created.OrderId, id);
        Assert.Equal((OrderStatus.Pending, PaymentStatus.Rejected, PaymentRejectReason.AdminRejected), (order.Status, payment.Status, payment.RejectReason));
        Assert.True(order.ExpiresAt >= Factory.Time.GetUtcNow().UtcDateTime.AddMinutes(10));

        var info = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{created.OrderId}/payment");
        Assert.Equal("ยอดไม่ตรงกับบัญชี", info.GetProperty("payment").GetProperty("note").GetString());

        Factory.Verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));   // อัปโหลดใหม่ได้
        var upload = await Client.PostAsync($"/api/v1/orders/{created.OrderId}/payment/slip",
            new MultipartFormDataContent { { new ByteArrayContent(Slips.Png(77)), "file", "slip.png" } });
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
    }

    [Fact]
    public async Task Reject_after_approve_gets_409()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.DuplicateUnknown);
        var client = await Finance();
        await client.PostAsync($"/api/v1/admin/payments/{id}/approve", null);

        var res = await client.PostAsJsonAsync($"/api/v1/admin/payments/{id}/reject", new { note = "x" });
        Assert.Equal("PAYMENT_NOT_REVIEWABLE", await Code(res));
    }

    // ---------- recheck ----------

    [Fact]
    public async Task Recheck_sends_a_timed_out_payment_back_to_the_job_and_holds_the_order()
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, PaymentRejectReason.VerifyTimeout);
        var res = await (await Finance()).PostAsync($"/api/v1/admin/payments/{id}/recheck", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (order, payment, _) = await Load(created.OrderId, id);
        Assert.Equal((PaymentStatus.Verifying, (string?)null, 0), (payment.Status, payment.RejectReason, payment.CheckAttempts));
        Assert.Equal(Factory.Time.GetUtcNow().UtcDateTime, payment.NextCheckAt);
        Assert.True(order.ExpiresAt >= Factory.Time.GetUtcNow().UtcDateTime.AddMinutes(90));
    }

    [Theory]
    [InlineData(PaymentRejectReason.DuplicateUnknown)]
    [InlineData(PaymentRejectReason.OrderExpired)]
    public async Task Recheck_is_only_for_verify_timeout(string reason)
    {
        var created = await CreateOrderOk();
        var id = await NeedsReview(created.OrderId, reason);
        var res = await (await Finance()).PostAsync($"/api/v1/admin/payments/{id}/recheck", null);

        Assert.Equal((HttpStatusCode.Conflict, "APPROVE_NOT_ALLOWED"), (res.StatusCode, await Code(res)));
        Assert.Equal(PaymentStatus.NeedsReview, (await Load(created.OrderId, id)).Payment.Status);
    }

    [Fact]
    public async Task Recheck_conflicts_when_the_runner_already_has_a_new_slip_verifying()
    {
        var created = await CreateOrderOk();
        var review = await NeedsReview(created.OrderId, PaymentRejectReason.VerifyTimeout);
        await NeedsReview(created.OrderId, null!, PaymentStatus.Verifying);

        var res = await (await Finance()).PostAsync($"/api/v1/admin/payments/{review}/recheck", null);

        Assert.Equal((HttpStatusCode.Conflict, "PAYMENT_IN_PROGRESS"), (res.StatusCode, await Code(res)));
        Assert.Equal(PaymentStatus.NeedsReview, (await Load(created.OrderId, review)).Payment.Status);
    }
}
