# Order Registration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** นักวิ่งส่งใบสมัคร (เดี่ยว/กลุ่ม) ได้จริง พร้อมจองโควตา/เสื้อ/โค้ดแบบ atomic, ดูสถานะออเดอร์, ออเดอร์หมดเวลาคืนที่อัตโนมัติ และมี service ออกเลขบิบรอขั้นชำระเงิน

**Architecture:** กติการาคาเป็น pure class ใน Domain (`OrderPricing`) · งานเขียนทั้งหมดอยู่ใน service ของ Infrastructure (`OrderService`, `OrderPaymentService`, `OrderExpiryService`) ที่โยน `OrderRuleException` · Api มี controller บาง ๆ + `BackgroundService` เรียก expiry ทุกนาที · การจองใช้ `UPDATE ... WHERE ReservedCount + n <= Quota` ใน transaction เดียว

**Tech Stack:** .NET 10, EF Core 10 + Npgsql (ExecuteUpdate, sequence, advisory lock), xUnit, Testcontainers, FakeTimeProvider

**Spec:** `docs/superpowers/specs/2026-10-11-order-registration-design.md`

## Global Constraints

- ต่อยอด branch `feat/order-registration` (มีโครงจาก `docs/superpowers/plans/2026-10-11-api-foundation.md` แล้ว)
- Base path `/api/v1`; error body `{ code, message, details }`
- เวลา UTC; ราคาใช้ `decimal` 2 ตำแหน่ง; คำนวณราคาที่เซิร์ฟเวอร์เท่านั้น
- นักวิ่ง 1–10 คนต่อออเดอร์ (`OrderValidator.MaxRunners = 10`)
- `GET /orders/{id}` ใช้ GUID อย่างเดียว และห้ามส่ง `medicalNote`, ผู้ติดต่อฉุกเฉิน, `checkInToken`
- Infrastructure ห้ามอ้าง Api
- ต้องเปิด Docker ตอนรัน test
- **Deviation จาก spec §5 (ledger เป็น ruling ตอนลงมือ):** ก่อนเลือกเศษสตางค์ ให้ถือ `pg_advisory_xact_lock(eventId, totalCents)` จนจบ transaction — spec ใช้แค่ retry 3 ครั้ง ซึ่งจะล้มเมื่อมีออเดอร์ยอดเท่ากันพร้อมกันหลายใบ (วันเปิดรับสมัคร) retry ยังคงไว้เป็นด่านสำรอง

## Review Focus

1. ออเดอร์ยอดเท่ากันพร้อมกันหลายใบ (early bird ราคาเดียวกัน) → ทุกใบต้องสำเร็จ เศษสตางค์ไม่ซ้ำ ไม่มี `PAYMENT_SLOT_BUSY` — test ใน Task 5
2. โค้ดส่วนลดที่เหลือสิทธิ์ 1 ครั้งถูกแย่งพร้อมกัน → ได้ออเดอร์เดียว ที่เหลือ `409 PROMO_EXHAUSTED` — test ใน Task 5
3. ผู้ใช้จ่ายเงินตอนออเดอร์กำลังหมดเวลา (MarkPaid แข่งกับ expiry) → ฝั่งเดียวชนะ ยอดจองถูกต้อง — test ใน Task 7
4. นักวิ่งหลายคนเลือกระยะ/ไซซ์ซ้ำกันในออเดอร์เดียว → จองตามจำนวนรวมของแต่ละกลุ่ม — test ใน Task 4
5. โค้ดส่วนลดพิมพ์ตัวเล็ก/มีช่องว่าง (`"  early10 "`) → ใช้ได้ — test ใน Task 3

---

## File Structure

```
src/HuebRun.Domain/
  Entities.cs                         (+ Order.GetEffectiveStatus, EventDistance.LastBibNumber)
  OrderPricing.cs                     PromoRejection, PriceBreakdown, OrderPricing (pure)
src/HuebRun.Infrastructure/
  AppDbContext.cs                     (+ sequence order_no_seq)
  DependencyInjection.cs              (+ register order services)
  Migrations/*_OrderRegistration.cs
  Orders/OrderRuleException.cs        OrderRuleException + OrderErrors (รหัส error ทั้งหมด)
  Orders/OrderRequests.cs             request/result records
  Orders/OrderValidator.cs            validate คำขอสร้างออเดอร์ (pure) + OrderReferenceData
  Orders/OrderService.cs              ValidatePromoAsync, CreateOrderAsync
  Orders/OrderPaymentService.cs       MarkPaidAsync (ออกเลขบิบ)
  Orders/OrderExpiryService.cs        ExpireDueOrdersAsync
src/HuebRun.Api/
  Program.cs                          (+ worker toggle)
  appsettings.json                    (+ Jobs:OrderExpiry:Enabled)
  Errors/AppExceptionHandler.cs       (+ map OrderRuleException)
  Contracts/OrderContracts.cs
  Controllers/RegistrationController.cs   POST promo/validate, POST orders
  Controllers/OrdersController.cs         GET order, GET tickets
  Jobs/OrderExpiryWorker.cs
tests/HuebRun.Tests/
  Infrastructure/ApiFactory.cs        (+ jobsEnabled)
  Infrastructure/TestData.cs          (+ CreateRegistrationEventAsync)
  Infrastructure/OrderTestBase.cs
  Domain/OrderPricingTests.cs
  Data/SchemaTests.cs                 (+ sequence test)
  Api/ErrorHandlingTests.cs           (+ OrderRuleException)
  Api/PromoValidateTests.cs
  Api/CreateOrderTests.cs
  Api/CreateOrderConcurrencyTests.cs
  Data/OrderPaymentServiceTests.cs
  Api/OrderQueryTests.cs
  Data/OrderExpiryTests.cs
  Api/HostTests.cs                    (+ worker toggle tests)
```

---

### Task 1: Domain — pricing rules, effective status, bib counter

**Files:**
- Create: `src/HuebRun.Domain/OrderPricing.cs`
- Modify: `src/HuebRun.Domain/Entities.cs`
- Test: `tests/HuebRun.Tests/Domain/OrderPricingTests.cs`

**Interfaces:**
- Produces:
  - `enum PromoRejection { NotFound, Inactive, NotStarted, Expired, MinOrderNotMet, Exhausted }`
  - `record PriceBreakdown(decimal SubTotal, decimal DiscountAmount, decimal ShippingFee, decimal Total)`
  - `static decimal OrderPricing.SubTotal(IEnumerable<EventDistance> runnerDistances, DateTime utcNow)`
  - `static PromoRejection? OrderPricing.CheckPromo(PromoCode? promo, decimal subTotal, DateTime utcNow)` — `null` = ใช้ได้
  - `static decimal OrderPricing.Discount(PromoCode promo, decimal subTotal)`
  - `static PriceBreakdown OrderPricing.Calculate(decimal subTotal, decimal discount, decimal shippingFee)`
  - `OrderStatus Order.GetEffectiveStatus(DateTime utcNow)`
  - `int EventDistance.LastBibNumber { get; set; }`

- [ ] **Step 1: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Domain/OrderPricingTests.cs`:

```csharp
using HuebRun.Domain;

namespace HuebRun.Tests.Domain;

public class OrderPricingTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

    private static PromoCode Promo(Action<PromoCode>? configure = null)
    {
        var p = new PromoCode { Code = "EARLY10", Type = DiscountType.Percent, Value = 10, IsActive = true };
        configure?.Invoke(p);
        return p;
    }

    [Fact]
    public void Subtotal_sums_effective_price_per_runner()
    {
        var fiveK = new EventDistance { Price = 500, EarlyBirdPrice = 400, EarlyBirdEndsAt = Now.AddDays(1) };
        var tenK = new EventDistance { Price = 700 };
        Assert.Equal(1500m, OrderPricing.SubTotal([fiveK, fiveK, tenK], Now));
    }

    [Fact]
    public void Percent_discount_rounds_half_away_from_zero()
    {
        // 333.33 × 15% = 49.9995 → 50.00
        Assert.Equal(50.00m, OrderPricing.Discount(Promo(p => p.Value = 15), 333.33m));
    }

    [Fact]
    public void Fixed_discount_never_exceeds_subtotal()
    {
        Assert.Equal(300m, OrderPricing.Discount(Promo(p => { p.Type = DiscountType.Fixed; p.Value = 1000; }), 300m));
        Assert.Equal(100m, OrderPricing.Discount(Promo(p => { p.Type = DiscountType.Fixed; p.Value = 100; }), 300m));
    }

    [Fact]
    public void Calculate_total_is_subtotal_minus_discount_plus_shipping()
    {
        Assert.Equal(new PriceBreakdown(1100m, 110m, 60m, 1050m), OrderPricing.Calculate(1100m, 110m, 60m));
    }

    [Fact]
    public void Valid_promo_has_no_rejection() =>
        Assert.Null(OrderPricing.CheckPromo(Promo(p => { p.StartsAt = Now; p.EndsAt = Now.AddDays(1); p.MinOrderAmount = 500; p.MaxUses = 2; p.UsedCount = 1; }), 500m, Now));

    [Fact]
    public void Promo_rejections()
    {
        Assert.Equal(PromoRejection.NotFound, OrderPricing.CheckPromo(null, 500m, Now));
        Assert.Equal(PromoRejection.Inactive, OrderPricing.CheckPromo(Promo(p => p.IsActive = false), 500m, Now));
        Assert.Equal(PromoRejection.NotStarted, OrderPricing.CheckPromo(Promo(p => p.StartsAt = Now.AddSeconds(1)), 500m, Now));
        Assert.Equal(PromoRejection.Expired, OrderPricing.CheckPromo(Promo(p => p.EndsAt = Now), 500m, Now));
        Assert.Equal(PromoRejection.MinOrderNotMet, OrderPricing.CheckPromo(Promo(p => p.MinOrderAmount = 500.01m), 500m, Now));
        Assert.Equal(PromoRejection.Exhausted, OrderPricing.CheckPromo(Promo(p => { p.MaxUses = 3; p.UsedCount = 3; }), 500m, Now));
    }

    [Fact]
    public void Pending_order_past_expiry_is_effectively_expired()
    {
        var order = new Order { Status = OrderStatus.Pending, ExpiresAt = Now };
        Assert.Equal(OrderStatus.Pending, order.GetEffectiveStatus(Now.AddSeconds(-1)));
        Assert.Equal(OrderStatus.Expired, order.GetEffectiveStatus(Now));
    }

    [Fact]
    public void Paid_order_never_becomes_expired()
    {
        var order = new Order { Status = OrderStatus.Paid, ExpiresAt = Now };
        Assert.Equal(OrderStatus.Paid, order.GetEffectiveStatus(Now.AddDays(1)));
    }
}
```

- [ ] **Step 2: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter FullyQualifiedName~OrderPricingTests`
Expected: compile error `The name 'OrderPricing' does not exist`

- [ ] **Step 3: สร้าง `src/HuebRun.Domain/OrderPricing.cs`**

```csharp
namespace HuebRun.Domain;

public enum PromoRejection { NotFound, Inactive, NotStarted, Expired, MinOrderNotMet, Exhausted }

public sealed record PriceBreakdown(decimal SubTotal, decimal DiscountAmount, decimal ShippingFee, decimal Total);

/// กติกาคิดราคาออเดอร์ (pure) ใช้ร่วมกันระหว่างตรวจโค้ดและสร้างออเดอร์
public static class OrderPricing
{
    public static decimal SubTotal(IEnumerable<EventDistance> runnerDistances, DateTime utcNow) =>
        runnerDistances.Sum(d => d.GetEffectivePrice(utcNow));

    /// null = ใช้ได้
    public static PromoRejection? CheckPromo(PromoCode? promo, decimal subTotal, DateTime utcNow)
    {
        if (promo is null) return PromoRejection.NotFound;
        if (!promo.IsActive) return PromoRejection.Inactive;
        if (promo.StartsAt is { } starts && utcNow < starts) return PromoRejection.NotStarted;
        if (promo.EndsAt is { } ends && utcNow >= ends) return PromoRejection.Expired;
        if (promo.MinOrderAmount is { } min && subTotal < min) return PromoRejection.MinOrderNotMet;
        if (promo.MaxUses is { } max && promo.UsedCount >= max) return PromoRejection.Exhausted;
        return null;
    }

    public static decimal Discount(PromoCode promo, decimal subTotal)
    {
        var raw = promo.Type == DiscountType.Percent
            ? Math.Round(subTotal * promo.Value / 100m, 2, MidpointRounding.AwayFromZero)
            : promo.Value;
        return Math.Min(raw, subTotal);
    }

    public static PriceBreakdown Calculate(decimal subTotal, decimal discount, decimal shippingFee) =>
        new(subTotal, discount, shippingFee, subTotal - discount + shippingFee);
}
```

