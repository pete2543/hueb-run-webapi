# HuebRun API Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** วางโครง ASP.NET Core Web API (.NET 10) แบบแยกชั้น พร้อม schema/migration/seed และ public read endpoint ตาม `api-contract.md`

**Architecture:** `HuebRun.Api → HuebRun.Infrastructure → HuebRun.Domain` · Controllers คุยกับ `AppDbContext` ตรง ๆ (ยังไม่มี service layer เพราะเป็นงานอ่านล้วน) · กติการาคา/คงเหลือเป็น method บน entity ใน Domain · error ทุกตัวผ่าน `IExceptionHandler` ตัวเดียว

**Tech Stack:** .NET 10, ASP.NET Core Controllers, EF Core 10 + Npgsql, ASP.NET Identity (EF stores), xUnit 2.9, WebApplicationFactory, Testcontainers.PostgreSql, FakeTimeProvider

**Spec:** `docs/superpowers/specs/2026-10-11-api-foundation-design.md`

## Global Constraints

- Target framework `net10.0` ทุกโปรเจกต์; SDK pin ด้วย `global.json` (10.0.301, rollForward latestFeature)
- Namespace: `HuebRun.Domain`, `HuebRun.Infrastructure`, `HuebRun.Api.*`, `HuebRun.Tests.*`
- Route base `/api/v1`; `/health` อยู่นอก base
- JSON camelCase, enum เป็น string, เวลา UTC (`DateTimeKind.Utc` เสมอเมื่อเขียนลง DB)
- Error body: `{ "code": string, "message": string, "details": object|null }`
- ห้าม commit connection string/รหัสผ่าน — ใช้ `dotnet user-secrets` (UserSecretsId ในโปรเจกต์ Api)
- ไม่ auto-migrate ตอนแอปเริ่ม; seed รันตอนเริ่มเฉพาะเมื่อไม่มี pending migration
- Test ที่ใช้ DB ต้องเปิด Docker (Testcontainers image `postgres:18-alpine`)
- ข้อมูล geo: `kongvut/thai-province-data` โฟลเดอร์ `api/v2/` (flat `name_th`/`name_en`, prefix ติดในชื่อ) — **ขออนุญาตผู้ใช้ก่อนดาวน์โหลด** (3 ไฟล์ รวม ~2.5MB + LICENSE)

## Review Focus

1. slug ตัวพิมพ์ใหญ่ (`/events/Hueb-Run-2027`) → ควรเจองานเดียวกัน ไม่ใช่ 404 — test ใน Task 6
2. zip เป็นเลขไทย (`๑๐๒๐๐`) → 400 ไม่ใช่ค้นแล้วได้ `[]` (`char.IsDigit` ยอมรับเลขไทย ต้องใช้ `[0-9]`) — test ใน Task 8
3. DB เชื่อมไม่ได้ตอนเริ่มแอป (เช่นรหัสผิด) → แอปยังขึ้น และ `/health` ตอบ 503 — test ใน Task 5
4. เวลาที่ตอบออกไปต้องมี `Z` (UTC) ไม่ใช่เวลาไม่มี offset ที่หน้าเว็บจะตีความเป็นเวลาเครื่อง — test ใน Task 6
5. แถว geo ที่ `deleted_at` ไม่ null หรือแม่ถูกลบ → ต้องไม่ทำให้ seed พังด้วย FK — test ใน Task 4

---

## File Structure

```
global.json
HuebRun.slnx
.config/dotnet-tools.json                     dotnet-ef local tool
src/HuebRun.Domain/
  HuebRun.Domain.csproj
  Entities.cs            (จาก docs + method ราคา/คงเหลือ)
  MasterData.cs          (จาก docs)
  AdminEntities.cs       (จาก docs)
src/HuebRun.Infrastructure/
  HuebRun.Infrastructure.csproj
  AppDbContext.cs        (จาก docs)
  DependencyInjection.cs AddInfrastructure()
  SeedData.cs            (จาก docs, แก้ geo ให้ตรงไฟล์ v2)
  DevSeedData.cs         Event ตัวอย่างสำหรับ Development
  Migrations/            InitialCreate
  Seed/geo/              province.json, district.json, sub_district.json, LICENSE, SOURCE.md
src/HuebRun.Api/
  HuebRun.Api.csproj
  Program.cs
  appsettings.json, appsettings.Development.json
  DatabaseStartup.cs     seed ตอนเริ่มแอป
  Errors/AppException.cs, ErrorResponse.cs, AppExceptionHandler.cs, ValidationErrorResponse.cs
  Contracts/EventContracts.cs, LookupContracts.cs, GeoContracts.cs
  Controllers/EventsController.cs, LookupsController.cs, GeoController.cs
tests/HuebRun.Tests/
  HuebRun.Tests.csproj
  Infrastructure/PostgresFixture.cs, ApiFactory.cs, TestData.cs
  Domain/PricingRulesTests.cs
  Data/SchemaTests.cs, SeedTests.cs
  Api/ErrorHandlingTests.cs, HostTests.cs, EventsEndpointTests.cs, LookupsEndpointTests.cs, GeoEndpointTests.cs
```

---

### Task 1: Scaffold solution

**Files:**
- Create: `global.json`, `HuebRun.slnx`, `.config/dotnet-tools.json`, 4 โปรเจกต์ตามโครงด้านบน
- Delete: ไฟล์ตัวอย่างจาก template (`WeatherForecast*`, `*.http`, `UnitTest1.cs`, `Class1.cs`)

**Interfaces:**
- Produces: โปรเจกต์ build ได้, reference `Api→Infrastructure→Domain`, `Tests→Api`

- [ ] **Step 1: สร้าง branch และ commit เอกสาร**

```bash
git checkout -b feat/api-foundation
git add docs
git commit -m "docs: add project plan, API contract, schema drafts, and foundation spec/plan"
```

- [ ] **Step 2: สร้าง solution และโปรเจกต์**

```bash
dotnet new globaljson --sdk-version 10.0.301 --roll-forward latestFeature
dotnet new sln -n HuebRun
dotnet new classlib -n HuebRun.Domain -o src/HuebRun.Domain
dotnet new classlib -n HuebRun.Infrastructure -o src/HuebRun.Infrastructure
dotnet new webapi -n HuebRun.Api -o src/HuebRun.Api --use-controllers
dotnet new xunit -n HuebRun.Tests -o tests/HuebRun.Tests
rm src/HuebRun.Domain/Class1.cs src/HuebRun.Infrastructure/Class1.cs tests/HuebRun.Tests/UnitTest1.cs
rm -f src/HuebRun.Api/WeatherForecast.cs src/HuebRun.Api/Controllers/WeatherForecastController.cs src/HuebRun.Api/HuebRun.Api.http
dotnet sln add src/HuebRun.Domain src/HuebRun.Infrastructure src/HuebRun.Api tests/HuebRun.Tests
dotnet add src/HuebRun.Infrastructure reference src/HuebRun.Domain
dotnet add src/HuebRun.Api reference src/HuebRun.Infrastructure
dotnet add tests/HuebRun.Tests reference src/HuebRun.Api
```

ตรวจว่าได้ไฟล์ `HuebRun.slnx` (ค่าเริ่มต้นของ .NET 10) และ test project ใช้ `xunit` 2.9.x:

```bash
ls HuebRun.slnx && grep -i '"xunit"\|Include="xunit"' tests/HuebRun.Tests/HuebRun.Tests.csproj
```

- [ ] **Step 3: เพิ่มแพ็กเกจ**

```bash
dotnet add src/HuebRun.Domain package Microsoft.Extensions.Identity.Stores
dotnet add src/HuebRun.Infrastructure package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/HuebRun.Infrastructure package Microsoft.AspNetCore.Identity.EntityFrameworkCore
dotnet add src/HuebRun.Api package Microsoft.EntityFrameworkCore.Design
dotnet add src/HuebRun.Api package Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore
dotnet add tests/HuebRun.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet add tests/HuebRun.Tests package Testcontainers.PostgreSql
dotnet add tests/HuebRun.Tests package Microsoft.Extensions.TimeProvider.Testing
dotnet new tool-manifest
dotnet tool install dotnet-ef
```

