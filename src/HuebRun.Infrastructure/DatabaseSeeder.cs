using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure;

public static class DatabaseSeeder
{
    // ค่าคงที่ของ advisory lock กันหลาย instance seed พร้อมกันบน DB ว่าง (lookup ไม่มี unique จะซ้ำ / PK ชน)
    private const long SeedLockKey = 0x48756562_5365_6564; // "HuebSeed"

    public static async Task RunAsync(AppDbContext db, bool includeDevData, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({SeedLockKey})", ct);
            try
            {
                await SeedData.RunAsync(db, SeedData.GeoDir, ct);
                if (includeDevData)
                    await DevSeedData.RunAsync(db, ct);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({SeedLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
