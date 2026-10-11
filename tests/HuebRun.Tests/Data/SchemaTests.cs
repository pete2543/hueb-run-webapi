using HuebRun.Domain;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Data;

[Collection("db")]
public class SchemaTests(PostgresFixture pg)
{
    [Fact]
    public async Task All_migrations_are_applied()
    {
        await using var db = pg.CreateDbContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Reserved_count_above_quota_is_rejected_by_check_constraint()
    {
        await using var db = pg.CreateDbContext();
        await Assert.ThrowsAsync<DbUpdateException>(() => TestData.CreateEventAsync(db, e =>
            e.Distances.Add(new EventDistance { Name = "3K", DistanceKm = 3, BibPrefix = "A", Quota = 10, ReservedCount = 11, Price = 100 })));
    }

    [Fact]
    public async Task Concurrent_update_of_distance_throws_concurrency_exception()
    {
        await using var setup = pg.CreateDbContext();
        var ev = await TestData.CreateEventAsync(setup, e =>
            e.Distances.Add(new EventDistance { Name = "5K", DistanceKm = 5, BibPrefix = "B", Quota = 10, Price = 100 }));
        var id = ev.Distances[0].Id;

        await using var a = pg.CreateDbContext();
        await using var b = pg.CreateDbContext();
        var da = await a.EventDistances.SingleAsync(d => d.Id == id);
        var dbb = await b.EventDistances.SingleAsync(d => d.Id == id);

        da.ReservedCount++;
        await a.SaveChangesAsync();

        dbb.ReservedCount++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
    }
}
