using HuebRun.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace HuebRun.Api;

public static class DatabaseStartup
{
    /// ตอนเริ่มแอป: migrate (ปิดได้ด้วย Database:MigrateOnStartup=false) แล้ว seed
    /// ข้าม seed ถ้ายังมี migration ค้าง หรือเชื่อม DB ไม่ได้ (ให้แอปขึ้นได้และ /health ตอบ 503)
    public static async Task SeedDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
        try
        {
            // EF ล็อก DB ระหว่าง migrate: รันหลาย instance พร้อมกันได้
            if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
                await db.Database.MigrateAsync();

            var pending = await db.Database.GetPendingMigrationsAsync();
            if (pending.Any())
            {
                logger.LogWarning("Database has pending migrations; run `dotnet ef database update`. Seed skipped.");
                return;
            }
            await DatabaseSeeder.RunAsync(db, includeDevData: app.Environment.IsDevelopment());
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Database migrate/seed failed; app keeps running and /health reports unhealthy");
        }
    }
}

/// ไม่ healthy จนกว่า migration จะลงครบ — กันเวอร์ชันที่ DB ยังไม่พร้อมรับ traffic
public sealed class MigrationsHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        return pending.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"{pending.Count} pending migration(s)");
    }
}