- [ ] **Step 4: แก้ `src/HuebRun.Domain/Entities.cs`**

ใน `class EventDistance` ต่อจากบรรทัด `public int ReservedCount { get; set; }             // รวมที่จองไว้ + จ่ายแล้ว (อัปเดตแบบ atomic)`:

```csharp
    public int LastBibNumber { get; set; }             // ตัวนับเลขบิบล่าสุดของระยะนี้ (เพิ่มแบบ atomic ตอนจ่ายเงิน)
```

ใน `class Order` ต่อจากบรรทัด `public List<Payment> Payments { get; set; } = new();`:

```csharp

    // ออเดอร์ที่เลยเวลาแต่ job ยังไม่ได้เปลี่ยนสถานะ ให้ถือว่าหมดเวลาแล้ว
    public OrderStatus GetEffectiveStatus(DateTime utcNow) =>
        Status == OrderStatus.Pending && utcNow >= ExpiresAt ? OrderStatus.Expired : Status;
```

- [ ] **Step 5: รัน test ให้ผ่าน**

Run: `dotnet test --filter FullyQualifiedName~OrderPricingTests`
Expected: PASS 8 tests

- [ ] **Step 6: Commit**

```bash
git add src/HuebRun.Domain tests/HuebRun.Tests/Domain/OrderPricingTests.cs
git commit -m "feat(domain): order pricing rules, effective status, bib counter"
```

---

### Task 2: Schema migration + order error type

**Files:**
- Modify: `src/HuebRun.Infrastructure/AppDbContext.cs`
- Create: `src/HuebRun.Infrastructure/Migrations/*_OrderRegistration.cs` (generated)
- Create: `src/HuebRun.Infrastructure/Orders/OrderRuleException.cs`
- Modify: `src/HuebRun.Api/Errors/AppExceptionHandler.cs`
- Test: `tests/HuebRun.Tests/Data/SchemaTests.cs`, `tests/HuebRun.Tests/Api/ErrorHandlingTests.cs`

**Interfaces:**
- Consumes: `EventDistance.LastBibNumber`, `PromoRejection` (Task 1)
- Produces:
  - Postgres sequence `order_no_seq`; คอลัมน์ `EventDistances.LastBibNumber`
  - `OrderRuleException(int statusCode, string code, string message, object? details = null)` with `StatusCode`, `Code`, `Details`
  - `static class OrderErrors`: `EventNotFound(string slug)`, `RegistrationClosed()`, `Validation(IReadOnlyDictionary<string,string[]> errors)`, `Promo(PromoRejection reason)`, `QuotaFull(int distanceId, string name)`, `ShirtOutOfStock(int shirtSizeId, string size)`, `PaymentSlotBusy()`, `OrderNotFound(Guid orderId)`, `OrderNotPaid(OrderStatus status)` — ทุกตัวคืน `OrderRuleException`
  - `const string OrderErrors.PayableIndexName = "IX_Orders_EventId_PayableAmount"`

- [ ] **Step 1: เขียน test ที่ล้มเหลว**

เพิ่มใน `tests/HuebRun.Tests/Data/SchemaTests.cs` (ภายในคลาส `SchemaTests`):

```csharp
    [Fact]
    public async Task Order_number_sequence_exists_and_increments()
    {
        await using var db = pg.CreateDbContext();
        var a = await db.Database.SqlQuery<long>($"SELECT nextval('order_no_seq') AS \"Value\"").SingleAsync();
        var b = await db.Database.SqlQuery<long>($"SELECT nextval('order_no_seq') AS \"Value\"").SingleAsync();
        Assert.Equal(a + 1, b);
    }

    [Fact]
    public async Task Distance_bib_counter_defaults_to_zero()
    {
        await using var db = pg.CreateDbContext();
        var ev = await TestData.CreateEventAsync(db, e =>
            e.Distances.Add(new EventDistance { Name = "3K", DistanceKm = 3, BibPrefix = "A", Quota = 10, Price = 100 }));
        await using var check = pg.CreateDbContext();
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == ev.Distances[0].Id)).LastBibNumber);
    }
```

เพิ่มใน `tests/HuebRun.Tests/Api/ErrorHandlingTests.cs` (ภายในคลาส) และ `using HuebRun.Domain;` + `using HuebRun.Infrastructure.Orders;` บนสุด:

```csharp
    [Fact]
    public async Task Order_rule_exception_maps_to_its_status_and_code()
    {
        var (status, body, _) = await Handle(OrderErrors.QuotaFull(7, "5K"));
        Assert.Equal(409, status);
        Assert.Equal("QUOTA_FULL", body.GetProperty("code").GetString());
        Assert.Equal(7, body.GetProperty("details").GetProperty("distanceId").GetInt32());
    }

    [Fact]
    public async Task Promo_rejection_reason_is_screaming_snake_case()
    {
        var (status, body, _) = await Handle(OrderErrors.Promo(PromoRejection.MinOrderNotMet));
        Assert.Equal(422, status);
        Assert.Equal("PROMO_INVALID", body.GetProperty("code").GetString());
        Assert.Equal("MIN_ORDER_NOT_MET", body.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Exhausted_promo_is_conflict()
    {
        var (status, body, _) = await Handle(OrderErrors.Promo(PromoRejection.Exhausted));
        Assert.Equal(409, status);
        Assert.Equal("PROMO_EXHAUSTED", body.GetProperty("code").GetString());
    }
```

- [ ] **Step 2: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter "FullyQualifiedName~SchemaTests|FullyQualifiedName~ErrorHandlingTests"`
Expected: compile error `The type or namespace name 'Orders' does not exist in the namespace 'HuebRun.Infrastructure'`

- [ ] **Step 3: สร้าง `src/HuebRun.Infrastructure/Orders/OrderRuleException.cs`**

```csharp
using HuebRun.Domain;

namespace HuebRun.Infrastructure.Orders;