ทุกแพ็กเกจ EF/ASP.NET ต้องเป็น major 10 — ตรวจด้วย `grep -h PackageReference src/*/*.csproj`

- [ ] **Step 4: เพิ่ม FrameworkReference ให้ Infrastructure และ Tests**

ใน `src/HuebRun.Infrastructure/HuebRun.Infrastructure.csproj` และ `tests/HuebRun.Tests/HuebRun.Tests.csproj` เพิ่มใน `<Project>`:

```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

- [ ] **Step 5: ทำ Program.cs ชั่วคราวให้ build ผ่าน**

แทนที่ `src/HuebRun.Api/Program.cs` ทั้งไฟล์:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();
app.Run();

public partial class Program { }
```

- [ ] **Step 6: Build**

Run: `dotnet build`
Expected: `Build succeeded` 0 error

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "chore: scaffold HuebRun solution (Domain, Infrastructure, Api, Tests)"
```

---

### Task 2: Domain entities + pricing rules

**Files:**
- Create: `src/HuebRun.Domain/Entities.cs`, `MasterData.cs`, `AdminEntities.cs` (คัดลอกจาก `docs/db-schema/`)
- Test: `tests/HuebRun.Tests/Domain/PricingRulesTests.cs`

**Interfaces:**
- Produces:
  - `bool Event.IsRegistrationOpen(DateTime utcNow)` — `RegistrationOpensAt <= utcNow < RegistrationClosesAt`
  - `bool EventDistance.IsEarlyBird(DateTime utcNow)`
  - `decimal EventDistance.GetEffectivePrice(DateTime utcNow)`
  - `int EventDistance.GetRemaining()` / `int EventShirtSize.GetRemaining()` — ไม่ติดลบ
  - entity ทั้งหมดใน namespace `HuebRun.Domain`

- [ ] **Step 1: คัดลอก entity และเปลี่ยน namespace**

```bash
for f in Entities MasterData AdminEntities; do
  sed 's/namespace RunEvent\.Domain;/namespace HuebRun.Domain;/' docs/db-schema/$f.cs > src/HuebRun.Domain/$f.cs
