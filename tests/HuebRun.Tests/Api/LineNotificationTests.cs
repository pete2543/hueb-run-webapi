using System.Net;
using System.Net.Http.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Infrastructure.Line;
using HuebRun.Infrastructure.Orders;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class LineNotificationTests(PostgresFixture pg) : LineTestBase(pg)
{
    private static AddressInput Address() => new("สมชาย ใจดี", "0812345678", "99/1 ถนนหน้าพระลาน", 100101, "10200");

    // ---------- เหตุการณ์ → ข้อความใน outbox ----------

    [Fact]
    public async Task Payment_success_queues_an_order_paid_message_with_order_no_and_ticket_link()
    {
        var (order, user) = await LinkedOrder();
        await PayOrder(order);

        var box = await Outbox(user);
        var n = Assert.Single(box);
        Assert.Equal(("order_paid", NotificationStatus.Pending, $"order_paid:{order.OrderId}"), (n.TemplateKey, n.Status, n.DedupeKey));
        Assert.Equal(1, await Dispatch());
        var text = Assert.Single(SentTo(user));
        Assert.Contains(order.OrderNo, text);
        Assert.Contains($"https://hueb.test/orders/{order.OrderId}", text);
        Assert.DoesNotContain("{{", text);   // แทน placeholder ครบ
        Assert.Equal(NotificationStatus.Sent, (await Outbox(user)).Single().Status);
    }

    [Fact]
    public async Task Nothing_is_queued_when_line_is_not_linked_or_the_user_unfollowed()
    {
        var plain = await CreateOrderOk();
        await PayOrder(plain);
        var (unfollowed, user) = await LinkedOrder(Request(Runner(firstName: "เลิกติดตาม")), following: false);
        await PayOrder(unfollowed);

        Assert.Empty(await Outbox(user));
        await using var db = Pg.CreateDbContext();
        Assert.False(await db.Notifications.AnyAsync(n => n.OrderId == plain.OrderId));
    }

    [Fact]
    public async Task Slip_rejection_queues_a_message_with_the_reason()
    {
        var (order, user) = await LinkedOrder();
        Factory.Verifier.Enqueue(new SlipCheckResult.Rejected(PaymentRejectReason.AmountMismatch, "{}"));
        var res = await Client.PostAsync($"/api/v1/orders/{order.OrderId}/payment/slip",
            new MultipartFormDataContent { { new ByteArrayContent(Slips.Png(8801)), "file", "slip.png" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);

        await Dispatch();
        var text = Assert.Single(SentTo(user));
        Assert.Contains("ยอดเงินไม่ตรง", text);
        Assert.Contains(order.OrderNo, text);
    }

    [Fact]
    public async Task Admin_reject_and_tracking_number_queue_messages_once()
    {
        var (order, user) = await LinkedOrder(Request() with { ShippingOptionId = Post.Id, ShippingAddress = Address() });
        await PayOrder(order);
        await Dispatch();
        var org = await AdminClient(AdminRoles.Organizer, Ev.Id);

        for (var i = 0; i < 2; i++)   // ส่งเลขเดิมซ้ำต้องไม่ส่งข้อความซ้ำ
            Assert.Equal(HttpStatusCode.OK, (await org.PatchAsJsonAsync($"/api/v1/admin/orders/{order.OrderId}/tracking", new { trackingNo = "EX1TH" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await org.PatchAsJsonAsync($"/api/v1/admin/orders/{order.OrderId}/tracking", new { trackingNo = "" })).StatusCode);   // ล้างเลข = ไม่แจ้ง
        await Dispatch();

        var shipping = SentTo(user).Where(t => t.Contains("EX1TH")).ToList();
        Assert.Single(shipping);
        Assert.Contains(order.OrderNo, shipping[0]);
    }

    [Fact]
    public async Task Inactive_template_means_no_message()
    {
        var (order, user) = await LinkedOrder();
        await using (var db = Pg.CreateDbContext())
            await db.NotificationTemplates.Where(t => t.Key == "order_paid").ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false));
        try
        {
            await PayOrder(order);
            Assert.Empty(await Outbox(user));
        }
        finally
        {
            await using var db = Pg.CreateDbContext();
            await db.NotificationTemplates.Where(t => t.Key == "order_paid").ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, true));
        }
    }

    // ---------- การส่ง ----------

    private async Task<string> QueueOne()
    {
        var (order, user) = await LinkedOrder();
        await PayOrder(order);
        return user;
    }

    [Fact]
    public async Task Temporary_failures_are_retried_with_backoff_then_succeed()
    {
        var user = await QueueOne();
        Factory.Line.Enqueue(new LineSendResult(LineSendOutcome.Retry, "HTTP 503"));

        Assert.Equal(1, await Dispatch());
        var after1 = (await Outbox(user)).Single();
        Assert.Equal((NotificationStatus.Pending, 1, "HTTP 503"), (after1.Status, after1.Attempts, after1.LastError));
        Assert.Equal(Factory.Time.GetUtcNow().UtcDateTime.AddMinutes(1), after1.ScheduledAt);

        Assert.Equal(0, await Dispatch());   // ยังไม่ถึงเวลา
        Factory.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await Dispatch());
        var done = (await Outbox(user)).Single();
        Assert.Equal((NotificationStatus.Sent, 2, (string?)null), (done.Status, done.Attempts, done.LastError));
    }

    [Fact]
    public async Task After_five_temporary_failures_the_message_fails_for_good()
    {
        var user = await QueueOne();
        Factory.Line.Enqueue(Enumerable.Repeat(new LineSendResult(LineSendOutcome.Retry, "HTTP 500"), 5).ToArray());
        for (var i = 0; i < 5; i++)
        {
            Factory.Time.Advance(TimeSpan.FromHours(4));
            await Dispatch();
        }
        var n = (await Outbox(user)).Single();
        Assert.Equal((NotificationStatus.Failed, 5), (n.Status, n.Attempts));
        Factory.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await Dispatch());
    }

    [Fact]
    public async Task Permanent_failures_are_not_retried()
    {
        var user = await QueueOne();
        Factory.Line.Enqueue(new LineSendResult(LineSendOutcome.Permanent, "HTTP 400: blocked"));
        await Dispatch();
        var n = (await Outbox(user)).Single();
        Assert.Equal((NotificationStatus.Failed, 1, "HTTP 400: blocked"), (n.Status, n.Attempts, n.LastError));
        Factory.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await Dispatch());
    }

    [Fact]
    public async Task Concurrent_dispatchers_send_each_message_once()
    {
        var user = await QueueOne();
        await Task.WhenAll(Dispatch(), Dispatch(), Dispatch());
        Assert.Single(SentTo(user));
    }
}
