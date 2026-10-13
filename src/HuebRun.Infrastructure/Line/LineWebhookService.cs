using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HuebRun.Infrastructure.Line;

/// webhook จาก LINE OA: follow/unfollow → อัปเดต LineAccount (ตรวจ X-Line-Signature = HMAC-SHA256(body, channel secret) แบบ base64)
public sealed class LineWebhookService(AppDbContext db, IOptions<LineOptions> options, TimeProvider time)
{
    public bool IsSignatureValid(string body, string? signature)
    {
        var secret = options.Value.ChannelSecret;
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signature)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        byte[] given;
        try { given = Convert.FromBase64String(signature); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public async Task<int> HandleAsync(string body, CancellationToken ct)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { return 0; }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return 0;

        var handled = 0;
        foreach (var e in events.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var userId = e.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object
                         && s.TryGetProperty("userId", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (string.IsNullOrEmpty(userId) || userId.Length > 100) continue;
            if (type is "follow") { await SetFollowingAsync(userId, true, ct); handled++; }
            else if (type is "unfollow") { await SetFollowingAsync(userId, false, ct); handled++; }
        }
        return handled;
    }

    private async Task SetFollowingAsync(string userId, bool following, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        // upsert แบบ atomic: webhook ซ้ำ/พร้อมกันไม่ทำให้เกิดแถวซ้ำ (LineUserId unique)
        if (following)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "LineAccounts" ("LineUserId", "IsFollowing", "FollowedAt", "CreatedAt")
                VALUES ({userId}, true, {now}, {now})
                ON CONFLICT ("LineUserId") DO UPDATE SET "IsFollowing" = true, "FollowedAt" = {now}
                """, ct);
        else
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "LineAccounts" ("LineUserId", "IsFollowing", "UnfollowedAt", "CreatedAt")
                VALUES ({userId}, false, {now}, {now})
                ON CONFLICT ("LineUserId") DO UPDATE SET "IsFollowing" = false, "UnfollowedAt" = {now}
                """, ct);
    }
}