/// ข้อผิดพลาดตามกติกาธุรกิจของการสมัคร (ข้อความปลอดภัยที่จะแสดงผู้ใช้) Api แปลงเป็น error body กลาง
public sealed class OrderRuleException(int statusCode, string code, string message, object? details = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

public static class OrderErrors
{
    public const string PayableIndexName = "IX_Orders_EventId_PayableAmount";

    public static OrderRuleException EventNotFound(string slug) =>
        new(404, "EVENT_NOT_FOUND", "ไม่พบงานนี้", new { slug });

    public static OrderRuleException RegistrationClosed() =>
        new(400, "REGISTRATION_CLOSED", "ไม่อยู่ในช่วงเปิดรับสมัคร");

    public static OrderRuleException Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(400, "VALIDATION_ERROR", "ข้อมูลไม่ถูกต้อง", errors);

    public static OrderRuleException Promo(PromoRejection reason) => reason == PromoRejection.Exhausted
        ? new(409, "PROMO_EXHAUSTED", "โค้ดส่วนลดถูกใช้ครบจำนวนแล้ว")
        : new(422, "PROMO_INVALID", "ใช้โค้ดส่วนลดนี้ไม่ได้", new { reason = ReasonCode(reason) });

    public static OrderRuleException QuotaFull(int distanceId, string name) =>
        new(409, "QUOTA_FULL", $"ระยะ {name} เต็มแล้ว", new { distanceId, name });

    public static OrderRuleException ShirtOutOfStock(int shirtSizeId, string size) =>
        new(409, "SHIRT_OUT_OF_STOCK", $"เสื้อไซซ์ {size} หมดแล้ว", new { shirtSizeId, size });

    public static OrderRuleException PaymentSlotBusy() =>
        new(409, "PAYMENT_SLOT_BUSY", "มีผู้สมัครยอดเดียวกันจำนวนมาก กรุณาลองใหม่อีกครั้ง");

    public static OrderRuleException OrderNotFound(Guid orderId) =>
        new(404, "ORDER_NOT_FOUND", "ไม่พบใบสมัคร", new { orderId });

    public static OrderRuleException OrderNotPaid(OrderStatus status) =>
        new(409, "ORDER_NOT_PAID", "ใบสมัครยังไม่ได้ชำระเงิน", new { status = status.ToString() });

    private static string ReasonCode(PromoRejection reason) => reason switch
    {
        PromoRejection.NotFound => "NOT_FOUND",
        PromoRejection.Inactive => "INACTIVE",
        PromoRejection.NotStarted => "NOT_STARTED",
        PromoRejection.Expired => "EXPIRED",
        PromoRejection.MinOrderNotMet => "MIN_ORDER_NOT_MET",
        _ => "EXHAUSTED",
    };
}
```

- [ ] **Step 4: map ใน `src/HuebRun.Api/Errors/AppExceptionHandler.cs`**

แทนที่บล็อก `if (exception is AppException app) { ... } else { ... }` ด้วย:

```csharp
        switch (exception)
        {
            case AppException app:
                status = app.StatusCode;
                body = new ErrorResponse(app.Code, app.Message, app.Details);
                break;
            case OrderRuleException rule:
                status = rule.StatusCode;
                body = new ErrorResponse(rule.Code, rule.Message, rule.Details);
                break;
            default:
                logger.LogError(exception, "Unhandled exception");
                status = StatusCodes.Status500InternalServerError;
                body = new ErrorResponse("INTERNAL_ERROR", "เกิดข้อผิดพลาดภายในระบบ", null);
                break;
        }
```

และเพิ่ม `using HuebRun.Infrastructure.Orders;` บนสุด

- [ ] **Step 5: เพิ่ม sequence ใน `src/HuebRun.Infrastructure/AppDbContext.cs`**

ต่อจากบรรทัด `base.OnModelCreating(b);   // ตาราง Identity (AspNetUsers ฯลฯ)`:

```csharp

        // เลขออเดอร์อ่านง่าย (HR27-000123) ไม่ชนกันแม้สร้างพร้อมกัน
        b.HasSequence<long>("order_no_seq");
```

- [ ] **Step 6: สร้าง migration และตรวจชื่อ index**

```bash
dotnet ef migrations add OrderRegistration -p src/HuebRun.Infrastructure -s src/HuebRun.Api -o Migrations
grep -n "order_no_seq\|LastBibNumber" src/HuebRun.Infrastructure/Migrations/*_OrderRegistration.cs
grep -n "IX_Orders_EventId_PayableAmount" src/HuebRun.Infrastructure/Migrations/*_InitialCreate.cs
```

Expected: migration มี `CreateSequence` `order_no_seq` และ `AddColumn` `LastBibNumber` (default 0); ชื่อ index ตรง `OrderErrors.PayableIndexName` (ถ้าไม่ตรงให้แก้ค่าคงที่ให้ตรงไฟล์ migration)

- [ ] **Step 7: รัน test ให้ผ่าน**

Run: `dotnet test --filter "FullyQualifiedName~SchemaTests|FullyQualifiedName~ErrorHandlingTests"`
Expected: PASS (SchemaTests 5, ErrorHandlingTests 5)

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(infra): order number sequence, bib counter column, order error codes"
```

---

### Task 3: Promo validate endpoint + order test scaffolding

**Files:**
- Create: `src/HuebRun.Infrastructure/Orders/OrderRequests.cs`, `src/HuebRun.Infrastructure/Orders/OrderService.cs`
- Modify: `src/HuebRun.Infrastructure/DependencyInjection.cs`
- Create: `src/HuebRun.Api/Controllers/RegistrationController.cs`
- Modify: `tests/HuebRun.Tests/Infrastructure/TestData.cs`
- Create: `tests/HuebRun.Tests/Infrastructure/OrderTestBase.cs`
- Test: `tests/HuebRun.Tests/Api/PromoValidateTests.cs`

**Interfaces:**
- Consumes: `OrderPricing`, `OrderErrors` (Tasks 1–2)
- Produces:
  - records (namespace `HuebRun.Infrastructure.Orders`): `CreateOrderRequest(ContactInput? Contact, List<RunnerInput>? Runners, int? ShippingOptionId, AddressInput? ShippingAddress, string? PromoCode, bool AcceptedPdpa)`, `ContactInput(string? Name, string? Phone, string? Email)`, `RunnerInput(int? DistanceId, int? ShirtSizeId, int? TitleId, string? FirstName, string? LastName, Gender? Gender, DateOnly? BirthDate, int? NationalityId, int? BloodTypeId, string? Phone, string? Email, string? TeamName, EmergencyContactInput? EmergencyContact, string? MedicalNote)`, `EmergencyContactInput(int? RelationId, string? Name, string? Phone)`, `AddressInput(string? Name, string? Phone, string? Line1, int? SubdistrictId, string? PostalCode)`, `ValidatePromoRequest(string? Code, List<int>? DistanceIds)`, `PromoQuote(string Code, decimal SubTotal, decimal DiscountAmount)`, `CreatedOrder(Guid OrderId, string OrderNo, decimal Total, decimal PayableAmount, DateTime ExpiresAt)`
  - `OrderService(AppDbContext db, TimeProvider time)` with `Task<PromoQuote> ValidatePromoAsync(string slug, ValidatePromoRequest request, CancellationToken ct)`
  - `POST /api/v1/events/{slug}/promo/validate`
  - `TestData.CreateRegistrationEventAsync(AppDbContext db, Action<Event>? configure = null)` — 5K (B, quota 100, 500 / early 400 ถึง 2026-11-30), 10K (C, quota 100, 700), เสื้อ M/L stock 100, Pickup 0 / Post (EMS) 60
  - `OrderTestBase` (ดูโค้ด)

- [ ] **Step 1: สร้าง request records**

`src/HuebRun.Infrastructure/Orders/OrderRequests.cs`:

```csharp
using HuebRun.Domain;

namespace HuebRun.Infrastructure.Orders;

// ทุกฟิลด์ nullable เพื่อให้ validate เองและตอบ error รูปแบบเดียวกันทุกฟิลด์
public sealed record CreateOrderRequest(
    ContactInput? Contact,
    List<RunnerInput>? Runners,
    int? ShippingOptionId,
    AddressInput? ShippingAddress,
    string? PromoCode,
    bool AcceptedPdpa);

public sealed record ContactInput(string? Name, string? Phone, string? Email);

public sealed record RunnerInput(
    int? DistanceId, int? ShirtSizeId,
    int? TitleId, string? FirstName, string? LastName, Gender? Gender,
    DateOnly? BirthDate, int? NationalityId, int? BloodTypeId,
    string? Phone, string? Email, string? TeamName,
    EmergencyContactInput? EmergencyContact, string? MedicalNote);

public sealed record EmergencyContactInput(int? RelationId, string? Name, string? Phone);

public sealed record AddressInput(string? Name, string? Phone, string? Line1, int? SubdistrictId, string? PostalCode);

public sealed record ValidatePromoRequest(string? Code, List<int>? DistanceIds);

public sealed record PromoQuote(string Code, decimal SubTotal, decimal DiscountAmount);

public sealed record CreatedOrder(Guid OrderId, string OrderNo, decimal Total, decimal PayableAmount, DateTime ExpiresAt);
```

- [ ] **Step 2: เพิ่ม test data และ base class**

เพิ่มใน `tests/HuebRun.Tests/Infrastructure/TestData.cs` (ภายในคลาส `TestData`):

```csharp
    // งานพร้อมรับสมัคร: 5K early bird 400 (ปกติ 500) ถึง 2026-11-30, 10K 700, เสื้อ M/L, รับเอง/EMS 60
    public static Task<Event> CreateRegistrationEventAsync(AppDbContext db, Action<Event>? configure = null) =>
        CreateEventAsync(db, e =>
        {
            e.Distances.Add(new EventDistance { Name = "5K", DistanceKm = 5, BibPrefix = "B", Quota = 100,
                Price = 500, EarlyBirdPrice = 400, EarlyBirdEndsAt = Utc(2026, 11, 30) });
            e.Distances.Add(new EventDistance { Name = "10K", DistanceKm = 10, BibPrefix = "C", Quota = 100, Price = 700 });
            e.ShirtSizes.Add(new EventShirtSize { Size = "M", Stock = 100 });
            e.ShirtSizes.Add(new EventShirtSize { Size = "L", Stock = 100 });
            e.ShippingOptions.Add(new ShippingOption { Type = ShippingType.Pickup, Name = "รับเอง", Fee = 0 });
            e.ShippingOptions.Add(new ShippingOption { Type = ShippingType.Post, Name = "EMS", Fee = 60 });
            configure?.Invoke(e);
        });
```

`tests/HuebRun.Tests/Infrastructure/OrderTestBase.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Infrastructure;

/// ฐานของ test การสมัคร: สร้างงานใหม่ต่อ test (slug ไม่ซ้ำ) และ helper สร้างคำขอ เวลา = 2026-11-15T00:00Z
public abstract class OrderTestBase(PostgresFixture pg) : IAsyncLifetime
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected PostgresFixture Pg { get; } = pg;
    protected ApiFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;
    protected Event Ev { get; private set; } = null!;
    protected EventDistance FiveK => Ev.Distances.Single(d => d.Name == "5K");
    protected EventDistance TenK => Ev.Distances.Single(d => d.Name == "10K");
    protected EventShirtSize M => Ev.ShirtSizes.Single(s => s.Size == "M");
    protected EventShirtSize L => Ev.ShirtSizes.Single(s => s.Size == "L");
    protected ShippingOption Pickup => Ev.ShippingOptions.Single(s => s.Type == ShippingType.Pickup);
    protected ShippingOption Post => Ev.ShippingOptions.Single(s => s.Type == ShippingType.Post);
    protected int ThaiId { get; private set; }

    protected virtual void ConfigureEvent(Event e) { }

    public async Task InitializeAsync()
    {
        Factory = new ApiFactory(Pg.ConnectionString);
        Client = Factory.CreateClient();   // เริ่มแอป → seed lookups/geo
        await using var db = Pg.CreateDbContext();
        Ev = await TestData.CreateRegistrationEventAsync(db, ConfigureEvent);
        ThaiId = await db.Nationalities.Where(n => n.NameEn == "Thai").Select(n => n.Id).SingleAsync();
    }

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }

    protected RunnerInput Runner(EventDistance? distance = null, EventShirtSize? shirt = null, string firstName = "สมชาย") => new(
        (distance ?? FiveK).Id, (shirt ?? M).Id,
        TitleId: null, FirstName: firstName, LastName: "ใจดี", Gender: Gender.Male,
        BirthDate: new DateOnly(1990, 1, 1), NationalityId: ThaiId, BloodTypeId: null,
        Phone: null, Email: null, TeamName: null,
        EmergencyContact: new EmergencyContactInput(null, "สมหญิง ใจดี", "0812345678"),
        MedicalNote: "แพ้ยาเพนิซิลลิน");

    protected CreateOrderRequest Request(params RunnerInput[] runners) => new(
        new ContactInput("สมชาย ใจดี", "081-234-5678", "somchai@example.com"),
        runners.Length == 0 ? [Runner()] : [.. runners],
        Pickup.Id, ShippingAddress: null, PromoCode: null, AcceptedPdpa: true);

    protected Task<HttpResponseMessage> PostOrder(CreateOrderRequest request) =>
        Client.PostAsJsonAsync($"/api/v1/events/{Ev.Slug}/orders", request, Json);

    protected async Task<CreatedOrder> CreateOrderOk(CreateOrderRequest? request = null)
    {
        var res = await PostOrder(request ?? Request());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<CreatedOrder>(Json))!;
    }

    protected async Task<PromoCode> AddPromoAsync(Action<PromoCode>? configure = null)
    {
        await using var db = Pg.CreateDbContext();
        var promo = new PromoCode { EventId = Ev.Id, Code = "EARLY10", Type = DiscountType.Percent, Value = 10, IsActive = true };
        configure?.Invoke(promo);
        db.PromoCodes.Add(promo);
        await db.SaveChangesAsync();
        return promo;
    }

    protected static async Task<JsonElement> AssertError(HttpResponseMessage res, HttpStatusCode status, string code)
    {
        Assert.Equal(status, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }
}
```

- [ ] **Step 3: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Api/PromoValidateTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class PromoValidateTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private Task<HttpResponseMessage> Validate(string? code, params int[] distanceIds) =>
        Client.PostAsJsonAsync($"/api/v1/events/{Ev.Slug}/promo/validate", new ValidatePromoRequest(code, [.. distanceIds]), Json);

    [Fact]
    public async Task Valid_code_returns_discount_for_server_computed_subtotal()
    {
        await AddPromoAsync();
        var res = await Validate("EARLY10", FiveK.Id, FiveK.Id, TenK.Id);   // 400 + 400 + 700
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var quote = (await res.Content.ReadFromJsonAsync<PromoQuote>(Json))!;
        Assert.Equal(new PromoQuote("EARLY10", 1500m, 150m), quote);
    }

    [Fact]
    public async Task Code_is_trimmed_and_case_insensitive()
    {
        await AddPromoAsync();
        var quote = (await (await Validate("  early10 ", FiveK.Id)).Content.ReadFromJsonAsync<PromoQuote>(Json))!;
        Assert.Equal("EARLY10", quote.Code);
        Assert.Equal(40m, quote.DiscountAmount);
    }

    [Fact]
    public async Task Validate_does_not_consume_a_use()
    {
        var promo = await AddPromoAsync(p => p.MaxUses = 1);
        await Validate("EARLY10", FiveK.Id);
        await using var db = Pg.CreateDbContext();
        Assert.Equal(0, (await db.PromoCodes.SingleAsync(p => p.Id == promo.Id)).UsedCount);
    }

    [Fact]
    public async Task Unknown_code_is_invalid_not_found()
    {
        var body = await AssertError(await Validate("NOPE", FiveK.Id), HttpStatusCode.UnprocessableEntity, "PROMO_INVALID");
        Assert.Equal("NOT_FOUND", body.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Code_from_another_event_is_not_found()
    {
        await using (var db = Pg.CreateDbContext())
        {
            var other = await TestData.CreateEventAsync(db);
            db.PromoCodes.Add(new PromoCode { EventId = other.Id, Code = "OTHER", Type = DiscountType.Fixed, Value = 50, IsActive = true });
            await db.SaveChangesAsync();
        }
        var body = await AssertError(await Validate("OTHER", FiveK.Id), HttpStatusCode.UnprocessableEntity, "PROMO_INVALID");
        Assert.Equal("NOT_FOUND", body.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Min_order_not_met()
    {
        await AddPromoAsync(p => p.MinOrderAmount = 1000);
        var body = await AssertError(await Validate("EARLY10", FiveK.Id), HttpStatusCode.UnprocessableEntity, "PROMO_INVALID");
        Assert.Equal("MIN_ORDER_NOT_MET", body.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Exhausted_code_is_conflict()
    {
        await AddPromoAsync(p => { p.MaxUses = 2; p.UsedCount = 2; });
        await AssertError(await Validate("EARLY10", FiveK.Id), HttpStatusCode.Conflict, "PROMO_EXHAUSTED");
    }

    [Fact]
    public async Task Distance_of_another_event_is_validation_error()
    {
        await AddPromoAsync();
        int otherDistanceId;
        await using (var db = Pg.CreateDbContext())
            otherDistanceId = (await TestData.CreateRegistrationEventAsync(db)).Distances[0].Id;
        var body = await AssertError(await Validate("EARLY10", otherDistanceId), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.True(body.GetProperty("details").TryGetProperty("distanceIds", out _));
    }

    [Fact]
    public async Task Empty_code_is_validation_error()
    {
        await AssertError(await Validate("  ", FiveK.Id), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
    }

    [Fact]
    public async Task Unknown_event_is_not_found()
    {
        var res = await Client.PostAsJsonAsync("/api/v1/events/no-such-event/promo/validate",
            new ValidatePromoRequest("EARLY10", [1]), Json);
        await AssertError(res, HttpStatusCode.NotFound, "EVENT_NOT_FOUND");
    }
}
```

- [ ] **Step 4: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter FullyQualifiedName~PromoValidateTests`
Expected: FAIL — `Expected: OK, Actual: NotFound` / `Expected: UnprocessableEntity, Actual: NotFound` (ยังไม่มี endpoint)

- [ ] **Step 5: สร้าง `src/HuebRun.Infrastructure/Orders/OrderService.cs` (ส่วน promo)**

```csharp
using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Orders;

