using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace HuebRun.Infrastructure.Orders;

public sealed record MarkPaidResult(bool AlreadyPaid);

/// เปลี่ยนออเดอร์เป็น Paid และออกเลขบิบ — ขั้นชำระเงินเรียกใช้ (ร่วม transaction ที่เปิดอยู่ได้)
public sealed class OrderPaymentService(AppDbContext db)
{
    public async Task<MarkPaidResult> MarkPaidAsync(Guid orderId, DateTime paidAtUtc, CancellationToken ct = default)
    {
        // ใช้ transaction ของคนเรียกถ้ามี (เช่น อัปเดต Payment พร้อมกัน) ไม่งั้นเปิดเอง
        var outer = db.Database.CurrentTransaction;
        await using var owned = outer is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var tx = outer ?? owned!;

        // update แบบมีเงื่อนไข: แข่งกับ job หมดเวลาได้ฝั่งเดียว
        var updated = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Paid).SetProperty(o => o.PaidAt, paidAtUtc), ct);
        if (updated == 0)
        {
            var status = await db.Orders.Where(o => o.Id == orderId).Select(o => (OrderStatus?)o.Status).FirstOrDefaultAsync(ct);
            if (status == OrderStatus.Paid) return new MarkPaidResult(AlreadyPaid: true);
            throw new InvalidOperationException(
                $"Order {orderId} cannot be marked paid (status: {status?.ToString() ?? "not found"})");
        }

        var registrations = await db.Registrations
            .Include(r => r.EventDistance)
            .Where(r => r.OrderId == orderId && !r.IsCancelled)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);
        foreach (var group in registrations.GroupBy(r => r.EventDistanceId).OrderBy(g => g.Key))
        {
            var count = group.Count();
            var last = await ReserveBibNumbersAsync(group.Key, count, tx, ct);
            var next = last - count + 1;
            foreach (var registration in group)
                registration.BibNumber = $"{registration.EventDistance.BibPrefix}{next++:D4}";
        }

        await db.SaveChangesAsync(ct);
        if (owned is not null) await owned.CommitAsync(ct);
        return new MarkPaidResult(AlreadyPaid: false);
    }

    // เพิ่มตัวนับแบบ atomic แล้วคืนเลขสุดท้ายที่จองได้
    private async Task<int> ReserveBibNumbersAsync(int distanceId, int count, IDbContextTransaction tx, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText =
            "UPDATE \"EventDistances\" SET \"LastBibNumber\" = \"LastBibNumber\" + @count WHERE \"Id\" = @id RETURNING \"LastBibNumber\"";
        command.Parameters.Add(new NpgsqlParameter("count", count));
        command.Parameters.Add(new NpgsqlParameter("id", distanceId));
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }
}
