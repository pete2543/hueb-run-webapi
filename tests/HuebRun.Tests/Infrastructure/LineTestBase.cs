using System.Net;
using HuebRun.Domain;
using HuebRun.Infrastructure.Line;
using HuebRun.Infrastructure.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HuebRun.Tests.Infrastructure;

/// ฐานของ test ที่เกี่ยวกับ LINE: outbox ใช้ร่วมกันทุก test ในฐานข้อมูลเดียว จึงล้างคิวที่ค้างก่อนเริ่มทุกครั้ง
public abstract class LineTestBase(PostgresFixture pg) : OrderTestBase(pg)
{
    private int _seed;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await using var db = Pg.CreateDbContext();
        await db.Notifications.Where(n => n.Status == NotificationStatus.Pending).ExecuteDeleteAsync();
    }

    protected static string NewLineUser() => "U" + Guid.NewGuid().ToString("N");

    protected async Task<int> AddLineAccount(string userId, bool following = true)
    {
        await using var db = Pg.CreateDbContext();
        var account = new LineAccount { LineUserId = userId, IsFollowing = following, CreatedAt = DateTime.UtcNow };
        db.LineAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    protected async Task LinkOrder(Guid orderId, int accountId)
    {
        await using var db = Pg.CreateDbContext();
        await db.Orders.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.LineAccountId, accountId));
    }

    /// ออเดอร์ที่ผูก LINE แล้ว (คืน userId ของ LINE ด้วย)
    protected async Task<(CreatedOrder Order, string LineUserId)> LinkedOrder(CreateOrderRequest? request = null, bool following = true)
    {
        var created = await CreateOrderOk(request);
        var userId = NewLineUser();
        await LinkOrder(created.OrderId, await AddLineAccount(userId, following));
        return (created, userId);
    }

    protected async Task PayOrder(CreatedOrder order)
    {
        Factory.Verifier.Enqueue(FakeSlipVerifier.Ok(order.PayableAmount));
        var res = await Client.PostAsync($"/api/v1/orders/{order.OrderId}/payment/slip",
            new MultipartFormDataContent { { new ByteArrayContent(Slips.Png(7000 + ++_seed)), "file", "slip.png" } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    protected async Task<int> Dispatch()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchDueAsync();
    }

    protected IEnumerable<string> SentTo(string userId) => Factory.Line.Sent.Where(s => s.UserId == userId).Select(s => s.Text);

    protected async Task<List<Notification>> Outbox(string userId)
    {
        await using var db = Pg.CreateDbContext();
        return await db.Notifications.AsNoTracking().Where(n => n.Recipient == userId).OrderBy(n => n.Id).ToListAsync();
    }
}