public sealed class OrderService(AppDbContext db, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// ตรวจโค้ดส่วนลดกับยอดที่เซิร์ฟเวอร์คำนวณเอง (ยังไม่นับการใช้)
    public async Task<PromoQuote> ValidatePromoAsync(string slug, ValidatePromoRequest request, CancellationToken ct)
    {
        var ev = await LoadPublishedEventAsync(slug, ct);
        var now = Now;
        var code = request.Code?.Trim() ?? "";
        var ids = request.DistanceIds ?? [];
        var distances = await db.EventDistances.AsNoTracking()
            .Where(d => d.EventId == ev.Id)
            .ToDictionaryAsync(d => d.Id, ct);

        var errors = new Dictionary<string, string[]>();
        if (code.Length == 0) errors["code"] = ["กรุณากรอกโค้ดส่วนลด"];
        if (ids.Count is < 1 or > OrderValidator.MaxRunners)
            errors["distanceIds"] = [$"ต้องมี 1–{OrderValidator.MaxRunners} รายการ"];
        else if (ids.Any(id => !distances.ContainsKey(id)))
            errors["distanceIds"] = ["มีระยะที่ไม่ใช่ของงานนี้"];
        if (errors.Count > 0) throw OrderErrors.Validation(errors);

        var subTotal = OrderPricing.SubTotal(ids.Select(id => distances[id]), now);
        var promo = await FindPromoAsync(ev.Id, code, ct);
        if (OrderPricing.CheckPromo(promo, subTotal, now) is { } rejection) throw OrderErrors.Promo(rejection);
        return new PromoQuote(promo!.Code, subTotal, OrderPricing.Discount(promo, subTotal));
    }

    private async Task<Event> LoadPublishedEventAsync(string slug, CancellationToken ct)
    {
        var normalized = slug.ToLowerInvariant();
        return await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Slug == normalized && e.IsPublished, ct)
            ?? throw OrderErrors.EventNotFound(slug);
    }

    private Task<PromoCode?> FindPromoAsync(int eventId, string code, CancellationToken ct)
    {
        var upper = code.ToUpperInvariant();
        return db.PromoCodes.AsNoTracking().FirstOrDefaultAsync(p => p.EventId == eventId && p.Code.ToUpper() == upper, ct);
    }
}
```

และสร้างไฟล์ `src/HuebRun.Infrastructure/Orders/OrderValidator.cs` ชั่วคราวเฉพาะค่าคงที่ (Task 4 เติมส่วนที่เหลือ):

```csharp
namespace HuebRun.Infrastructure.Orders;

public static class OrderValidator
{
    public const int MaxRunners = 10;
}
```

- [ ] **Step 6: ลงทะเบียน service และสร้าง controller**

ใน `src/HuebRun.Infrastructure/DependencyInjection.cs` เพิ่ม `using HuebRun.Infrastructure.Orders;` และก่อน `return services;`:

```csharp
        services.AddScoped<OrderService>();
```

`src/HuebRun.Api/Controllers/RegistrationController.cs`:

```csharp
using HuebRun.Infrastructure.Orders;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/events/{slug}")]
public sealed class RegistrationController(OrderService orders) : ControllerBase
{
    [HttpPost("promo/validate")]
    public Task<PromoQuote> ValidatePromo(string slug, ValidatePromoRequest request, CancellationToken ct) =>
        orders.ValidatePromoAsync(slug, request, ct);
}
```

- [ ] **Step 7: รัน test ให้ผ่าน**

Run: `dotnet test --filter FullyQualifiedName~PromoValidateTests`
Expected: PASS 10 tests

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(api): promo code validation endpoint"
```

---

### Task 4: Create order — validation, pricing, atomic reservation

**Files:**
- Modify: `src/HuebRun.Infrastructure/Orders/OrderValidator.cs`, `src/HuebRun.Infrastructure/Orders/OrderService.cs`
- Modify: `src/HuebRun.Api/Controllers/RegistrationController.cs`
- Test: `tests/HuebRun.Tests/Api/CreateOrderTests.cs`

**Interfaces:**
- Consumes: Task 1–3
- Produces:
  - `OrderReferenceData` (Distances, ShirtSizes, ShippingOptions dictionaries; TitleIds, NationalityIds, BloodTypeIds, RelationIds sets; `Subdistrict? ShipSubdistrict` พร้อม District.Province)
  - `static Dictionary<string,string[]> OrderValidator.Validate(CreateOrderRequest request, OrderReferenceData data, DateTime utcNow)`
  - `static string? OrderValidator.NormalizePhone(string? raw)`
  - `Task<CreatedOrder> OrderService.CreateOrderAsync(string slug, CreateOrderRequest request, CancellationToken ct)`
  - `POST /api/v1/events/{slug}/orders` → `201` + `Location: /api/v1/orders/{orderId}`

