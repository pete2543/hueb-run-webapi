using HuebRun.Domain;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class PaymentSchemaTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private static Payment Verifying(Guid orderId, string hash) => new()
    {
        OrderId = orderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.Verifying,
        ExpectedAmount = 400.01m, SlipHash = hash, CreatedAt = TestData.Utc(2026, 11, 15),
        NextCheckAt = TestData.Utc(2026, 11, 15), SlipObjectKey = "slips/x.png",
    };

    [Fact]
    public async Task Order_can_have_only_one_verifying_payment()
    {
        var created = await CreateOrderOk();
        await using var db = Pg.CreateDbContext();
        db.Payments.Add(Verifying(created.OrderId, Guid.NewGuid().ToString()));
        await db.SaveChangesAsync();

        db.Payments.Add(Verifying(created.OrderId, Guid.NewGuid().ToString()));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("IX_Payments_OrderId_Verifying", ((PostgresException)ex.InnerException!).ConstraintName);
    }

    [Fact]
    public async Task Rejected_payments_do_not_block_a_new_verifying_one()
    {
        var created = await CreateOrderOk();
        await using var db = Pg.CreateDbContext();
        var first = Verifying(created.OrderId, Guid.NewGuid().ToString());
        first.Status = PaymentStatus.Rejected;
        db.Payments.Add(first);
        db.Payments.Add(Verifying(created.OrderId, Guid.NewGuid().ToString()));
        await db.SaveChangesAsync();
        Assert.Equal(0, (await db.Payments.SingleAsync(p => p.Id == first.Id)).CheckAttempts);
    }
}
