using HuebRun.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace HuebRun.Tests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    // ฐานข้อมูลใหม่ว่าง ๆ ใน container เดียวกัน สำหรับ test ที่ต้องเริ่มจากตารางว่าง
    public async Task<AppDbContext> CreateFreshDatabaseAsync() => (await CreateFreshDatabaseWithConnectionAsync()).Db;

    public async Task<(AppDbContext Db, string ConnectionString)> CreateFreshDatabaseWithConnectionAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = CreateDbContext())
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {name}");
        var conn = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(conn).Options);
        await db.Database.MigrateAsync();
        return (db, conn);
    }

    // ฐานข้อมูลใหม่ที่ยังไม่มีตารางเลย (ไม่ migrate) คืน connection string
    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = "e_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = CreateDbContext())
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {name}");
        return new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<PostgresFixture>;