- [ ] **Step 1: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Api/CreateOrderTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class CreateOrderTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private const int PhraBoromMahaRatchawang = 100101;   // ตำบลใน seed geo รหัส 10200

    private AddressInput Address(string postalCode = "10200") =>
        new("สมชาย ใจดี", "0812345678", "99/1 ถนนหน้าพระลาน", PhraBoromMahaRatchawang, postalCode);

    [Fact]
    public async Task Creates_group_order_with_server_side_pricing_and_reservations()
    {
        var promo = await AddPromoAsync();
        var request = Request(Runner(FiveK, M, "เอ"), Runner(TenK, M, "บี")) with
        {
            ShippingOptionId = Post.Id,
            ShippingAddress = Address(),
            PromoCode = "early10",
        };

        var res = await PostOrder(request);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = (await res.Content.ReadFromJsonAsync<CreatedOrder>(Json))!;
        Assert.Equal($"/api/v1/orders/{created.OrderId}", res.Headers.Location!.OriginalString);
        Assert.Matches(new Regex(@"^HR27-\d{6}$"), created.OrderNo);
        Assert.Equal(1050m, created.Total);                     // (400 + 700) − 10% + 60
        Assert.Equal(1050.01m, created.PayableAmount);
        Assert.Equal(new DateTime(2026, 11, 15, 0, 30, 0, DateTimeKind.Utc), created.ExpiresAt);

        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.Include(o => o.Registrations).SingleAsync(o => o.Id == created.OrderId);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal((1100m, 110m, 60m), (order.SubTotal, order.DiscountAmount, order.ShippingFee));
        Assert.Equal("0812345678", order.Phone);
        Assert.Equal(("กรุงเทพมหานคร", "เขตพระนคร", "พระบรมมหาราชวัง", "10200"),
            (order.ShipProvinceText, order.ShipDistrictText, order.ShipSubdistrictText, order.ShipPostalCode));
        Assert.Equal([400m, 700m], order.Registrations.OrderBy(r => r.Id).Select(r => r.PricePaid));
        Assert.All(order.Registrations, r => Assert.True(r.CheckInToken.Length >= 40));
        Assert.Equal(2, order.Registrations.Select(r => r.CheckInToken).Distinct().Count());

        Assert.Equal(1, (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
        Assert.Equal(1, (await db.EventDistances.SingleAsync(d => d.Id == TenK.Id)).ReservedCount);
        Assert.Equal(2, (await db.EventShirtSizes.SingleAsync(s => s.Id == M.Id)).ReservedCount);
        Assert.Equal(1, (await db.PromoCodes.SingleAsync(p => p.Id == promo.Id)).UsedCount);
    }

    [Fact]
    public async Task Pickup_order_has_no_shipping_snapshot()
    {
        var created = await CreateOrderOk();
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.SingleAsync(o => o.Id == created.OrderId);
        Assert.Null(order.ShipSubdistrictId);
        Assert.Null(order.ShipAddress);
        Assert.Equal(400m, created.Total);
    }

    [Fact]
    public async Task Unpublished_event_is_not_found()
    {
        await using (var db = Pg.CreateDbContext())
            await db.Events.Where(e => e.Id == Ev.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.IsPublished, false));
        await AssertError(await PostOrder(Request()), HttpStatusCode.NotFound, "EVENT_NOT_FOUND");
    }

    [Fact]
    public async Task Closed_registration_is_rejected()
    {
        await using (var db = Pg.CreateDbContext())
            await db.Events.Where(e => e.Id == Ev.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.RegistrationClosesAt, TestData.Utc(2026, 11, 1)));
        await AssertError(await PostOrder(Request()), HttpStatusCode.BadRequest, "REGISTRATION_CLOSED");
    }

    [Fact]
    public async Task Full_distance_is_conflict_and_nothing_is_reserved()
    {
        await using (var db = Pg.CreateDbContext())
            await db.EventDistances.Where(d => d.Id == TenK.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Quota, 1));

        var body = await AssertError(await PostOrder(Request(Runner(FiveK), Runner(TenK), Runner(TenK))),
            HttpStatusCode.Conflict, "QUOTA_FULL");
        Assert.Equal(TenK.Id, body.GetProperty("details").GetProperty("distanceId").GetInt32());

        await using var check = Pg.CreateDbContext();
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
        Assert.Equal(0, (await check.EventShirtSizes.SingleAsync(s => s.Id == M.Id)).ReservedCount);
        Assert.False(await check.Orders.AnyAsync(o => o.EventId == Ev.Id));
    }

    [Fact]
    public async Task Out_of_stock_shirt_is_conflict_and_distance_reservation_rolls_back()
    {
        await using (var db = Pg.CreateDbContext())
            await db.EventShirtSizes.Where(s => s.Id == L.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, 0));

        var body = await AssertError(await PostOrder(Request(Runner(FiveK, L))), HttpStatusCode.Conflict, "SHIRT_OUT_OF_STOCK");
        Assert.Equal("L", body.GetProperty("details").GetProperty("size").GetString());

        await using var check = Pg.CreateDbContext();
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
    }

    [Fact]
    public async Task Exhausted_promo_is_conflict()
    {
        await AddPromoAsync(p => { p.MaxUses = 1; p.UsedCount = 1; });
        await AssertError(await PostOrder(Request() with { PromoCode = "EARLY10" }), HttpStatusCode.Conflict, "PROMO_EXHAUSTED");
    }

    [Fact]
    public async Task Expired_promo_is_unprocessable()
    {
        await AddPromoAsync(p => p.EndsAt = TestData.Utc(2026, 11, 1));
        var body = await AssertError(await PostOrder(Request() with { PromoCode = "EARLY10" }),
            HttpStatusCode.UnprocessableEntity, "PROMO_INVALID");
        Assert.Equal("EXPIRED", body.GetProperty("details").GetProperty("reason").GetString());
    }

    private async Task AssertValidationError(CreateOrderRequest request, string field)
    {
        var body = await AssertError(await PostOrder(request), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.True(body.GetProperty("details").TryGetProperty(field, out _),
            $"expected details.{field} in {body.GetProperty("details")}");
    }

    [Fact]
    public Task Pdpa_must_be_accepted() => AssertValidationError(Request() with { AcceptedPdpa = false }, "acceptedPdpa");

    [Fact]
    public Task At_least_one_runner() => AssertValidationError(Request() with { Runners = [] }, "runners");

    [Fact]
    public Task At_most_ten_runners() =>
        AssertValidationError(Request() with { Runners = [.. Enumerable.Range(0, 11).Select(_ => Runner())] }, "runners");

    [Fact]
    public async Task Distance_of_another_event_is_rejected()
    {
        int otherDistanceId;
        await using (var db = Pg.CreateDbContext())
            otherDistanceId = (await TestData.CreateRegistrationEventAsync(db)).Distances[0].Id;
        await AssertValidationError(Request(Runner() with { DistanceId = otherDistanceId }), "runners[0].distanceId");
    }

    [Fact]
    public Task Contact_phone_must_be_9_or_10_digits() =>
        AssertValidationError(Request() with { Contact = new ContactInput("ก", "12345", null) }, "contact.phone");

    [Fact]
    public Task Birth_date_cannot_be_in_future() =>
        AssertValidationError(Request(Runner() with { BirthDate = new DateOnly(2027, 1, 1) }), "runners[0].birthDate");

    [Fact]
    public Task Emergency_contact_is_required() =>
        AssertValidationError(Request(Runner() with { EmergencyContact = null }), "runners[0].emergencyContact");

    [Fact]
    public Task Post_requires_address() =>
        AssertValidationError(Request() with { ShippingOptionId = Post.Id }, "shippingAddress");

    [Fact]
    public Task Postal_code_must_match_subdistrict() =>
        AssertValidationError(Request() with { ShippingOptionId = Post.Id, ShippingAddress = Address("10500") },
            "shippingAddress.postalCode");

    [Fact]
    public async Task Inactive_shipping_option_is_rejected()
    {
        await using (var db = Pg.CreateDbContext())
            await db.ShippingOptions.Where(s => s.Id == Pickup.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await AssertValidationError(Request(), "shippingOptionId");
    }

    [Fact]
    public async Task Empty_body_is_validation_error()
    {
        var res = await Client.PostAsync($"/api/v1/events/{Ev.Slug}/orders",
            new StringContent("", System.Text.Encoding.UTF8, "application/json"));
        await AssertError(res, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
    }
}
```

- [ ] **Step 2: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter FullyQualifiedName~CreateOrderTests`
Expected: FAIL — `Expected: Created, Actual: NotFound` และ validation test ได้ 404/405 แทน 400

- [ ] **Step 3: เขียน `src/HuebRun.Infrastructure/Orders/OrderValidator.cs` ทั้งไฟล์**

```csharp
using System.Net.Mail;
using HuebRun.Domain;

namespace HuebRun.Infrastructure.Orders;

/// ข้อมูลอ้างอิงของงานที่ใช้ตรวจคำขอ (โหลดครั้งเดียวต่อคำขอ)
public sealed class OrderReferenceData
{
    public required IReadOnlyDictionary<int, EventDistance> Distances { get; init; }
    public required IReadOnlyDictionary<int, EventShirtSize> ShirtSizes { get; init; }
    public required IReadOnlyDictionary<int, ShippingOption> ShippingOptions { get; init; }   // active เท่านั้น
    public required IReadOnlySet<int> TitleIds { get; init; }
    public required IReadOnlySet<int> NationalityIds { get; init; }
    public required IReadOnlySet<int> BloodTypeIds { get; init; }
    public required IReadOnlySet<int> RelationIds { get; init; }
    public Subdistrict? ShipSubdistrict { get; init; }                                         // รวม District.Province
}

public static class OrderValidator
{
    public const int MaxRunners = 10;

    /// คืน map ฟิลด์ → ข้อความ (ว่าง = ผ่าน)
    public static Dictionary<string, string[]> Validate(CreateOrderRequest request, OrderReferenceData data, DateTime utcNow)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list)) errors[field] = list = [];
            list.Add(message);
        }

        if (!request.AcceptedPdpa) Add("acceptedPdpa", "ต้องยอมรับนโยบายข้อมูลส่วนบุคคล");

        if (request.Contact is not { } contact) Add("contact", "กรุณากรอกข้อมูลผู้ติดต่อ");
        else
        {
            if (!HasLength(contact.Name, 1, 200)) Add("contact.name", "กรุณากรอกชื่อผู้ติดต่อ");
            if (NormalizePhone(contact.Phone) is null) Add("contact.phone", "เบอร์โทรต้องเป็นตัวเลข 9–10 หลัก");
            if (!IsBlank(contact.Email) && !IsEmail(contact.Email!)) Add("contact.email", "รูปแบบอีเมลไม่ถูกต้อง");
        }

        var runners = request.Runners ?? [];
        if (runners.Count is < 1 or > MaxRunners) Add("runners", $"ต้องมีนักวิ่ง 1–{MaxRunners} คน");
        var today = DateOnly.FromDateTime(utcNow);
        for (var i = 0; i < runners.Count && i < MaxRunners; i++)
        {
            var r = runners[i];
            var p = $"runners[{i}]";
            if (r.DistanceId is not { } d || !data.Distances.ContainsKey(d)) Add($"{p}.distanceId", "ไม่พบระยะนี้ในงาน");
            if (r.ShirtSizeId is not { } s || !data.ShirtSizes.ContainsKey(s)) Add($"{p}.shirtSizeId", "ไม่พบไซซ์เสื้อนี้ในงาน");
            if (!HasLength(r.FirstName, 1, 100)) Add($"{p}.firstName", "กรุณากรอกชื่อ (ไม่เกิน 100 ตัวอักษร)");
            if (!HasLength(r.LastName, 1, 100)) Add($"{p}.lastName", "กรุณากรอกนามสกุล (ไม่เกิน 100 ตัวอักษร)");
            if (r.Gender is null) Add($"{p}.gender", "กรุณาเลือกเพศ");
            if (r.BirthDate is not { } birth || birth > today) Add($"{p}.birthDate", "วันเกิดไม่ถูกต้อง");
            if (r.NationalityId is not { } n || !data.NationalityIds.Contains(n)) Add($"{p}.nationalityId", "กรุณาเลือกสัญชาติ");
            if (r.TitleId is { } t && !data.TitleIds.Contains(t)) Add($"{p}.titleId", "ไม่พบคำนำหน้านี้");
            if (r.BloodTypeId is { } b && !data.BloodTypeIds.Contains(b)) Add($"{p}.bloodTypeId", "ไม่พบกรุ๊ปเลือดนี้");
            if (!IsBlank(r.Phone) && NormalizePhone(r.Phone) is null) Add($"{p}.phone", "เบอร์โทรต้องเป็นตัวเลข 9–10 หลัก");
            if (!IsBlank(r.Email) && !IsEmail(r.Email!)) Add($"{p}.email", "รูปแบบอีเมลไม่ถูกต้อง");
            if (r.EmergencyContact is not { } ec || !HasLength(ec.Name, 1, 200) || NormalizePhone(ec.Phone) is null)
                Add($"{p}.emergencyContact", "กรุณากรอกชื่อและเบอร์ผู้ติดต่อฉุกเฉิน");
            else if (ec.RelationId is { } rel && !data.RelationIds.Contains(rel))
                Add($"{p}.emergencyContact.relationId", "ไม่พบความสัมพันธ์นี้");
        }

        if (request.ShippingOptionId is not { } so || !data.ShippingOptions.TryGetValue(so, out var option))
            Add("shippingOptionId", "กรุณาเลือกวิธีรับของ");
        else if (option.Type == ShippingType.Post)
        {
            if (request.ShippingAddress is not { } a) Add("shippingAddress", "กรุณากรอกที่อยู่จัดส่ง");
            else
            {
                if (!HasLength(a.Name, 1, 200)) Add("shippingAddress.name", "กรุณากรอกชื่อผู้รับ");
                if (NormalizePhone(a.Phone) is null) Add("shippingAddress.phone", "เบอร์โทรต้องเป็นตัวเลข 9–10 หลัก");
                if (!HasLength(a.Line1, 1, 300)) Add("shippingAddress.line1", "กรุณากรอกที่อยู่");
                if (data.ShipSubdistrict is null) Add("shippingAddress.subdistrictId", "ไม่พบตำบลนี้");
                else if (a.PostalCode?.Trim() != data.ShipSubdistrict.ZipCode)
                    Add("shippingAddress.postalCode", "รหัสไปรษณีย์ไม่ตรงกับตำบล");
            }
        }

        return errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    /// ตัด "-" และช่องว่าง แล้วต้องเหลือตัวเลข 9–10 หลัก ไม่งั้นคืน null
    public static string? NormalizePhone(string? raw)
    {
        if (raw is null) return null;
        var digits = raw.Replace("-", "").Replace(" ", "");
        return digits.Length is 9 or 10 && digits.All(c => c is >= '0' and <= '9') ? digits : null;
    }

    internal static string? Clean(string? value) => IsBlank(value) ? null : value!.Trim();

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static bool HasLength(string? value, int min, int max)
    {
        var length = value?.Trim().Length ?? 0;
        return length >= min && length <= max;
    }

    private static bool IsEmail(string value) =>
        MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim();
}
```

- [ ] **Step 4: เพิ่มการสร้างออเดอร์ใน `src/HuebRun.Infrastructure/Orders/OrderService.cs`**

เพิ่ม using บนสุด:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using Npgsql;
```

เพิ่มสมาชิกในคลาส `OrderService` (หลัง `ValidatePromoAsync`):

```csharp
    private const int MaxCreateAttempts = 3;

    /// สร้างออเดอร์ + จองโควตา/เสื้อ/โค้ดใน transaction เดียว
    public async Task<CreatedOrder> CreateOrderAsync(string slug, CreateOrderRequest request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryCreateOrderAsync(slug, request, ct);
            }
            catch (DbUpdateException ex) when (IsPayableConflict(ex))
            {
                // ด่านสำรอง: ปกติ advisory lock กันไว้แล้ว
                db.ChangeTracker.Clear();
                if (attempt >= MaxCreateAttempts) throw OrderErrors.PaymentSlotBusy();
            }
        }
    }

    private async Task<CreatedOrder> TryCreateOrderAsync(string slug, CreateOrderRequest request, CancellationToken ct)
    {
        var now = Now;
        var ev = await LoadPublishedEventAsync(slug, ct);
        if (!ev.IsRegistrationOpen(now)) throw OrderErrors.RegistrationClosed();

        var data = await LoadReferenceDataAsync(ev.Id, request, ct);
        var errors = OrderValidator.Validate(request, data, now);
        if (errors.Count > 0) throw OrderErrors.Validation(errors);

        var runners = request.Runners!;
        var subTotal = OrderPricing.SubTotal(runners.Select(r => data.Distances[r.DistanceId!.Value]), now);
        PromoCode? promo = null;
        var discount = 0m;
        if (OrderValidator.Clean(request.PromoCode) is { } code)
        {
            promo = await FindPromoAsync(ev.Id, code, ct);
            if (OrderPricing.CheckPromo(promo, subTotal, now) is { } rejection) throw OrderErrors.Promo(rejection);
            discount = OrderPricing.Discount(promo!, subTotal);
        }
        var shipping = data.ShippingOptions[request.ShippingOptionId!.Value];
        var price = OrderPricing.Calculate(subTotal, discount, shipping.Fee);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // จองแบบ atomic เรียงตาม Id ลดโอกาส deadlock; ล้มที่ใดก็ rollback ทั้งหมด
        foreach (var group in runners.GroupBy(r => r.DistanceId!.Value).OrderBy(g => g.Key))
        {
            var (id, n) = (group.Key, group.Count());
            var reserved = await db.EventDistances
                .Where(d => d.Id == id && d.ReservedCount + n <= d.Quota)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReservedCount, d => d.ReservedCount + n), ct);
            if (reserved == 0) throw OrderErrors.QuotaFull(id, data.Distances[id].Name);
        }
        foreach (var group in runners.GroupBy(r => r.ShirtSizeId!.Value).OrderBy(g => g.Key))
        {
            var (id, n) = (group.Key, group.Count());
            var reserved = await db.EventShirtSizes
                .Where(s => s.Id == id && s.ReservedCount + n <= s.Stock)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedCount, x => x.ReservedCount + n), ct);
            if (reserved == 0) throw OrderErrors.ShirtOutOfStock(id, data.ShirtSizes[id].Size);
        }
        if (promo is not null)
        {
            var promoId = promo.Id;
            var used = await db.PromoCodes
                .Where(p => p.Id == promoId && (p.MaxUses == null || p.UsedCount < p.MaxUses))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UsedCount, p => p.UsedCount + 1), ct);
            if (used == 0) throw OrderErrors.Promo(PromoRejection.Exhausted);
        }

        var payable = await PickPayableAmountAsync(ev.Id, price.Total, ct);
        var sequence = await db.Database.SqlQuery<long>($"SELECT nextval('order_no_seq') AS \"Value\"").SingleAsync(ct);

        var order = BuildOrder(ev, request, data, promo, shipping, price, payable, sequence, now);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new CreatedOrder(order.Id, order.OrderNo, order.Total, order.PayableAmount, order.ExpiresAt);
    }

    /// ยอด + เศษสตางค์ 0.01–0.99 ค่าน้อยสุดที่ออเดอร์ Pending ของงานนี้ยังไม่ใช้
    /// ถือ advisory lock ต่อ (งาน, ยอด) จนจบ transaction ให้คำขอยอดเดียวกันเลือกทีละคน
    private async Task<decimal> PickPayableAmountAsync(int eventId, decimal total, CancellationToken ct)
    {
        var totalCents = (int)(total * 100);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({eventId}, {totalCents})", ct);

        var low = total + 0.01m;
        var high = total + 0.99m;
        var taken = (await db.Orders
                .Where(o => o.EventId == eventId && o.Status == OrderStatus.Pending
                         && o.PayableAmount >= low && o.PayableAmount <= high)
                .Select(o => o.PayableAmount)
                .ToListAsync(ct))
            .ToHashSet();
        for (var satang = 1; satang <= 99; satang++)
        {
            var candidate = total + satang / 100m;
            if (!taken.Contains(candidate)) return candidate;
        }
        throw OrderErrors.PaymentSlotBusy();
    }

    private async Task<OrderReferenceData> LoadReferenceDataAsync(int eventId, CreateOrderRequest request, CancellationToken ct)
    {
        Subdistrict? subdistrict = null;
        if (request.ShippingAddress?.SubdistrictId is { } subId)
            subdistrict = await db.Subdistricts.AsNoTracking()
                .Include(s => s.District).ThenInclude(d => d.Province)
                .FirstOrDefaultAsync(s => s.Id == subId, ct);

        return new OrderReferenceData
        {
            Distances = await db.EventDistances.AsNoTracking().Where(d => d.EventId == eventId).ToDictionaryAsync(d => d.Id, ct),
            ShirtSizes = await db.EventShirtSizes.AsNoTracking().Where(s => s.EventId == eventId).ToDictionaryAsync(s => s.Id, ct),
            ShippingOptions = await db.ShippingOptions.AsNoTracking().Where(s => s.EventId == eventId && s.IsActive).ToDictionaryAsync(s => s.Id, ct),
            TitleIds = (await db.Titles.Where(x => x.IsActive).Select(x => x.Id).ToListAsync(ct)).ToHashSet(),
            NationalityIds = (await db.Nationalities.Where(x => x.IsActive).Select(x => x.Id).ToListAsync(ct)).ToHashSet(),
            BloodTypeIds = (await db.BloodTypes.Where(x => x.IsActive).Select(x => x.Id).ToListAsync(ct)).ToHashSet(),
            RelationIds = (await db.EmergencyRelations.Where(x => x.IsActive).Select(x => x.Id).ToListAsync(ct)).ToHashSet(),
            ShipSubdistrict = subdistrict,
        };
    }

    private static Order BuildOrder(Event ev, CreateOrderRequest request, OrderReferenceData data, PromoCode? promo,
        ShippingOption shipping, PriceBreakdown price, decimal payable, long sequence, DateTime now)
    {
        var contact = request.Contact!;
        var isPost = shipping.Type == ShippingType.Post;
        var address = isPost ? request.ShippingAddress : null;
        var sub = isPost ? data.ShipSubdistrict : null;

        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNo = $"HR{ev.Year % 100:D2}-{sequence:D6}",
            EventId = ev.Id,
            ContactName = contact.Name!.Trim(),
            Phone = OrderValidator.NormalizePhone(contact.Phone)!,
            Email = OrderValidator.Clean(contact.Email),
            Status = OrderStatus.Pending,
            SubTotal = price.SubTotal,
            DiscountAmount = price.DiscountAmount,
            ShippingFee = price.ShippingFee,
            Total = price.Total,
            PayableAmount = payable,
            PromoCodeId = promo?.Id,
            ShippingOptionId = shipping.Id,
            ShipName = address?.Name?.Trim(),
            ShipPhone = address is null ? null : OrderValidator.NormalizePhone(address.Phone),
            ShipAddress = address?.Line1?.Trim(),
            ShipSubdistrictId = sub?.Id,
            ShipSubdistrictText = sub?.NameTh,
            ShipDistrictText = sub?.District.NameTh,
            ShipProvinceText = sub?.District.Province.NameTh,
            ShipPostalCode = sub?.ZipCode,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(ev.ReservationMinutes),
        };

        foreach (var r in request.Runners!)
        {
            var ec = r.EmergencyContact!;
            order.Registrations.Add(new Registration
            {
                EventId = ev.Id,
                EventDistanceId = r.DistanceId!.Value,
                EventShirtSizeId = r.ShirtSizeId!.Value,
                PricePaid = data.Distances[r.DistanceId.Value].GetEffectivePrice(now),
                TitleId = r.TitleId,
                FirstName = r.FirstName!.Trim(),
                LastName = r.LastName!.Trim(),
                Gender = r.Gender!.Value,
                BirthDate = r.BirthDate!.Value,
                NationalityId = r.NationalityId!.Value,
                BloodTypeId = r.BloodTypeId,
                Phone = OrderValidator.NormalizePhone(r.Phone),
                Email = OrderValidator.Clean(r.Email),
                TeamName = OrderValidator.Clean(r.TeamName),
                EmergencyRelationId = ec.RelationId,
                EmergencyContactName = ec.Name!.Trim(),
                EmergencyContactPhone = OrderValidator.NormalizePhone(ec.Phone)!,
                MedicalNote = OrderValidator.Clean(r.MedicalNote),
                CheckInToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
            });
        }
        return order;
    }

    private static bool IsPayableConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == OrderErrors.PayableIndexName;
