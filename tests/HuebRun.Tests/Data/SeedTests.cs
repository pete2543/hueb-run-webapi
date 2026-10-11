using HuebRun.Infrastructure;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Data;

[Collection("db")]
public class SeedTests(PostgresFixture pg)
{
    [Fact]
    public async Task Seed_is_idempotent_and_loads_all_master_data()
    {
        await using (var db = pg.CreateDbContext()) await SeedData.RunAsync(db, SeedData.GeoDir);
        await using (var db = pg.CreateDbContext()) await SeedData.RunAsync(db, SeedData.GeoDir);

        await using var check = pg.CreateDbContext();
        Assert.Equal(5, await check.Titles.CountAsync(t => t.IsActive));   // test อื่นอาจเพิ่ม title ที่ inactive
        Assert.Equal(5, await check.Nationalities.CountAsync());
        Assert.Equal(5, await check.BloodTypes.CountAsync());
        Assert.Equal(6, await check.EmergencyRelations.CountAsync());
        Assert.Equal(4, await check.Roles.CountAsync());
        Assert.Equal(7, await check.NotificationTemplates.CountAsync());
        Assert.Equal(77, await check.Provinces.CountAsync());
        Assert.True(await check.Districts.CountAsync() > 900);
        Assert.True(await check.Subdistricts.CountAsync() > 7000);
    }

    [Fact]
    public async Task Geo_has_bangkok_and_five_digit_zip_codes()
    {
        await using (var db = pg.CreateDbContext()) await SeedData.RunAsync(db, SeedData.GeoDir);
        await using var check = pg.CreateDbContext();

        Assert.True(await check.Provinces.AnyAsync(p => p.NameTh == "กรุงเทพมหานคร"));
        Assert.True(await check.Subdistricts.AnyAsync(s => s.ZipCode == "10200"));
        Assert.False(await check.Subdistricts.AnyAsync(s => s.ZipCode.Length != 5));
    }

    [Fact]
    public async Task Dev_seed_creates_sample_event_once()
    {
        await using (var db = pg.CreateDbContext()) await DevSeedData.RunAsync(db);
        await using (var db = pg.CreateDbContext()) await DevSeedData.RunAsync(db);

        await using var check = pg.CreateDbContext();
        var ev = await check.Events
            .Include(e => e.Distances).Include(e => e.ShirtSizes).Include(e => e.ShippingOptions)
            .SingleAsync(e => e.Slug == DevSeedData.Slug);
        Assert.True(ev.IsPublished);
        Assert.Equal(3, ev.Distances.Count);
        Assert.Equal(6, ev.ShirtSizes.Count);
        Assert.Equal(2, ev.ShippingOptions.Count);
    }
}

[Collection("db")]
public class GeoSeedFilteringTests(PostgresFixture pg) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("geo-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Deleted_rows_and_orphans_are_skipped_without_breaking_foreign_keys()
    {
        File.WriteAllText(Path.Combine(_dir, "province.json"), """
            [ { "id": 1, "name_th": "จังหวัดเอ", "name_en": "A", "deleted_at": null },
              { "id": 2, "name_th": "จังหวัดที่ลบแล้ว", "name_en": "B", "deleted_at": "2025-01-01T00:00:00.000+07:00" } ]
            """);
        File.WriteAllText(Path.Combine(_dir, "district.json"), """
            [ { "id": 101, "name_th": "อำเภอเอ", "province_id": 1, "deleted_at": null },
              { "id": 201, "name_th": "อำเภอของจังหวัดที่ลบ", "province_id": 2, "deleted_at": null },
              { "id": 102, "name_th": "อำเภอที่ลบแล้ว", "province_id": 1, "deleted_at": "2025-01-01T00:00:00.000+07:00" } ]
            """);
        File.WriteAllText(Path.Combine(_dir, "sub_district.json"), """
            [ { "id": 10101, "zip_code": 1100, "name_th": "ตำบลเอ", "district_id": 101, "deleted_at": null },
              { "id": 20101, "zip_code": 20000, "name_th": "ตำบลกำพร้า", "district_id": 201, "deleted_at": null },
              { "id": 10201, "zip_code": 30000, "name_th": "ตำบลของอำเภอที่ลบ", "district_id": 102, "deleted_at": null } ]
            """);

        await using var db = await pg.CreateFreshDatabaseAsync();
        await SeedData.RunAsync(db, _dir);

        Assert.Equal([1], await db.Provinces.Select(p => p.Id).ToListAsync());
        Assert.Equal([101], await db.Districts.Select(d => d.Id).ToListAsync());
        var sub = await db.Subdistricts.SingleAsync();
        Assert.Equal(10101, sub.Id);
        Assert.Equal("01100", sub.ZipCode);
    }
}
