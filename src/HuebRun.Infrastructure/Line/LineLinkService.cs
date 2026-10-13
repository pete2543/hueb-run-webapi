using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Line;

/// ผูก LINE ของนักวิ่งกับออเดอร์ (นักวิ่งล็อกอิน LIFF แล้วส่ง ID token มา) เพื่อให้ส่งแจ้งเตือนเข้า LINE ได้
public sealed class LineLinkService(AppDbContext db, ILineIdentityVerifier verifier, TimeProvider time)
{
    public async Task LinkOrderAsync(Guid orderId, string? idToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idToken)) throw LineErrors.InvalidToken();
        var status = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => (OrderStatus?)o.Status).SingleOrDefaultAsync(ct)
                     ?? throw Orders.OrderErrors.OrderNotFound(orderId);
        if (status is OrderStatus.Cancelled or OrderStatus.Refunded or OrderStatus.Expired) throw LineErrors.OrderClosed();
        var identity = await verifier.VerifyIdTokenAsync(idToken.Trim(), ct) ?? throw LineErrors.InvalidToken();

        var now = time.GetUtcNow().UtcDateTime;
        // IsFollowing ไม่แตะ: รู้จากเหตุการณ์ follow/unfollow ของ webhook เท่านั้น
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "LineAccounts" ("LineUserId", "DisplayName", "PictureUrl", "IsFollowing", "CreatedAt")
            VALUES ({identity.UserId}, {identity.DisplayName}, {identity.PictureUrl}, false, {now})
            ON CONFLICT ("LineUserId") DO UPDATE SET "DisplayName" = COALESCE({identity.DisplayName}, "LineAccounts"."DisplayName"),
                                                     "PictureUrl" = COALESCE({identity.PictureUrl}, "LineAccounts"."PictureUrl")
            """, ct);
        var accountId = await db.LineAccounts.AsNoTracking().Where(a => a.LineUserId == identity.UserId).Select(a => a.Id).SingleAsync(ct);

        var linked = await db.Orders.Where(o => o.Id == orderId && (o.LineAccountId == null || o.LineAccountId == accountId))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LineAccountId, accountId), ct);
        if (linked == 0) throw LineErrors.AlreadyLinked();
    }
}