```

- [ ] **Step 5: เพิ่ม action ใน `RegistrationController`**

```csharp
    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder(string slug, CreateOrderRequest request, CancellationToken ct)
    {
        var created = await orders.CreateOrderAsync(slug, request, ct);
        return Created($"/api/v1/orders/{created.OrderId}", created);
    }
```

- [ ] **Step 6: รัน test ให้ผ่าน**

Run: `dotnet test --filter FullyQualifiedName~CreateOrderTests`
Expected: PASS 19 tests

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(api): create order with server-side pricing and atomic reservations"
```

---

### Task 5: Concurrency — last seats, last promo use, same-total orders

**Files:**
- Test: `tests/HuebRun.Tests/Api/CreateOrderConcurrencyTests.cs`
- Modify (เฉพาะถ้า test ล้ม): `src/HuebRun.Infrastructure/Orders/OrderService.cs`

**Interfaces:**
- Consumes: `OrderTestBase`, `POST /orders` (Task 4)

- [ ] **Step 1: เขียน test**

`tests/HuebRun.Tests/Api/CreateOrderConcurrencyTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class CreateOrderConcurrencyTests(PostgresFixture pg) : OrderTestBase(pg)
{
    protected override void ConfigureEvent(Event e) => e.Distances.Single(d => d.Name == "10K").Quota = 5;

    [Fact]
    public async Task Twenty_concurrent_orders_for_five_seats_create_exactly_five()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => PostOrder(Request(Runner(TenK, M, $"r{i}")))));

        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        var conflicts = results.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();
        Assert.Equal(15, conflicts.Count);
        foreach (var c in conflicts) await AssertError(c, HttpStatusCode.Conflict, "QUOTA_FULL");

        await using var db = Pg.CreateDbContext();
        Assert.Equal(5, (await db.EventDistances.SingleAsync(d => d.Id == TenK.Id)).ReservedCount);
        Assert.Equal(5, (await db.EventShirtSizes.SingleAsync(s => s.Id == M.Id)).ReservedCount);
        Assert.Equal(5, await db.Orders.CountAsync(o => o.EventId == Ev.Id));
    }

    [Fact]
    public async Task Concurrent_orders_with_same_total_all_succeed_with_distinct_payable_amounts()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 15)
            .Select(i => PostOrder(Request(Runner(FiveK, L, $"p{i}")))));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var created = await Task.WhenAll(results.Select(r => r.Content.ReadFromJsonAsync<CreatedOrder>(Json)));
        var payables = created.Select(c => c!.PayableAmount).ToList();
        Assert.Equal(15, payables.Distinct().Count());
        Assert.All(payables, p => Assert.InRange(p, 400.01m, 400.99m));
    }

    [Fact]
    public async Task Last_promo_use_goes_to_exactly_one_order()
    {
        await AddPromoAsync(p => p.MaxUses = 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(i => PostOrder(Request(Runner(FiveK, M, $"x{i}")) with { PromoCode = "EARLY10" })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var r in results.Where(r => r.StatusCode != HttpStatusCode.Created))
            await AssertError(r, HttpStatusCode.Conflict, "PROMO_EXHAUSTED");
        await using var db = Pg.CreateDbContext();
        Assert.Equal(1, (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
    }

    [Fact]
    public async Task Two_orders_with_same_total_get_different_payable_amounts()
    {
        var a = await CreateOrderOk();
        var b = await CreateOrderOk();
        Assert.Equal(400.01m, a.PayableAmount);
        Assert.Equal(400.02m, b.PayableAmount);
    }

    [Fact]
    public async Task Non_pending_order_frees_its_payable_amount()
    {
        var a = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
            await db.Orders.Where(o => o.Id == a.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired));
        var b = await CreateOrderOk();
        Assert.Equal(a.PayableAmount, b.PayableAmount);
    }

    [Fact]
    public async Task All_99_satang_slots_taken_is_conflict_and_rolls_back_reservation()
    {
        await using (var db = Pg.CreateDbContext())
        {
            for (var i = 1; i <= 99; i++)
                db.Orders.Add(new Order
                {
                    Id = Guid.NewGuid(), OrderNo = $"FILL-{Ev.Id}-{i}", EventId = Ev.Id, ContactName = "x", Phone = "0812345678",
                    Status = OrderStatus.Pending, Total = 400, PayableAmount = 400 + i / 100m, ShippingOptionId = Pickup.Id,
                    CreatedAt = TestData.Utc(2026, 11, 15), ExpiresAt = TestData.Utc(2026, 11, 16),
                });
            await db.SaveChangesAsync();
        }

        await AssertError(await PostOrder(Request()), HttpStatusCode.Conflict, "PAYMENT_SLOT_BUSY");
        await using var check = Pg.CreateDbContext();
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
    }
}
```

- [ ] **Step 2: รัน test**

Run: `dotnet test --filter FullyQualifiedName~CreateOrderConcurrencyTests`
Expected: PASS 6 tests (Task 4 implement ไว้แล้ว) — ถ้า test ใดผ่านตั้งแต่ครั้งแรก ให้ทำ mutation check ก่อน commit: คอมเมนต์บรรทัด `pg_advisory_xact_lock` ชั่วคราว → `Concurrent_orders_with_same_total...` ต้อง FAIL; เปลี่ยน `d.ReservedCount + n <= d.Quota` เป็น `true` ชั่วคราว → `Twenty_concurrent...` ต้อง FAIL; คืนค่าแล้วรันให้ผ่าน (สำรองไฟล์ด้วย `cp` ก่อนแก้ เพราะ `git checkout` ใช้ได้เฉพาะไฟล์ที่ commit แล้ว)

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test: concurrency guarantees for seats, promo uses, and payable amounts"
```

---

### Task 6: Bib issuance — `OrderPaymentService.MarkPaidAsync`

**Files:**
- Create: `src/HuebRun.Infrastructure/Orders/OrderPaymentService.cs`
- Modify: `src/HuebRun.Infrastructure/DependencyInjection.cs`
- Test: `tests/HuebRun.Tests/Data/OrderPaymentServiceTests.cs`

**Interfaces:**
- Consumes: `OrderTestBase`, `CreatedOrder`
- Produces:
  - `record MarkPaidResult(bool AlreadyPaid)`
  - `OrderPaymentService(AppDbContext db)` with `Task<MarkPaidResult> MarkPaidAsync(Guid orderId, DateTime paidAtUtc, CancellationToken ct = default)` — throws `InvalidOperationException` ถ้าออเดอร์ไม่พบหรือไม่ใช่ Pending/Paid

- [ ] **Step 1: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Data/OrderPaymentServiceTests.cs`:

```csharp
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class OrderPaymentServiceTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private static readonly DateTime PaidAt = TestData.Utc(2026, 11, 15);

    private async Task<MarkPaidResult> MarkPaid(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        return await new OrderPaymentService(db).MarkPaidAsync(orderId, PaidAt);
    }

    private async Task<List<string?>> Bibs(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        return await db.Registrations.Where(r => r.OrderId == orderId).OrderBy(r => r.Id).Select(r => r.BibNumber).ToListAsync();
    }

    [Fact]
    public async Task Marks_paid_and_issues_sequential_bibs_per_distance()
    {
        var first = await CreateOrderOk(Request(Runner(FiveK), Runner(TenK), Runner(FiveK)));
        var second = await CreateOrderOk(Request(Runner(FiveK)));

        Assert.False((await MarkPaid(first.OrderId)).AlreadyPaid);
        await MarkPaid(second.OrderId);

        Assert.Equal(["B0001", "C0001", "B0002"], await Bibs(first.OrderId));
        Assert.Equal(["B0003"], await Bibs(second.OrderId));
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.SingleAsync(o => o.Id == first.OrderId);
        Assert.Equal((OrderStatus.Paid, (DateTime?)PaidAt), (order.Status, order.PaidAt));
        Assert.Equal(3, (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);   // ยังนับเป็นจอง
    }

    [Fact]
    public async Task Marking_twice_is_idempotent()
    {
        var created = await CreateOrderOk();
        await MarkPaid(created.OrderId);
        Assert.True((await MarkPaid(created.OrderId)).AlreadyPaid);
        Assert.Equal(["B0001"], await Bibs(created.OrderId));
    }

    [Fact]
    public async Task Concurrent_payments_never_share_a_bib()
    {
        var orders = new List<CreatedOrder>();
        for (var i = 0; i < 8; i++) orders.Add(await CreateOrderOk(Request(Runner(FiveK), Runner(FiveK))));

        await Task.WhenAll(orders.Select(o => MarkPaid(o.OrderId)));

        await using var db = Pg.CreateDbContext();
        var bibs = await db.Registrations.Where(r => r.EventId == Ev.Id).Select(r => r.BibNumber!).ToListAsync();
        Assert.Equal(16, bibs.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 16).Select(n => $"B{n:D4}"), bibs.Order());
    }

    [Fact]
    public async Task Expired_order_cannot_be_marked_paid()
    {
        var created = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
            await db.Orders.Where(o => o.Id == created.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarkPaid(created.OrderId));
    }

    [Fact]
    public async Task Unknown_order_cannot_be_marked_paid() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarkPaid(Guid.NewGuid()));
}
```

- [ ] **Step 2: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter FullyQualifiedName~OrderPaymentServiceTests`
Expected: compile error `The type or namespace name 'OrderPaymentService' could not be found`

- [ ] **Step 3: สร้าง `src/HuebRun.Infrastructure/Orders/OrderPaymentService.cs`**

```csharp
using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace HuebRun.Infrastructure.Orders;

public sealed record MarkPaidResult(bool AlreadyPaid);

