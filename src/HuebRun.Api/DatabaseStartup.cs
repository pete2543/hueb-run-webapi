using HuebRun.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HuebRun.Api;

public static class DatabaseStartup
{
    /// seed ตอนเริ่มแอป: ข้ามถ้ายังมี migration ค้าง หรือเชื่อม DB ไม่ได้ (ให้แอปขึ้นได้และ /health ตอบ 503)
    public static async Task SeedDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
        try
        {
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
            logger.LogError(ex, "Cannot reach database; seed skipped");
        }
    }
}
