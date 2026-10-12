using System.Net;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using HuebRun.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public class StartupMigrationTests(PostgresFixture pg)
{
    [Fact]
    public async Task App_creates_schema_and_seeds_an_empty_database_on_startup()
    {
        var connectionString = await pg.CreateEmptyDatabaseAsync();
        using var factory = new ApiFactory(connectionString);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await db.Provinces.AnyAsync());
        Assert.True(await db.Nationalities.AnyAsync());
    }

    [Fact]
    public async Task Health_is_unhealthy_while_migrations_are_pending()
    {
        var connectionString = await pg.CreateEmptyDatabaseAsync();
        using var factory = new ApiFactory(connectionString,
            settings: new Dictionary<string, string> { ["Database:MigrateOnStartup"] = "false" });

        var res = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }
}