/// เปลี่ยนออเดอร์เป็น Paid และออกเลขบิบ — ขั้นชำระเงิน (สลิป/gateway) เรียกใช้
public sealed class OrderPaymentService(AppDbContext db)
{
    public async Task<MarkPaidResult> MarkPaidAsync(Guid orderId, DateTime paidAtUtc, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // update แบบมีเงื่อนไข: แข่งกับ job หมดเวลาได้ฝั่งเดียว
        var updated = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Paid).SetProperty(o => o.PaidAt, paidAtUtc), ct);
        if (updated == 0)
        {
            var status = await db.Orders.Where(o => o.Id == orderId).Select(o => (OrderStatus?)o.Status).FirstOrDefaultAsync(ct);
            if (status == OrderStatus.Paid) return new MarkPaidResult(AlreadyPaid: true);
            throw new InvalidOperationException(
                $"Order {orderId} cannot be marked paid (status: {status?.ToString() ?? "not found"})");
        }

        var registrations = await db.Registrations
            .Include(r => r.EventDistance)
            .Where(r => r.OrderId == orderId && !r.IsCancelled)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);
        foreach (var group in registrations.GroupBy(r => r.EventDistanceId).OrderBy(g => g.Key))
        {
            var count = group.Count();
            var last = await ReserveBibNumbersAsync(group.Key, count, tx, ct);
            var next = last - count + 1;
            foreach (var registration in group)
                registration.BibNumber = $"{registration.EventDistance.BibPrefix}{next++:D4}";
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new MarkPaidResult(AlreadyPaid: false);
    }

    // เพิ่มตัวนับแบบ atomic แล้วคืนเลขสุดท้ายที่จองได้
    private async Task<int> ReserveBibNumbersAsync(int distanceId, int count, IDbContextTransaction tx, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText =
            "UPDATE \"EventDistances\" SET \"LastBibNumber\" = \"LastBibNumber\" + @count WHERE \"Id\" = @id RETURNING \"LastBibNumber\"";
        command.Parameters.Add(new NpgsqlParameter("count", count));
        command.Parameters.Add(new NpgsqlParameter("id", distanceId));
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }
}
```

ใน `DependencyInjection.cs` เพิ่ม `services.AddScoped<OrderPaymentService>();`

- [ ] **Step 4: รัน test ให้ผ่าน**

Run: `dotnet test --filter FullyQualifiedName~OrderPaymentServiceTests`
Expected: PASS 5 tests

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(infra): mark order paid and issue sequential bib numbers"
```

---

### Task 7: Order expiry — service, worker, race with payment

**Files:**
- Create: `src/HuebRun.Infrastructure/Orders/OrderExpiryService.cs`
- Create: `src/HuebRun.Api/Jobs/OrderExpiryWorker.cs`
- Modify: `src/HuebRun.Infrastructure/DependencyInjection.cs`, `src/HuebRun.Api/Program.cs`, `src/HuebRun.Api/appsettings.json`
- Modify: `tests/HuebRun.Tests/Infrastructure/ApiFactory.cs`
- Test: `tests/HuebRun.Tests/Data/OrderExpiryTests.cs`, `tests/HuebRun.Tests/Api/HostTests.cs`

**Interfaces:**
- Consumes: `OrderPaymentService` (Task 6), `OrderTestBase`
- Produces:
  - `OrderExpiryService(AppDbContext db, TimeProvider time)` with `Task<int> ExpireDueOrdersAsync(CancellationToken ct = default)` (คืนจำนวนที่หมดเวลาในรอบนี้), `const int BatchSize = 200`
  - `OrderExpiryWorker : BackgroundService` (namespace `HuebRun.Api.Jobs`)
  - config `Jobs:OrderExpiry:Enabled` (default true)
  - `ApiFactory(string connectionString, bool jobsEnabled = false)`