done
grep -n "RunEvent" src/HuebRun.Domain/*.cs || echo "no RunEvent left"
dotnet build src/HuebRun.Domain
```

Expected: `no RunEvent left`, build ผ่าน

- [ ] **Step 2: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Domain/PricingRulesTests.cs`:

```csharp
using HuebRun.Domain;

namespace HuebRun.Tests.Domain;

public class PricingRulesTests
{
    private static readonly DateTime Cutoff = new(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc);

    private static EventDistance Distance(decimal? earlyBird = 390m, DateTime? endsAt = null) => new()
    {
        Price = 450m,
        EarlyBirdPrice = earlyBird,
        EarlyBirdEndsAt = earlyBird is null ? null : endsAt ?? Cutoff,
        Quota = 100,
    };

    [Fact]
    public void Before_cutoff_uses_early_bird_price()
    {
        var d = Distance();
        Assert.True(d.IsEarlyBird(Cutoff.AddSeconds(-1)));
        Assert.Equal(390m, d.GetEffectivePrice(Cutoff.AddSeconds(-1)));
    }

    [Fact]
    public void At_cutoff_uses_regular_price()
    {
        var d = Distance();
        Assert.False(d.IsEarlyBird(Cutoff));
        Assert.Equal(450m, d.GetEffectivePrice(Cutoff));
    }

    [Fact]
    public void Without_early_bird_uses_regular_price()
    {
        var d = Distance(earlyBird: null);
        Assert.False(d.IsEarlyBird(Cutoff.AddDays(-30)));
        Assert.Equal(450m, d.GetEffectivePrice(Cutoff.AddDays(-30)));
    }

    [Fact]
    public void Early_bird_price_without_end_date_is_ignored()
    {
        var d = new EventDistance { Price = 450m, EarlyBirdPrice = 390m, EarlyBirdEndsAt = null };
        Assert.False(d.IsEarlyBird(Cutoff));
        Assert.Equal(450m, d.GetEffectivePrice(Cutoff));
    }

    [Theory]
    [InlineData(100, 30, 70)]
    [InlineData(100, 100, 0)]
    [InlineData(50, 80, 0)]
    public void Distance_remaining_never_negative(int quota, int reserved, int expected) =>
        Assert.Equal(expected, new EventDistance { Quota = quota, ReservedCount = reserved }.GetRemaining());

    [Theory]
    [InlineData(20, 5, 15)]
    [InlineData(10, 12, 0)]
    public void Shirt_remaining_never_negative(int stock, int reserved, int expected) =>
        Assert.Equal(expected, new EventShirtSize { Stock = stock, ReservedCount = reserved }.GetRemaining());

    [Fact]
    public void Registration_open_window_is_inclusive_start_exclusive_end()
    {
        var opens = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var closes = new DateTime(2027, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        var e = new Event { RegistrationOpensAt = opens, RegistrationClosesAt = closes };

        Assert.False(e.IsRegistrationOpen(opens.AddSeconds(-1)));
        Assert.True(e.IsRegistrationOpen(opens));
        Assert.True(e.IsRegistrationOpen(closes.AddSeconds(-1)));
        Assert.False(e.IsRegistrationOpen(closes));
    }
}
```

- [ ] **Step 3: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter FullyQualifiedName~PricingRulesTests`
Expected: compile error `'EventDistance' does not contain a definition for 'IsEarlyBird'`

- [ ] **Step 4: เพิ่ม method ใน `src/HuebRun.Domain/Entities.cs`**

ใน `class Event` ต่อท้ายบรรทัด `public List<Order> Orders { get; set; } = new();`:

```csharp

    public bool IsRegistrationOpen(DateTime utcNow) =>
        RegistrationOpensAt <= utcNow && utcNow < RegistrationClosesAt;
```

ใน `class EventDistance` ต่อท้ายบรรทัด `public uint Version { get; set; }                  // Postgres xmin (concurrency token)`:

```csharp

    // early bird ใช้ได้เมื่อมีทั้งราคาและวันตัด และยังไม่ถึงวันตัด (ตรงวันตัด = ราคาปกติ)
    public bool IsEarlyBird(DateTime utcNow) =>
        EarlyBirdPrice is not null && EarlyBirdEndsAt is not null && utcNow < EarlyBirdEndsAt;

    public decimal GetEffectivePrice(DateTime utcNow) => IsEarlyBird(utcNow) ? EarlyBirdPrice!.Value : Price;

    public int GetRemaining() => Math.Max(0, Quota - ReservedCount);
```

ใน `class EventShirtSize` ต่อท้ายบรรทัด `public uint Version { get; set; }` ของคลาสนั้น:

```csharp

    public int GetRemaining() => Math.Max(0, Stock - ReservedCount);
```

(ใช้ method ไม่ใช่ property เพื่อไม่ให้ EF พยายาม map เป็นคอลัมน์)

- [ ] **Step 5: รัน test ให้ผ่าน**

Run: `dotnet test --filter FullyQualifiedName~PricingRulesTests`
Expected: PASS ทั้งหมด (10 tests)

- [ ] **Step 6: Commit**

```bash
git add src/HuebRun.Domain tests/HuebRun.Tests/Domain
git commit -m "feat(domain): port entities and add early-bird/remaining rules"
```

---

### Task 3: AppDbContext + InitialCreate migration

**Files:**
- Create: `src/HuebRun.Infrastructure/AppDbContext.cs` (คัดลอก), `DependencyInjection.cs`, `Migrations/*`
- Create: `tests/HuebRun.Tests/Infrastructure/PostgresFixture.cs`, `tests/HuebRun.Tests/Infrastructure/TestData.cs`
- Test: `tests/HuebRun.Tests/Data/SchemaTests.cs`

**Interfaces:**
- Consumes: entity จาก Task 2
- Produces:
  - `HuebRun.Infrastructure.AppDbContext`
  - `IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)` — อ่าน `ConnectionStrings:DefaultConnection`
  - `PostgresFixture` (`ConnectionString`, `AppDbContext CreateDbContext()`) + `[Collection("db")]`
  - `TestData.CreateEventAsync(AppDbContext db, Action<Event>? configure = null) : Task<Event>`

- [ ] **Step 1: คัดลอก DbContext**

```bash
sed -e 's/using RunEvent\.Domain;/using HuebRun.Domain;/' -e 's/namespace RunEvent\.Data;/namespace HuebRun.Infrastructure;/' \
  docs/db-schema/AppDbContext.cs > src/HuebRun.Infrastructure/AppDbContext.cs
```

- [ ] **Step 2: สร้าง `src/HuebRun.Infrastructure/DependencyInjection.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HuebRun.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("DefaultConnection") ?? ""));
        return services;
    }
}
```

และใน `src/HuebRun.Api/Program.cs` เพิ่มหลังบรรทัด `var builder = ...`:

```csharp
builder.Services.AddInfrastructure(builder.Configuration);
```

พร้อม `using HuebRun.Infrastructure;` บนสุด แล้ว `dotnet build` ให้ผ่าน (แก้ compile error จาก schema ร่างตรงนี้ถ้ามี โดยรักษาความหมายเดิม)

- [ ] **Step 3: สร้าง migration**

```bash
dotnet ef migrations add InitialCreate -p src/HuebRun.Infrastructure -s src/HuebRun.Api -o Migrations
grep -c "ck_distance_reserved\|ck_shirt_reserved\|xmin" src/HuebRun.Infrastructure/Migrations/*_InitialCreate.cs
```

Expected: migration ถูกสร้าง และ grep เจอ check constraint ทั้งสองตัว + `xmin` (ถ้าไม่เจอ `xmin` ให้หยุดและรายงาน — concurrency token ไม่ทำงาน)

- [ ] **Step 4: สร้าง test fixture**

`tests/HuebRun.Tests/Infrastructure/PostgresFixture.cs`:

```csharp
using HuebRun.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace HuebRun.Tests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

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
```

`tests/HuebRun.Tests/Infrastructure/TestData.cs`:

```csharp
using HuebRun.Domain;
using HuebRun.Infrastructure;

namespace HuebRun.Tests.Infrastructure;

public static class TestData
{
    public static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    // Event ที่ publish แล้ว เปิดรับ 2026-10-01 ถึง 2027-01-31 slug ไม่ซ้ำกันทุกครั้ง
    public static async Task<Event> CreateEventAsync(AppDbContext db, Action<Event>? configure = null)
    {
        var e = new Event
        {
            Name = "Test Run",
            Slug = "t-" + Guid.NewGuid().ToString("N")[..12],
            Year = 2027,
            EventDate = Utc(2027, 2, 14),
            RegistrationOpensAt = Utc(2026, 10, 1),
            RegistrationClosesAt = Utc(2027, 1, 31),
            PromptPayId = "0812345678",
            PromptPayAccountName = "SECRET ACCOUNT",
            IsPublished = true,
        };
        configure?.Invoke(e);
        db.Events.Add(e);
        await db.SaveChangesAsync();
        return e;
    }
}
```

- [ ] **Step 5: เขียน schema test**

`tests/HuebRun.Tests/Data/SchemaTests.cs`:

```csharp
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
```

- [ ] **Step 6: รัน test**

Run: `dotnet test --filter FullyQualifiedName~SchemaTests` (เปิด Docker ก่อน)
Expected: PASS 3 tests. ถ้า concurrency test ไม่ throw แปลว่า xmin map ไม่ถูก — หยุดและรายงาน

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(infra): add AppDbContext, DI registration, and InitialCreate migration"
```

---

### Task 4: Seed data (lookups, roles, templates, geo, dev event)

**Files:**
- Create: `src/HuebRun.Infrastructure/SeedData.cs` (คัดลอก + แก้ geo), `DevSeedData.cs`
- Create: `src/HuebRun.Infrastructure/Seed/geo/{province.json,district.json,sub_district.json,LICENSE,SOURCE.md}`
- Modify: `src/HuebRun.Infrastructure/HuebRun.Infrastructure.csproj`
- Test: `tests/HuebRun.Tests/Data/SeedTests.cs`

**Interfaces:**
- Consumes: `AppDbContext`, `PostgresFixture`
- Produces:
  - `SeedData.RunAsync(AppDbContext db, string seedDir, CancellationToken ct = default)`
  - `SeedData.GeoDir` = `Path.Combine(AppContext.BaseDirectory, "Seed", "geo")`
  - `DevSeedData.RunAsync(AppDbContext db, CancellationToken ct = default)`, `DevSeedData.Slug = "hueb-run-2027"`

- [ ] **Step 1: ขออนุญาตผู้ใช้แล้วดาวน์โหลดไฟล์ geo**

แจ้งผู้ใช้: ดาวน์โหลดจาก `raw.githubusercontent.com/kongvut/thai-province-data/master/` ไฟล์ `api/v2/province.json` (~19KB), `api/v2/district.json` (~228KB), `api/v2/sub_district.json` (~2.3MB), `LICENSE` (MIT, 1KB) — รอคำตอบ yes ก่อน

```bash
mkdir -p src/HuebRun.Infrastructure/Seed/geo
R=https://raw.githubusercontent.com/kongvut/thai-province-data/master
for f in province district sub_district; do curl -fsSL "$R/api/v2/$f.json" -o src/HuebRun.Infrastructure/Seed/geo/$f.json; done
curl -fsSL "$R/LICENSE" -o src/HuebRun.Infrastructure/Seed/geo/LICENSE
python3 -c "import json;[print(f, len(json.load(open(f'src/HuebRun.Infrastructure/Seed/geo/{f}.json')))) for f in ['province','district','sub_district']]"
```

Expected: province 77, district ~928, sub_district ~7400+

`src/HuebRun.Infrastructure/Seed/geo/SOURCE.md`:

```markdown
ที่มา: https://github.com/kongvut/thai-province-data (โฟลเดอร์ `api/v2/`, shape แบบ flat `name_th`/`name_en`)
License: MIT (ดู `LICENSE` ในโฟลเดอร์นี้) · ดาวน์โหลดเมื่อ 2026-10-11
อัปเดต: ดาวน์โหลดไฟล์เดิมทับ แล้วสร้าง DB ใหม่ (seed ทำเฉพาะเมื่อตาราง Province ว่าง)
```

เพิ่มใน `HuebRun.Infrastructure.csproj`:

```xml
  <ItemGroup>
    <None Include="Seed/geo/*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Data/SeedTests.cs`:

```csharp
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
```

- [ ] **Step 3: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter FullyQualifiedName~SeedTests`
Expected: compile error `The name 'SeedData' does not exist`

- [ ] **Step 4: คัดลอกและแก้ SeedData**

```bash
sed -e 's/using RunEvent\.Domain;/using HuebRun.Domain;/' -e 's/namespace RunEvent\.Data;/namespace HuebRun.Infrastructure;/' \
  docs/db-schema/SeedData.cs > src/HuebRun.Infrastructure/SeedData.cs
```

ใน `src/HuebRun.Infrastructure/SeedData.cs`:

(ก) เปลี่ยน comment บนคลาสและเพิ่ม `GeoDir` เป็นสมาชิกแรกของคลาส:

```csharp
/// เรียกตอนเริ่มแอป: await SeedData.RunAsync(db, SeedData.GeoDir);
/// - ตัวเลือกทั่วไป/roles/เทมเพลต: seed ถ้าตารางว่าง
/// - จังหวัด/อำเภอ/ตำบล: อ่านจาก Seed/geo/*.json (kongvut/thai-province-data api/v2) ถ้าตารางว่าง
public static class SeedData
{
    public static string GeoDir => Path.Combine(AppContext.BaseDirectory, "Seed", "geo");
```

(ข) แทนที่ทั้งเมธอด `SeedThaiGeoAsync` และคลาส `GeoDto` ด้วย:

```csharp
    private static async Task SeedThaiGeoAsync(AppDbContext db, string seedDir, CancellationToken ct)
    {
        if (await db.Provinces.AnyAsync(ct)) return;

        // ข้าม record ที่ถูกลบ (deleted_at) และลูกที่แม่ไม่อยู่ในชุดข้อมูล เพื่อไม่ให้ FK พัง
        var provinces = (await ReadAsync<GeoDto>(Path.Combine(seedDir, "province.json"), ct))
            .Where(p => p.DeletedAt is null).ToList();
        var provinceIds = provinces.Select(p => p.Id).ToHashSet();
        var districts = (await ReadAsync<GeoDto>(Path.Combine(seedDir, "district.json"), ct))
            .Where(d => d.DeletedAt is null && d.ProvinceId is int pid && provinceIds.Contains(pid)).ToList();
        var districtIds = districts.Select(d => d.Id).ToHashSet();
        var subs = (await ReadAsync<GeoDto>(Path.Combine(seedDir, "sub_district.json"), ct))
            .Where(s => s.DeletedAt is null && s.DistrictId is int did && districtIds.Contains(did) && s.ZipCode is not null)
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Provinces.AddRange(provinces.Select(p => new Province
            { Id = p.Id, NameTh = p.NameTh, NameEn = p.NameEn }));
        await db.SaveChangesAsync(ct);

        db.Districts.AddRange(districts.Select(d => new District
            { Id = d.Id, ProvinceId = d.ProvinceId!.Value, NameTh = d.NameTh, NameEn = d.NameEn }));
        await db.SaveChangesAsync(ct);

        // ตำบลเป็นหลักพันแถว แบ่งเป็นก้อนให้ SaveChanges ไม่หนักเกินไป
        foreach (var chunk in subs.Chunk(1000))
        {
            db.Subdistricts.AddRange(chunk.Select(s => new Subdistrict
            {
                Id = s.Id, DistrictId = s.DistrictId!.Value,
                NameTh = s.NameTh, NameEn = s.NameEn, ZipCode = s.ZipCode!.Value.ToString("D5")
            }));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<List<T>> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<T>>(fs, cancellationToken: ct) ?? new();
    }

    // ตรงกับ kongvut/thai-province-data api/v2 (zip_code เป็นตัวเลข)
    private sealed class GeoDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name_th")] public string NameTh { get; set; } = "";
        [JsonPropertyName("name_en")] public string? NameEn { get; set; }
        [JsonPropertyName("province_id")] public int? ProvinceId { get; set; }
        [JsonPropertyName("district_id")] public int? DistrictId { get; set; }
        [JsonPropertyName("zip_code")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? ZipCode { get; set; }
        [JsonPropertyName("deleted_at")] public string? DeletedAt { get; set; }
    }
```

(เมธอด `ReadAsync` เดิมถูกแทนที่ด้วยตัวเดียวกันข้างบน ลบตัวเดิมออกให้เหลือตัวเดียว)

- [ ] **Step 5: สร้าง `src/HuebRun.Infrastructure/DevSeedData.cs`**

```csharp
using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure;

/// ข้อมูลงานตัวอย่างสำหรับ Development เท่านั้น ให้หน้าบ้านมีข้อมูลเรียกได้ทันที
public static class DevSeedData
{
    public const string Slug = "hueb-run-2027";

    public static async Task RunAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Events.AnyAsync(e => e.Slug == Slug, ct)) return;

        static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);
        var earlyBirdEnds = Utc(2026, 11, 30);

        db.Events.Add(new Event
        {
            Name = "Hueb Run 2027",
            Slug = Slug,
            Year = 2027,
            EventDate = Utc(2027, 2, 13, 22),          // 05:00 เวลาไทย 14 ก.พ. 2027
            RegistrationOpensAt = Utc(2026, 10, 1),
            RegistrationClosesAt = Utc(2027, 1, 31),
            PromptPayId = "0000000000",
            PromptPayAccountName = "บัญชีตัวอย่าง (dev)",
            IsPublished = true,
            Distances =
            {
                new() { Name = "Mini 3K", DistanceKm = 3, BibPrefix = "A", Quota = 300, Price = 450, EarlyBirdPrice = 390, EarlyBirdEndsAt = earlyBirdEnds },
                new() { Name = "Fun Run 5K", DistanceKm = 5, BibPrefix = "B", Quota = 400, Price = 550, EarlyBirdPrice = 490, EarlyBirdEndsAt = earlyBirdEnds },
                new() { Name = "Mini Marathon 10K", DistanceKm = 10, BibPrefix = "C", Quota = 300, Price = 650, EarlyBirdPrice = 590, EarlyBirdEndsAt = earlyBirdEnds },
            },
            ShirtSizes =
            {
                new() { Size = "XS", Detail = "รอบอก 34 นิ้ว", Stock = 80 },
                new() { Size = "S", Detail = "รอบอก 36 นิ้ว", Stock = 180 },
                new() { Size = "M", Detail = "รอบอก 38 นิ้ว", Stock = 250 },
                new() { Size = "L", Detail = "รอบอก 40 นิ้ว", Stock = 250 },
                new() { Size = "XL", Detail = "รอบอก 42 นิ้ว", Stock = 180 },
                new() { Size = "3XL", Detail = "รอบอก 46 นิ้ว", Stock = 80 },
            },
            ShippingOptions =
            {
                new() { Type = ShippingType.Pickup, Name = "รับเองหน้างาน", Fee = 0 },
                new() { Type = ShippingType.Post, Name = "ส่งไปรษณีย์ (EMS)", Fee = 60 },
            },
        });
        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 6: รัน test ให้ผ่าน**

Run: `dotnet test --filter FullyQualifiedName~SeedTests`
Expected: PASS 3 tests. ถ้าล้มด้วย `FileNotFoundException` ให้ตรวจว่า `tests/HuebRun.Tests/bin/Debug/net10.0/Seed/geo/province.json` มีอยู่ (ไฟล์ต้องถูกคัดลอกต่อจาก Infrastructure)

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(infra): seed lookups, roles, templates, Thai geo (kongvut v2, MIT) and dev sample event"
```

---

### Task 5: API host — errors, CORS, health, OpenAPI, startup seed

**Files:**
- Create: `src/HuebRun.Api/Errors/AppException.cs`, `ErrorResponse.cs`, `AppExceptionHandler.cs`, `ValidationErrorResponse.cs`
- Create: `src/HuebRun.Api/DatabaseStartup.cs`
- Modify: `src/HuebRun.Api/Program.cs`, `appsettings.json`, `appsettings.Development.json`
- Create: `tests/HuebRun.Tests/Infrastructure/ApiFactory.cs`
- Test: `tests/HuebRun.Tests/Api/ErrorHandlingTests.cs`, `tests/HuebRun.Tests/Api/HostTests.cs`

**Interfaces:**
- Consumes: `AddInfrastructure`, `SeedData`, `DevSeedData`
- Produces:
  - `AppException(int statusCode, string code, string message, object? details = null)` with `StatusCode`, `Code`, `Details`
  - `record ErrorResponse(string Code, string Message, object? Details)`
  - `ApiFactory(string connectionString)` : `WebApplicationFactory<Program>` with `FakeTimeProvider Time` (เริ่มที่ 2026-11-15T00:00Z)
  - `TimeProvider` ลงทะเบียนใน DI

- [ ] **Step 1: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Api/ErrorHandlingTests.cs`:

```csharp
using System.Text.Json;
using HuebRun.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace HuebRun.Tests.Api;

public class ErrorHandlingTests
{
    private static async Task<(int Status, JsonElement Body, string Raw)> Handle(Exception ex)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var handled = await new AppExceptionHandler(NullLogger<AppExceptionHandler>.Instance)
            .TryHandleAsync(ctx, ex, CancellationToken.None);
        Assert.True(handled);
        ctx.Response.Body.Position = 0;
        var raw = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, JsonDocument.Parse(raw).RootElement, raw);
    }

    [Fact]
    public async Task App_exception_maps_to_its_status_and_code()
    {
        var (status, body, _) = await Handle(new AppException(404, "EVENT_NOT_FOUND", "ไม่พบงานนี้", new { slug = "x" }));
        Assert.Equal(404, status);
        Assert.Equal("EVENT_NOT_FOUND", body.GetProperty("code").GetString());
        Assert.Equal("ไม่พบงานนี้", body.GetProperty("message").GetString());
        Assert.Equal("x", body.GetProperty("details").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Unexpected_exception_returns_500_without_leaking_details()
    {
        var (status, body, raw) = await Handle(new InvalidOperationException("db password is hunter2"));
        Assert.Equal(500, status);
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("details").ValueKind);
        Assert.DoesNotContain("hunter2", raw);
    }
}
```

`tests/HuebRun.Tests/Infrastructure/ApiFactory.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace HuebRun.Tests.Infrastructure;

public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
        });
    }
}
```

`tests/HuebRun.Tests/Api/HostTests.cs`:

```csharp
using System.Net;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public class HostTests(PostgresFixture pg)
{
    [Fact]
    public async Task Health_is_ok_when_database_is_reachable()
    {
        using var factory = new ApiFactory(pg.ConnectionString);
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Healthy", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cors_allows_configured_frontend_origin()
    {
        using var factory = new ApiFactory(pg.ConnectionString);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/lookups");
        req.Headers.Add("Origin", "http://localhost:3000");
        var res = await factory.CreateClient().SendAsync(req);
        Assert.Equal("http://localhost:3000", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Cors_rejects_unknown_origin()
    {
        using var factory = new ApiFactory(pg.ConnectionString);
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("Origin", "https://evil.example");
        var res = await factory.CreateClient().SendAsync(req);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

public class HostWithoutDatabaseTests
{
    [Fact]
    public async Task App_starts_and_health_is_503_when_database_is_unreachable()
    {
        using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2");
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }
}
```

(หมายเหตุ: `Cors_allows_configured_frontend_origin` เรียก `/api/v1/lookups` ซึ่งจะได้ 404 จนถึง Task 7 — CORS header ยังถูกใส่แม้ 404 จึงผ่านได้ตั้งแต่ task นี้)

- [ ] **Step 2: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter "FullyQualifiedName~ErrorHandlingTests|FullyQualifiedName~HostTests|FullyQualifiedName~HostWithoutDatabaseTests"`
Expected: compile error `The type or namespace name 'Errors' does not exist`

- [ ] **Step 3: สร้างไฟล์ Errors**

`src/HuebRun.Api/Errors/AppException.cs`:

```csharp
namespace HuebRun.Api.Errors;

/// ข้อผิดพลาดที่ตั้งใจตอบกลับไปหาผู้เรียก (ข้อความต้องปลอดภัยที่จะแสดง)
public sealed class AppException(int statusCode, string code, string message, object? details = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public object? Details { get; } = details;
}
```

`src/HuebRun.Api/Errors/ErrorResponse.cs`:

```csharp
namespace HuebRun.Api.Errors;

public sealed record ErrorResponse(string Code, string Message, object? Details);
```

`src/HuebRun.Api/Errors/AppExceptionHandler.cs`:

```csharp
using Microsoft.AspNetCore.Diagnostics;

namespace HuebRun.Api.Errors;

public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        int status;
        ErrorResponse body;
        if (exception is AppException app)
        {
            status = app.StatusCode;
            body = new ErrorResponse(app.Code, app.Message, app.Details);
        }
        else
        {
            logger.LogError(exception, "Unhandled exception");
            status = StatusCodes.Status500InternalServerError;
            body = new ErrorResponse("INTERNAL_ERROR", "เกิดข้อผิดพลาดภายในระบบ", null);
        }

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(body, ct);
        return true;
    }
}
```

`src/HuebRun.Api/Errors/ValidationErrorResponse.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Errors;

/// แปลง ModelState ที่ไม่ผ่านของ [ApiController] ให้อยู่ในรูปแบบ error กลาง
public static class ValidationErrorResponse
{
    public static IActionResult Create(ActionContext context)
    {
        var details = context.ModelState
            .Where(kv => kv.Value is { Errors.Count: > 0 })
            .ToDictionary(kv => kv.Key, kv => kv.Value!.Errors.Select(e => e.ErrorMessage).ToArray());
        return new BadRequestObjectResult(new ErrorResponse("VALIDATION_ERROR", "ข้อมูลไม่ถูกต้อง", details));
    }
}
```

- [ ] **Step 4: สร้าง `src/HuebRun.Api/DatabaseStartup.cs`**

```csharp
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
            await SeedData.RunAsync(db, SeedData.GeoDir);
            if (app.Environment.IsDevelopment())
                await DevSeedData.RunAsync(db);
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Cannot reach database; seed skipped");
        }
    }
}
```

- [ ] **Step 5: แทนที่ `src/HuebRun.Api/Program.cs` ทั้งไฟล์**

```csharp
using System.Text.Json.Serialization;
using HuebRun.Api;
using HuebRun.Api.Errors;
using HuebRun.Infrastructure;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<ApiBehaviorOptions>(o =>
    o.InvalidModelStateResponseFactory = ValidationErrorResponse.Create);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapControllers();

await app.SeedDatabaseAsync();
app.Run();

public partial class Program { }
```

- [ ] **Step 6: ตั้งค่า appsettings**

แทนที่ `src/HuebRun.Api/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": ""
  },
  "Cors": {
    "AllowedOrigins": [ "http://localhost:3000" ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

แทนที่ `src/HuebRun.Api/appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  }
}
```

ตั้ง user-secrets id (ยังไม่ใส่ค่า — ทำใน Task 9):

```bash
dotnet user-secrets init --project src/HuebRun.Api
```

- [ ] **Step 7: รัน test ให้ผ่าน**

Run: `dotnet test --filter "FullyQualifiedName~ErrorHandlingTests|FullyQualifiedName~HostTests|FullyQualifiedName~HostWithoutDatabaseTests"`
Expected: PASS 6 tests

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(api): error format, CORS, health check, OpenAPI, and startup seeding"
```

---

### Task 6: Event endpoints

**Files:**
- Create: `src/HuebRun.Api/Contracts/EventContracts.cs`, `src/HuebRun.Api/Controllers/EventsController.cs`
- Test: `tests/HuebRun.Tests/Api/EventsEndpointTests.cs`

**Interfaces:**
- Consumes: `Event.IsRegistrationOpen`, `EventDistance.IsEarlyBird/GetEffectivePrice/GetRemaining`, `EventShirtSize.GetRemaining`, `AppException`, `ApiFactory`, `TestData`
- Produces: `GET /api/v1/events/{slug}`, `/distances`, `/shirt-sizes`, `/shipping-options`

- [ ] **Step 1: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Api/EventsEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Tests.Infrastructure;
using static HuebRun.Tests.Infrastructure.TestData;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class EventsEndpointTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg.ConnectionString);   // เวลา = 2026-11-15T00:00Z
    private HttpClient Client => _factory.CreateClient();
    public void Dispose() => _factory.Dispose();

    private async Task<Event> SeedEventAsync(Action<Event>? configure = null)
    {
        await using var db = pg.CreateDbContext();
        return await CreateEventAsync(db, configure);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AssertEventNotFound(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EVENT_NOT_FOUND", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_event_returns_public_fields_without_promptpay()
    {
        var ev = await SeedEventAsync();
        var res = await Client.GetAsync($"/api/v1/events/{ev.Slug}");
        var raw = await res.Content.ReadAsStringAsync();
        var body = await Json(res);

        Assert.Equal(ev.Id, body.GetProperty("id").GetInt32());
        Assert.Equal("Test Run", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("registrationOpen").GetBoolean());
        Assert.DoesNotContain("promptPay", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET ACCOUNT", raw);
    }

    [Fact]
    public async Task Event_times_are_serialized_as_utc()
    {
        var ev = await SeedEventAsync();
        var body = await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}"));
        Assert.Equal("2026-10-01T00:00:00Z", body.GetProperty("registrationOpensAt").GetString());
    }

    [Fact]
    public async Task Registration_closed_after_close_time()
    {
        var ev = await SeedEventAsync(e => e.RegistrationClosesAt = Utc(2026, 11, 1));
        var body = await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}"));
        Assert.False(body.GetProperty("registrationOpen").GetBoolean());
    }

    [Fact]
    public async Task Slug_lookup_is_case_insensitive()
    {
        var ev = await SeedEventAsync();
        var res = await Client.GetAsync($"/api/v1/events/{ev.Slug.ToUpperInvariant()}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/distances")]
    [InlineData("/shirt-sizes")]
    [InlineData("/shipping-options")]
    public async Task Unpublished_event_is_not_found_on_every_route(string suffix)
    {
        var ev = await SeedEventAsync(e => e.IsPublished = false);
        await AssertEventNotFound(await Client.GetAsync($"/api/v1/events/{ev.Slug}{suffix}"));
    }

    [Fact]
    public async Task Unknown_slug_is_not_found()
    {
        await AssertEventNotFound(await Client.GetAsync("/api/v1/events/no-such-event"));
    }

    [Fact]
    public async Task Distances_show_effective_price_and_remaining_sorted_by_km()
    {
        var ev = await SeedEventAsync(e =>
        {
            e.Distances.Add(new EventDistance { Name = "10K", DistanceKm = 10, BibPrefix = "C", Quota = 50, ReservedCount = 50, Price = 650 });
            e.Distances.Add(new EventDistance { Name = "3K", DistanceKm = 3, BibPrefix = "A", Quota = 100, ReservedCount = 30,
                Price = 450, EarlyBirdPrice = 390, EarlyBirdEndsAt = Utc(2026, 11, 30) });
        });

        var body = await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}/distances"));
        var items = body.EnumerateArray().ToList();

        Assert.Equal(["3K", "10K"], items.Select(i => i.GetProperty("name").GetString()));
        Assert.Equal(390m, items[0].GetProperty("price").GetDecimal());
        Assert.Equal(450m, items[0].GetProperty("regularPrice").GetDecimal());
        Assert.True(items[0].GetProperty("isEarlyBird").GetBoolean());
        Assert.Equal(70, items[0].GetProperty("remaining").GetInt32());
        Assert.Equal(650m, items[1].GetProperty("price").GetDecimal());
        Assert.False(items[1].GetProperty("isEarlyBird").GetBoolean());
        Assert.Equal(0, items[1].GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task Distances_use_regular_price_after_early_bird_cutoff()
    {
        var ev = await SeedEventAsync(e => e.Distances.Add(new EventDistance { Name = "5K", DistanceKm = 5, BibPrefix = "B",
            Quota = 10, Price = 550, EarlyBirdPrice = 490, EarlyBirdEndsAt = Utc(2026, 11, 30) }));
        _factory.Time.SetUtcNow(new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero));

        var item = (await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}/distances"))).EnumerateArray().Single();
        Assert.Equal(550m, item.GetProperty("price").GetDecimal());
        Assert.False(item.GetProperty("isEarlyBird").GetBoolean());
    }

    [Fact]
    public async Task Shirt_sizes_show_remaining()
    {
        var ev = await SeedEventAsync(e =>
        {
            e.ShirtSizes.Add(new EventShirtSize { Size = "S", Detail = "รอบอก 36 นิ้ว", Stock = 20, ReservedCount = 5 });
            e.ShirtSizes.Add(new EventShirtSize { Size = "M", Stock = 10, ReservedCount = 10 });
        });

        var items = (await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}/shirt-sizes"))).EnumerateArray().ToList();
        Assert.Equal(["S", "M"], items.Select(i => i.GetProperty("size").GetString()));
        Assert.Equal("รอบอก 36 นิ้ว", items[0].GetProperty("detail").GetString());
        Assert.Equal(15, items[0].GetProperty("remaining").GetInt32());
        Assert.Equal(0, items[1].GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task Shipping_options_hide_inactive_and_use_string_type()
    {
        var ev = await SeedEventAsync(e =>
        {
            e.ShippingOptions.Add(new ShippingOption { Type = ShippingType.Pickup, Name = "รับเอง", Fee = 0 });
            e.ShippingOptions.Add(new ShippingOption { Type = ShippingType.Post, Name = "EMS", Fee = 60 });
            e.ShippingOptions.Add(new ShippingOption { Type = ShippingType.Post, Name = "ปิดแล้ว", Fee = 40, IsActive = false });
        });

        var items = (await Json(await Client.GetAsync($"/api/v1/events/{ev.Slug}/shipping-options"))).EnumerateArray().ToList();
        Assert.Equal(["รับเอง", "EMS"], items.Select(i => i.GetProperty("name").GetString()));
        Assert.Equal("Pickup", items[0].GetProperty("type").GetString());
        Assert.Equal(60m, items[1].GetProperty("fee").GetDecimal());
    }
}
```

- [ ] **Step 2: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter FullyQualifiedName~EventsEndpointTests`
Expected: FAIL — 404 ทุกเส้น (ยังไม่มี controller) หรือ assertion `EVENT_NOT_FOUND` ไม่ตรงเพราะ body ว่าง

- [ ] **Step 3: สร้าง contracts**

`src/HuebRun.Api/Contracts/EventContracts.cs`:

```csharp
using HuebRun.Domain;

namespace HuebRun.Api.Contracts;

public sealed record EventResponse(
    int Id, string Slug, string Name, int Year, DateTime EventDate,
    DateTime RegistrationOpensAt, DateTime RegistrationClosesAt, bool RegistrationOpen);

public sealed record DistanceResponse(
    int Id, string Name, decimal DistanceKm, decimal Price, decimal RegularPrice,
    bool IsEarlyBird, DateTime? EarlyBirdEndsAt, int Quota, int Remaining);

public sealed record ShirtSizeResponse(int Id, string Size, string? Detail, int Remaining);

public sealed record ShippingOptionResponse(int Id, ShippingType Type, string Name, decimal Fee);
```

- [ ] **Step 4: สร้าง controller**

`src/HuebRun.Api/Controllers/EventsController.cs`:

```csharp
using HuebRun.Api.Contracts;
using HuebRun.Api.Errors;
using HuebRun.Domain;
using HuebRun.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/events/{slug}")]
public sealed class EventsController(AppDbContext db, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<EventResponse> Get(string slug, CancellationToken ct)
    {
        var e = await Published(slug).AsNoTracking().FirstOrDefaultAsync(ct) ?? throw EventNotFound(slug);
        return new EventResponse(e.Id, e.Slug, e.Name, e.Year, e.EventDate,
            e.RegistrationOpensAt, e.RegistrationClosesAt, e.IsRegistrationOpen(Now));
    }

    [HttpGet("distances")]
    public async Task<IReadOnlyList<DistanceResponse>> GetDistances(string slug, CancellationToken ct)
    {
        var eventId = await GetEventIdAsync(slug, ct);
        var rows = await db.EventDistances.AsNoTracking()
            .Where(d => d.EventId == eventId)
            .OrderBy(d => d.DistanceKm).ThenBy(d => d.Id)
            .ToListAsync(ct);
        var now = Now;
        return rows.Select(d => new DistanceResponse(d.Id, d.Name, d.DistanceKm, d.GetEffectivePrice(now), d.Price,
            d.IsEarlyBird(now), d.EarlyBirdEndsAt, d.Quota, d.GetRemaining())).ToList();
    }

    [HttpGet("shirt-sizes")]
    public async Task<IReadOnlyList<ShirtSizeResponse>> GetShirtSizes(string slug, CancellationToken ct)
    {
        var eventId = await GetEventIdAsync(slug, ct);
        var rows = await db.EventShirtSizes.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
        return rows.Select(s => new ShirtSizeResponse(s.Id, s.Size, s.Detail, s.GetRemaining())).ToList();
    }

    [HttpGet("shipping-options")]
    public async Task<IReadOnlyList<ShippingOptionResponse>> GetShippingOptions(string slug, CancellationToken ct)
    {
        var eventId = await GetEventIdAsync(slug, ct);
        return await db.ShippingOptions.AsNoTracking()
            .Where(s => s.EventId == eventId && s.IsActive)
            .OrderBy(s => s.Id)
            .Select(s => new ShippingOptionResponse(s.Id, s.Type, s.Name, s.Fee))
            .ToListAsync(ct);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // slug เก็บเป็นตัวพิมพ์เล็กเสมอ
    private IQueryable<Event> Published(string slug)
    {
        var normalized = slug.ToLowerInvariant();
        return db.Events.Where(e => e.Slug == normalized && e.IsPublished);
    }

    private async Task<int> GetEventIdAsync(string slug, CancellationToken ct) =>
        await Published(slug).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct) ?? throw EventNotFound(slug);

    private static AppException EventNotFound(string slug) =>
        new(StatusCodes.Status404NotFound, "EVENT_NOT_FOUND", "ไม่พบงานนี้", new { slug });
}
```

- [ ] **Step 5: รัน test ให้ผ่าน**

Run: `dotnet test --filter FullyQualifiedName~EventsEndpointTests`
Expected: PASS ทั้งหมด (13 tests)

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(api): public event, distances, shirt-sizes, shipping-options endpoints"
```

---

### Task 7: Lookups endpoint

**Files:**
- Create: `src/HuebRun.Api/Contracts/LookupContracts.cs`, `src/HuebRun.Api/Controllers/LookupsController.cs`
- Test: `tests/HuebRun.Tests/Api/LookupsEndpointTests.cs`

**Interfaces:**
- Consumes: `LookupBase` และ subclass, seed จาก Task 4 (รันตอนแอปเริ่ม)
- Produces: `GET /api/v1/lookups`

- [ ] **Step 1: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Api/LookupsEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class LookupsEndpointTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg.ConnectionString);
    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetLookups()
    {
        var res = await _factory.CreateClient().GetAsync("/api/v1/lookups");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Returns_all_four_lists_sorted_by_sort_order()
    {
        var body = await GetLookups();

        var titles = body.GetProperty("titles").EnumerateArray().Select(t => t.GetProperty("nameTh").GetString()).ToList();
        Assert.Equal(["นาย", "นาง", "นางสาว", "เด็กชาย", "เด็กหญิง"], titles.Take(5));
        Assert.Equal("Mr.", body.GetProperty("titles")[0].GetProperty("nameEn").GetString());
        Assert.NotEmpty(body.GetProperty("nationalities").EnumerateArray());
        Assert.NotEmpty(body.GetProperty("bloodTypes").EnumerateArray());
        Assert.Equal("อื่น ๆ", body.GetProperty("emergencyRelations").EnumerateArray().Last().GetProperty("nameTh").GetString());
    }

    [Fact]
    public async Task Inactive_items_are_hidden()
    {
        await GetLookups(); // ให้ seed ทำงานก่อน
        await using (var db = pg.CreateDbContext())
        {
            db.Titles.Add(new Title { NameTh = "ยศที่ปิดแล้ว", SortOrder = 50, IsActive = false });
            await db.SaveChangesAsync();
        }

        var body = await GetLookups();
        Assert.DoesNotContain(body.GetProperty("titles").EnumerateArray(),
            t => t.GetProperty("nameTh").GetString() == "ยศที่ปิดแล้ว");
    }
}
```

- [ ] **Step 2: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter FullyQualifiedName~LookupsEndpointTests`
Expected: FAIL `Expected: OK, Actual: NotFound`

- [ ] **Step 3: สร้าง contracts และ controller**

`src/HuebRun.Api/Contracts/LookupContracts.cs`:

```csharp
namespace HuebRun.Api.Contracts;

public sealed record LookupItem(int Id, string NameTh, string? NameEn);

public sealed record LookupsResponse(
    IReadOnlyList<LookupItem> Titles,
    IReadOnlyList<LookupItem> Nationalities,
    IReadOnlyList<LookupItem> BloodTypes,
    IReadOnlyList<LookupItem> EmergencyRelations);
```

`src/HuebRun.Api/Controllers/LookupsController.cs`:

```csharp
using HuebRun.Api.Contracts;
using HuebRun.Domain;
using HuebRun.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/lookups")]
public sealed class LookupsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<LookupsResponse> Get(CancellationToken ct) => new(
        await ListAsync(db.Titles, ct),
        await ListAsync(db.Nationalities, ct),
        await ListAsync(db.BloodTypes, ct),
        await ListAsync(db.EmergencyRelations, ct));

    private static async Task<IReadOnlyList<LookupItem>> ListAsync<T>(IQueryable<T> source, CancellationToken ct)
        where T : LookupBase =>
        await source.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new LookupItem(x.Id, x.NameTh, x.NameEn))
            .ToListAsync(ct);
}
```

- [ ] **Step 4: รัน test ให้ผ่าน**

Run: `dotnet test --filter "FullyQualifiedName~LookupsEndpointTests|FullyQualifiedName~HostTests"`
Expected: PASS ทั้งหมด

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(api): lookups endpoint"
```

---

### Task 8: Geo endpoints

**Files:**
- Create: `src/HuebRun.Api/Contracts/GeoContracts.cs`, `src/HuebRun.Api/Controllers/GeoController.cs`
- Test: `tests/HuebRun.Tests/Api/GeoEndpointTests.cs`

**Interfaces:**
- Consumes: geo seed จาก Task 4, `AppException`, `ValidationErrorResponse`
- Produces: `GET /api/v1/geo/provinces`, `/provinces/{id}/districts`, `/districts/{id}/subdistricts`, `/zip/{zipCode}`

- [ ] **Step 1: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Api/GeoEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class GeoEndpointTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg.ConnectionString);
    private HttpClient Client => _factory.CreateClient();
    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> Body(HttpResponseMessage res, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Provinces_returns_77()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/provinces"));
        Assert.Equal(77, body.GetArrayLength());
        Assert.Contains(body.EnumerateArray(), p => p.GetProperty("nameTh").GetString() == "กรุงเทพมหานคร");
    }

    [Fact]
    public async Task Bangkok_districts_then_subdistricts_cascade()
    {
        var districts = await Body(await Client.GetAsync("/api/v1/geo/provinces/1/districts"));
        var phraNakhon = districts.EnumerateArray().Single(d => d.GetProperty("nameTh").GetString() == "เขตพระนคร");

        var subs = await Body(await Client.GetAsync($"/api/v1/geo/districts/{phraNakhon.GetProperty("id").GetInt32()}/subdistricts"));
        var first = subs.EnumerateArray().Single(s => s.GetProperty("nameTh").GetString() == "พระบรมมหาราชวัง");
        Assert.Equal("10200", first.GetProperty("zipCode").GetString());
    }

    [Fact]
    public async Task Unknown_province_is_404()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/provinces/999999/districts"), HttpStatusCode.NotFound);
        Assert.Equal("PROVINCE_NOT_FOUND", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_district_is_404()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/districts/999999/subdistricts"), HttpStatusCode.NotFound);
        Assert.Equal("DISTRICT_NOT_FOUND", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Non_numeric_id_is_validation_error()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/provinces/abc/districts"), HttpStatusCode.BadRequest);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Zip_lookup_returns_full_address_chain()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/zip/10200"));
        var match = body.EnumerateArray().First(m => m.GetProperty("subdistrict").GetProperty("nameTh").GetString() == "พระบรมมหาราชวัง");
        Assert.Equal("เขตพระนคร", match.GetProperty("district").GetProperty("nameTh").GetString());
        Assert.Equal("กรุงเทพมหานคร", match.GetProperty("province").GetProperty("nameTh").GetString());
        Assert.Equal("10200", match.GetProperty("zipCode").GetString());
    }

    [Fact]
    public async Task Unknown_zip_returns_empty_list()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/zip/99999"));
        Assert.Equal(0, body.GetArrayLength());
    }

    [Theory]
    [InlineData("1020")]
    [InlineData("102000")]
    [InlineData("10a00")]
    [InlineData("๑๐๒๐๐")]
    public async Task Malformed_zip_is_validation_error(string zip)
    {
        var body = await Body(await Client.GetAsync($"/api/v1/geo/zip/{Uri.EscapeDataString(zip)}"), HttpStatusCode.BadRequest);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
    }
}
```

- [ ] **Step 2: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter FullyQualifiedName~GeoEndpointTests`
Expected: FAIL `Expected: OK, Actual: NotFound`

- [ ] **Step 3: สร้าง contracts และ controller**

`src/HuebRun.Api/Contracts/GeoContracts.cs`:

```csharp
namespace HuebRun.Api.Contracts;

public sealed record GeoItem(int Id, string NameTh, string? NameEn);

public sealed record SubdistrictItem(int Id, string NameTh, string? NameEn, string ZipCode);

public sealed record GeoRef(int Id, string NameTh);

public sealed record ZipMatch(GeoRef Subdistrict, GeoRef District, GeoRef Province, string ZipCode);
```

`src/HuebRun.Api/Controllers/GeoController.cs`:

```csharp
using System.Text.RegularExpressions;
using HuebRun.Api.Contracts;
using HuebRun.Api.Errors;
using HuebRun.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/geo")]
public sealed partial class GeoController(AppDbContext db) : ControllerBase
{
    [HttpGet("provinces")]
    public async Task<IReadOnlyList<GeoItem>> GetProvinces(CancellationToken ct) =>
        await db.Provinces.AsNoTracking()
            .OrderBy(p => p.NameTh)
            .Select(p => new GeoItem(p.Id, p.NameTh, p.NameEn))
            .ToListAsync(ct);

    [HttpGet("provinces/{id}/districts")]
    public async Task<IReadOnlyList<GeoItem>> GetDistricts(int id, CancellationToken ct)
    {
        if (!await db.Provinces.AnyAsync(p => p.Id == id, ct))
            throw new AppException(StatusCodes.Status404NotFound, "PROVINCE_NOT_FOUND", "ไม่พบจังหวัด", new { id });
        return await db.Districts.AsNoTracking()
            .Where(d => d.ProvinceId == id)
            .OrderBy(d => d.NameTh)
            .Select(d => new GeoItem(d.Id, d.NameTh, d.NameEn))
            .ToListAsync(ct);
    }

    [HttpGet("districts/{id}/subdistricts")]
    public async Task<IReadOnlyList<SubdistrictItem>> GetSubdistricts(int id, CancellationToken ct)
    {
        if (!await db.Districts.AnyAsync(d => d.Id == id, ct))
            throw new AppException(StatusCodes.Status404NotFound, "DISTRICT_NOT_FOUND", "ไม่พบอำเภอ/เขต", new { id });
        return await db.Subdistricts.AsNoTracking()
            .Where(s => s.DistrictId == id)
            .OrderBy(s => s.NameTh)
            .Select(s => new SubdistrictItem(s.Id, s.NameTh, s.NameEn, s.ZipCode))
            .ToListAsync(ct);
    }

    [HttpGet("zip/{zipCode}")]
    public async Task<IReadOnlyList<ZipMatch>> GetByZip(string zipCode, CancellationToken ct)
    {
        if (!ZipPattern().IsMatch(zipCode))
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "รหัสไปรษณีย์ต้องเป็นตัวเลข 5 หลัก", new { zipCode });
        return await db.Subdistricts.AsNoTracking()
            .Where(s => s.ZipCode == zipCode)
            .OrderBy(s => s.District.Province.NameTh).ThenBy(s => s.District.NameTh).ThenBy(s => s.NameTh)
            .Select(s => new ZipMatch(
                new GeoRef(s.Id, s.NameTh),
                new GeoRef(s.District.Id, s.District.NameTh),
                new GeoRef(s.District.Province.Id, s.District.Province.NameTh),
                s.ZipCode))
            .ToListAsync(ct);
    }

    // ใช้ [0-9] ไม่ใช้ \d เพราะ \d ยอมรับเลขไทย
    [GeneratedRegex("^[0-9]{5}$")]
    private static partial Regex ZipPattern();
}
```

- [ ] **Step 4: รัน test ให้ผ่าน**

Run: `dotnet test --filter FullyQualifiedName~GeoEndpointTests`
Expected: PASS ทั้งหมด (11 tests)

- [ ] **Step 5: รัน test ทั้งชุด**

Run: `dotnet test`
Expected: PASS ทั้งหมด 0 failed

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(api): Thai geo endpoints (provinces, districts, subdistricts, zip lookup)"
```

---

### Task 9: เชื่อม DB จริงและตรวจรันจริง

**ต้องมีรหัสผ่าน DB ที่ใช้ได้จากผู้ใช้ก่อน** (ณ วันที่เขียนแผน รหัสที่ให้มาล็อกอินไม่ผ่าน) ถ้ายังไม่มี ให้หยุดที่ task นี้และรายงาน

**Files:** ไม่มีไฟล์ใน repo เปลี่ยน (secret อยู่นอก repo)

- [ ] **Step 1: ทดสอบเชื่อมต่อ**

```bash
PGPASSWORD='<password>' psql -h 192.168.155.203 -U postgres -d postgres -Atc "select version()"
```

Expected: แสดงเวอร์ชัน PostgreSQL (ถ้าต่ำกว่า 13 ให้รายงานผู้ใช้ก่อนทำต่อ)

- [ ] **Step 2: ตั้ง user-secrets**

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" 'Host=192.168.155.203;Port=5432;Database=rundb;Username=postgres;Password=<password>' --project src/HuebRun.Api
git status --porcelain | grep -i secret || echo "no secrets in repo"
```

- [ ] **Step 3: สร้างฐานและรัน migration**

```bash
dotnet ef database update -p src/HuebRun.Infrastructure -s src/HuebRun.Api
```

Expected: `Done.` และ DB `rundb` ถูกสร้าง

- [ ] **Step 4: รันแอปและ smoke test**

```bash
dotnet run --project src/HuebRun.Api --urls http://localhost:5080
```

ในอีก terminal:

```bash
curl -s localhost:5080/health
curl -s localhost:5080/api/v1/events/hueb-run-2027
curl -s localhost:5080/api/v1/events/hueb-run-2027/distances
curl -s localhost:5080/api/v1/geo/zip/10200
curl -s -o /dev/null -w "%{http_code}\n" localhost:5080/openapi/v1.json
```

Expected: `Healthy`, event JSON ที่ `registrationOpen: true`, 3 ระยะ (early bird ถ้าวันนี้ก่อน 2026-11-30), รายการตำบล, `200`

- [ ] **Step 5: ปิดแอปและรายงานผล**

ไม่มี commit ใน task นี้