- [ ] **Step 1: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Data/OrderExpiryTests.cs`:

```csharp
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class OrderExpiryTests(PostgresFixture pg) : OrderTestBase(pg)
{
    // ออเดอร์สร้างตอน 2026-11-15T00:00Z หมดอายุ 00:30Z
    private static readonly FakeTimeProvider AfterExpiry = new(new DateTimeOffset(2026, 11, 15, 0, 31, 0, TimeSpan.Zero));
    private static readonly FakeTimeProvider BeforeExpiry = new(new DateTimeOffset(2026, 11, 15, 0, 29, 0, TimeSpan.Zero));

    private async Task<int> RunExpiry(TimeProvider time)
    {
        await using var db = Pg.CreateDbContext();
        return await new OrderExpiryService(db, time).ExpireDueOrdersAsync();
    }

    private async Task<(OrderStatus Status, int FiveK, int TenK, int M, int Promo)> Snapshot(Guid orderId, int promoId = 0)
    {
        await using var db = Pg.CreateDbContext();
        return (
            (await db.Orders.SingleAsync(o => o.Id == orderId)).Status,
            (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount,
            (await db.EventDistances.SingleAsync(d => d.Id == TenK.Id)).ReservedCount,
            (await db.EventShirtSizes.SingleAsync(s => s.Id == M.Id)).ReservedCount,
            promoId == 0 ? 0 : (await db.PromoCodes.SingleAsync(p => p.Id == promoId)).UsedCount);
    }

    [Fact]
    public async Task Expires_due_order_and_releases_seats_shirts_and_promo()
    {
        var promo = await AddPromoAsync();
        var created = await CreateOrderOk(Request(Runner(FiveK), Runner(FiveK), Runner(TenK)) with { PromoCode = "EARLY10" });
        Assert.Equal((OrderStatus.Pending, 2, 1, 3, 1), await Snapshot(created.OrderId, promo.Id));

        Assert.True(await RunExpiry(AfterExpiry) >= 1);

        Assert.Equal((OrderStatus.Expired, 0, 0, 0, 0), await Snapshot(created.OrderId, promo.Id));
    }

    [Fact]
    public async Task Running_twice_does_not_release_twice()
    {
        var keep = await CreateOrderOk();                       // จะจ่าย → ต้องคงการจองไว้
        var drop = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
            await new OrderPaymentService(db).MarkPaidAsync(keep.OrderId, TestData.Utc(2026, 11, 15));

        await RunExpiry(AfterExpiry);
        await RunExpiry(AfterExpiry);

        Assert.Equal((OrderStatus.Expired, 1, 0, 1, 0), await Snapshot(drop.OrderId));
        Assert.Equal(OrderStatus.Paid, (await Snapshot(keep.OrderId)).Status);
    }

    [Fact]
    public async Task Order_not_yet_due_is_untouched()
    {
        var created = await CreateOrderOk();
        await RunExpiry(BeforeExpiry);
        Assert.Equal((OrderStatus.Pending, 1, 0, 1, 0), await Snapshot(created.OrderId));
    }

    [Fact]
    public async Task Payment_racing_expiry_has_exactly_one_winner_and_consistent_counts()
    {
        for (var round = 0; round < 10; round++)
        {
            var created = await CreateOrderOk();
            await using var payDb = Pg.CreateDbContext();
            await using var expireDb = Pg.CreateDbContext();

            var pay = Task.Run(async () =>
            {
                try { await new OrderPaymentService(payDb).MarkPaidAsync(created.OrderId, TestData.Utc(2026, 11, 15)); return true; }
                catch (InvalidOperationException) { return false; }
            });
            var expire = Task.Run(() => new OrderExpiryService(expireDb, AfterExpiry).ExpireDueOrdersAsync());
            var paid = await pay;
            await expire;

            var snap = await Snapshot(created.OrderId);
            Assert.Equal(paid ? OrderStatus.Paid : OrderStatus.Expired, snap.Status);
        }

        // ออเดอร์ที่จ่ายแล้วเท่านั้นที่ยังถือที่นั่ง
        await using var db = Pg.CreateDbContext();
        var paidCount = await db.Orders.CountAsync(o => o.EventId == Ev.Id && o.Status == OrderStatus.Paid);
        Assert.Equal(paidCount, (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
        Assert.Equal(paidCount, (await db.EventShirtSizes.SingleAsync(s => s.Id == M.Id)).ReservedCount);
    }
}
```

เพิ่มใน `tests/HuebRun.Tests/Api/HostTests.cs` (ในคลาส `HostTests`) และ `using HuebRun.Api.Jobs;` + `using Microsoft.Extensions.DependencyInjection;` + `using Microsoft.Extensions.Hosting;` บนสุด:

```csharp
    [Fact]
    public void Order_expiry_worker_is_off_in_tests_by_default()
    {
        using var factory = new ApiFactory(pg.ConnectionString);
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), s => s is OrderExpiryWorker);
    }

    [Fact]
    public void Order_expiry_worker_runs_when_enabled()
    {
        using var factory = new ApiFactory(pg.ConnectionString, jobsEnabled: true);
        Assert.Contains(factory.Services.GetServices<IHostedService>(), s => s is OrderExpiryWorker);
    }
```

แก้ `tests/HuebRun.Tests/Infrastructure/ApiFactory.cs`: เปลี่ยน primary constructor เป็น `ApiFactory(string connectionString, bool jobsEnabled = false)` และใน `ConfigureWebHost` เพิ่มหลังบรรทัด connection string:

```csharp
        builder.UseSetting("Jobs:OrderExpiry:Enabled", jobsEnabled ? "true" : "false");
```

- [ ] **Step 2: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter "FullyQualifiedName~OrderExpiryTests|FullyQualifiedName~HostTests"`
Expected: compile error `'OrderExpiryService' could not be found` / `'OrderExpiryWorker' could not be found`

- [ ] **Step 3: สร้าง `src/HuebRun.Infrastructure/Orders/OrderExpiryService.cs`**

```csharp
using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Orders;

/// เปลี่ยนออเดอร์ Pending ที่เลยเวลาเป็น Expired และคืนโควตา/เสื้อ/โค้ด
public sealed class OrderExpiryService(AppDbContext db, TimeProvider time)
{
    public const int BatchSize = 200;

    public async Task<int> ExpireDueOrdersAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Pending && o.ExpiresAt <= now)
            .OrderBy(o => o.ExpiresAt)
            .Select(o => o.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        var expired = 0;
        foreach (var orderId in due)
            if (await ExpireAsync(orderId, ct)) expired++;
        return expired;
    }

    private async Task<bool> ExpireAsync(Guid orderId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // update แบบมีเงื่อนไข: ถ้าเพิ่งถูกจ่ายหรือถูก job อีกตัวทำไปแล้ว จะไม่คืนซ้ำ
        var updated = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired), ct);
        if (updated == 0) return false;

        var registrations = await db.Registrations.AsNoTracking()
            .Where(r => r.OrderId == orderId && !r.IsCancelled)
            .Select(r => new { r.EventDistanceId, r.EventShirtSizeId })
            .ToListAsync(ct);
        foreach (var group in registrations.GroupBy(r => r.EventDistanceId).OrderBy(g => g.Key))
        {
            var (id, n) = (group.Key, group.Count());
            await db.EventDistances.Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReservedCount, d => d.ReservedCount - n), ct);
        }
        foreach (var group in registrations.GroupBy(r => r.EventShirtSizeId).OrderBy(g => g.Key))
        {
            var (id, n) = (group.Key, group.Count());
            await db.EventShirtSizes.Where(s => s.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedCount, x => x.ReservedCount - n), ct);
        }

        var promoId = await db.Orders.Where(o => o.Id == orderId).Select(o => o.PromoCodeId).SingleAsync(ct);
        if (promoId is { } pid)
            await db.PromoCodes.Where(p => p.Id == pid && p.UsedCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UsedCount, p => p.UsedCount - 1), ct);

        await tx.CommitAsync(ct);
        return true;
    }
}
```

ใน `DependencyInjection.cs` เพิ่ม `services.AddScoped<OrderExpiryService>();`

- [ ] **Step 4: สร้าง `src/HuebRun.Api/Jobs/OrderExpiryWorker.cs`**

```csharp
using HuebRun.Infrastructure.Orders;

namespace HuebRun.Api.Jobs;

/// เรียก OrderExpiryService ทุก 1 นาที (ถ้ารันหลายเครื่องก็ปลอดภัย เพราะทุกการเปลี่ยนสถานะเป็น update แบบมีเงื่อนไข)
public sealed class OrderExpiryWorker(IServiceScopeFactory scopes, ILogger<OrderExpiryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var expired = await scope.ServiceProvider.GetRequiredService<OrderExpiryService>()
                    .ExpireDueOrdersAsync(stoppingToken);
                if (expired > 0) logger.LogInformation("Expired {Count} unpaid orders", expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Order expiry run failed; will retry next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

- [ ] **Step 5: เปิด worker ตาม config**

ใน `src/HuebRun.Api/Program.cs` เพิ่ม `using HuebRun.Api.Jobs;` และหลังบรรทัด `builder.Services.AddSingleton<GeoSearchIndex>();`:

```csharp
if (builder.Configuration.GetValue("Jobs:OrderExpiry:Enabled", true))
    builder.Services.AddHostedService<OrderExpiryWorker>();
```

ใน `src/HuebRun.Api/appsettings.json` เพิ่ม property ระดับบนสุด (หลัง `"Cors"`):

```json
  "Jobs": {
    "OrderExpiry": { "Enabled": true }
  },
```

- [ ] **Step 6: รัน test ให้ผ่าน**

Run: `dotnet test --filter "FullyQualifiedName~OrderExpiryTests|FullyQualifiedName~HostTests"`
Expected: PASS (OrderExpiryTests 4, HostTests 5)

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: expire unpaid orders every minute and release reservations"
```

---

### Task 8: Order query endpoints — `GET /orders/{id}`, `/tickets`

**Files:**
- Create: `src/HuebRun.Api/Contracts/OrderContracts.cs`, `src/HuebRun.Api/Controllers/OrdersController.cs`
- Test: `tests/HuebRun.Tests/Api/OrderQueryTests.cs`

**Interfaces:**
- Consumes: `Order.GetEffectiveStatus`, `OrderErrors.OrderNotFound/OrderNotPaid`, `OrderPaymentService`
- Produces: `GET /api/v1/orders/{orderId}`, `GET /api/v1/orders/{orderId}/tickets`

- [ ] **Step 1: เขียน test ที่ล้มเหลว**

`tests/HuebRun.Tests/Api/OrderQueryTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Infrastructure.Orders;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class OrderQueryTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private async Task<(JsonElement Body, string Raw)> GetOrder(Guid id)
    {
        var res = await Client.GetAsync($"/api/v1/orders/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var raw = await res.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(raw).RootElement, raw);
    }

    [Fact]
    public async Task Returns_order_summary_without_sensitive_fields()
    {
        var created = await CreateOrderOk(Request(Runner(FiveK, M, "เอ"), Runner(TenK, L, "บี")));
        var (body, raw) = await GetOrder(created.OrderId);

        Assert.Equal(created.OrderNo, body.GetProperty("orderNo").GetString());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(1100m, body.GetProperty("total").GetDecimal());
        Assert.Equal(created.PayableAmount, body.GetProperty("payableAmount").GetDecimal());
        Assert.Equal("0812345678", body.GetProperty("contact").GetProperty("phone").GetString());
        Assert.Equal("Pickup", body.GetProperty("shipping").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("shipping").GetProperty("address").ValueKind);

        var runners = body.GetProperty("runners").EnumerateArray().ToList();
        Assert.Equal(["เอ", "บี"], runners.Select(r => r.GetProperty("firstName").GetString()));
        Assert.Equal("10K", runners[1].GetProperty("distance").GetProperty("name").GetString());
        Assert.Equal("L", runners[1].GetProperty("shirtSize").GetProperty("size").GetString());
        Assert.Equal(JsonValueKind.Null, runners[0].GetProperty("bibNumber").ValueKind);

        Assert.DoesNotContain("medical", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("แพ้ยา", raw);
        Assert.DoesNotContain("emergency", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("สมหญิง", raw);
        Assert.DoesNotContain("checkInToken", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pending_order_past_expiry_shows_expired()
    {
        var created = await CreateOrderOk();
        Factory.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("Expired", (await GetOrder(created.OrderId)).Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_order_is_not_found()
    {
        await AssertError(await Client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "ORDER_NOT_FOUND");
        await AssertError(await Client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}/tickets"), HttpStatusCode.NotFound, "ORDER_NOT_FOUND");
    }

    [Fact]
    public async Task Tickets_require_paid_order()
    {
        var created = await CreateOrderOk();
        var body = await AssertError(await Client.GetAsync($"/api/v1/orders/{created.OrderId}/tickets"),
            HttpStatusCode.Conflict, "ORDER_NOT_PAID");
        Assert.Equal("Pending", body.GetProperty("details").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Paid_order_returns_tickets_with_bib_and_checkin_token()
    {
        var created = await CreateOrderOk(Request(Runner(FiveK, M, "เอ"), Runner(TenK, M, "บี")));
        await using (var db = Pg.CreateDbContext())
            await new OrderPaymentService(db).MarkPaidAsync(created.OrderId, TestData.Utc(2026, 11, 15));

        var res = await Client.GetAsync($"/api/v1/orders/{created.OrderId}/tickets");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var tickets = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(["B0001", "C0001"], tickets.Select(t => t.GetProperty("bibNumber").GetString()));
        Assert.Equal("5K", tickets[0].GetProperty("distanceName").GetString());
        Assert.All(tickets, t => Assert.True(t.GetProperty("checkInToken").GetString()!.Length >= 40));

        Assert.Equal("B0001", (await GetOrder(created.OrderId)).Body.GetProperty("runners")[0].GetProperty("bibNumber").GetString());
    }
}
```

- [ ] **Step 2: รัน test ให้ล้มเหลว**

Run: `dotnet test --filter FullyQualifiedName~OrderQueryTests`
Expected: FAIL `Expected: OK, Actual: NotFound`

- [ ] **Step 3: สร้าง contracts**

`src/HuebRun.Api/Contracts/OrderContracts.cs`:

```csharp
using HuebRun.Domain;

namespace HuebRun.Api.Contracts;

public sealed record OrderResponse(
    Guid OrderId, string OrderNo, OrderStatus Status,
    decimal SubTotal, decimal DiscountAmount, decimal ShippingFee, decimal Total, decimal PayableAmount,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? PaidAt, string? PromoCode,
    OrderContactResponse Contact, OrderShippingResponse Shipping, IReadOnlyList<OrderRunnerResponse> Runners);

public sealed record OrderContactResponse(string Name, string Phone, string? Email);

public sealed record OrderShippingResponse(ShippingType Type, string Name, OrderAddressResponse? Address, string? TrackingNo);

public sealed record OrderAddressResponse(
    string? Name, string? Phone, string? Line1, string? Subdistrict, string? District, string? Province, string? PostalCode);

public sealed record OrderRunnerResponse(
    int RegistrationId, int? TitleId, string FirstName, string LastName,
    IdName Distance, ShirtSizeRef ShirtSize, decimal PricePaid, string? BibNumber);

public sealed record IdName(int Id, string Name);

public sealed record ShirtSizeRef(int Id, string Size);

public sealed record TicketResponse(
    int RegistrationId, string FirstName, string LastName, string DistanceName, string BibNumber, string CheckInToken);
```

- [ ] **Step 4: สร้าง `src/HuebRun.Api/Controllers/OrdersController.cs`**

```csharp
using HuebRun.Api.Contracts;
using HuebRun.Domain;
using HuebRun.Infrastructure;
using HuebRun.Infrastructure.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/orders/{orderId}")]
public sealed class OrdersController(AppDbContext db, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<OrderResponse> Get(Guid orderId, CancellationToken ct)
    {
        var o = await LoadAsync(orderId, ct);
        var isPost = o.ShippingOption.Type == ShippingType.Post;
        return new OrderResponse(
            o.Id, o.OrderNo, o.GetEffectiveStatus(time.GetUtcNow().UtcDateTime),
            o.SubTotal, o.DiscountAmount, o.ShippingFee, o.Total, o.PayableAmount,
            o.CreatedAt, o.ExpiresAt, o.PaidAt, o.PromoCode?.Code,
            new OrderContactResponse(o.ContactName, o.Phone, o.Email),
            new OrderShippingResponse(o.ShippingOption.Type, o.ShippingOption.Name,
                isPost
                    ? new OrderAddressResponse(o.ShipName, o.ShipPhone, o.ShipAddress, o.ShipSubdistrictText,
                        o.ShipDistrictText, o.ShipProvinceText, o.ShipPostalCode)
                    : null,
                o.TrackingNo),
            o.Registrations.OrderBy(r => r.Id).Select(r => new OrderRunnerResponse(
                r.Id, r.TitleId, r.FirstName, r.LastName,
                new IdName(r.EventDistance.Id, r.EventDistance.Name),
                new ShirtSizeRef(r.EventShirtSize.Id, r.EventShirtSize.Size),
                r.PricePaid, r.BibNumber)).ToList());
    }

    [HttpGet("tickets")]
    public async Task<IReadOnlyList<TicketResponse>> GetTickets(Guid orderId, CancellationToken ct)
    {
        var o = await LoadAsync(orderId, ct);
        var status = o.GetEffectiveStatus(time.GetUtcNow().UtcDateTime);
        if (status != OrderStatus.Paid) throw OrderErrors.OrderNotPaid(status);
        return o.Registrations.Where(r => !r.IsCancelled).OrderBy(r => r.Id)
            .Select(r => new TicketResponse(r.Id, r.FirstName, r.LastName, r.EventDistance.Name, r.BibNumber!, r.CheckInToken))
            .ToList();
    }

    private async Task<Order> LoadAsync(Guid orderId, CancellationToken ct) =>
        await db.Orders.AsNoTracking()
            .Include(o => o.ShippingOption)
            .Include(o => o.PromoCode)
            .Include(o => o.Registrations).ThenInclude(r => r.EventDistance)
            .Include(o => o.Registrations).ThenInclude(r => r.EventShirtSize)
            .AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
        ?? throw OrderErrors.OrderNotFound(orderId);
}
```

- [ ] **Step 5: รัน test ให้ผ่าน**

Run: `dotnet test --filter FullyQualifiedName~OrderQueryTests`
Expected: PASS 5 tests

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(api): order status and tickets endpoints"
```

---

### Task 9: Contract docs, full suite, real-DB smoke

**Files:**
- Modify: `docs/api-contract.md`

- [ ] **Step 1: อัปเดต `docs/api-contract.md`**

แทนที่แถว `| GET | \`/orders/{orderId}/tickets\` | 6 | เลขบิบ + QR เช็กอินของแต่ละคน (เมื่อ \`Paid\`) |` ด้วย:

```markdown
| GET | `/orders/{orderId}/tickets` | 6 | เลขบิบ + `checkInToken` (หน้าเว็บสร้าง QR เอง) เฉพาะ `Paid` ไม่งั้น `409 ORDER_NOT_PAID` |
```

และเพิ่มย่อหน้าใต้หัวข้อ `### POST /events/{slug}/orders` ต่อจากบรรทัด "ฝั่งเซิร์ฟเวอร์ต้อง: ...":

```markdown
สถานะที่ implement แล้ว (2026-10-11): validate/promo/create/GET order/tickets + job หมดเวลา ทุก 1 นาที
error เพิ่มเติม: `409 PAYMENT_SLOT_BUSY` (เศษสตางค์ของยอดนี้เต็ม 99 ช่อง), `404 ORDER_NOT_FOUND`
`GET /orders/{id}` ไม่ส่ง `medicalNote`, ผู้ติดต่อฉุกเฉิน, `checkInToken`; `status` แสดง `Expired` ทันทีเมื่อเลย `expiresAt`
ยังไม่ทำ: `GET /orders/{id}/payment`, `POST /orders/{id}/payment/slip` (รอเลือกบริการตรวจสลิป)
```

- [ ] **Step 2: รัน test ทั้งชุด**

Run: `dotnet test`
Expected: PASS ทั้งหมด 0 failed

- [ ] **Step 3: migration บน DB จริงและ smoke test**

```bash
dotnet ef database update -p src/HuebRun.Infrastructure -s src/HuebRun.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/HuebRun.Api --no-launch-profile --urls http://localhost:5080
```

อีก terminal:

```bash
curl -s localhost:5080/api/v1/events/hueb-run-2027/distances
curl -s -X POST localhost:5080/api/v1/events/hueb-run-2027/orders -H 'Content-Type: application/json' -d '{"contact":{"name":"ทดสอบ ระบบ","phone":"0812345678"},"runners":[{"distanceId":1,"shirtSizeId":3,"firstName":"ทดสอบ","lastName":"ระบบ","gender":"Male","birthDate":"1990-01-01","nationalityId":1,"emergencyContact":{"name":"ผู้ติดต่อ","phone":"0899999999"}}],"shippingOptionId":1,"acceptedPdpa":true}'
curl -s localhost:5080/api/v1/orders/<orderId จากผลด้านบน>
```

Expected: `Applying migration '..._OrderRegistration'` → `Done.`; POST ได้ `201` พร้อม `orderNo` `HR27-...` และ `payableAmount` = 390.01 (early bird 3K); GET ได้ `Pending`; distances แสดง `remaining` ของ Mini 3K ลดลง 1 (ออเดอร์ทดสอบนี้จะหมดเวลาและคืนที่เองใน 30 นาที — worker เปิดอยู่ใน Development)

- [ ] **Step 4: ปิดแอปและ commit**

```bash
git add docs/api-contract.md
git commit -m "docs: update API contract for order registration"
```
