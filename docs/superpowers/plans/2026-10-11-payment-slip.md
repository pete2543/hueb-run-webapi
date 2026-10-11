# Payment Slip Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** นักวิ่งเห็น QR PromptPay ของออเดอร์ อัปโหลดสลิป ระบบตรวจกับ SlipOK เก็บรูปใน MinIO แล้วออเดอร์เป็น `Paid` + ออกเลขบิบอัตโนมัติ สลิปที่ตรวจทันทีไม่ได้ถูก job ตรวจซ้ำ

**Architecture:** QR payload เป็น pure class ใน Domain (`PromptPayPayload`) · งานทั้งหมดอยู่ใน `Infrastructure/Payments` (`PaymentService`, `PaymentRecheckService`) โดยคุยกับภายนอกผ่าน `ISlipVerifier` (SlipOK) และ `ISlipStorage` (MinIO) · Api มี `PaymentController` บาง ๆ + `PaymentRecheckWorker` · ระหว่างตรวจ ยืด `Order.ExpiresAt` (hold) เพื่อไม่ให้ job หมดเวลาคืนที่

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, `Minio` 6.0.5, typed `HttpClient`, xUnit, Testcontainers (PostgreSql + Minio), FakeTimeProvider

**Spec:** `docs/superpowers/specs/2026-10-11-payment-slip-design.md`

## Global Constraints

- ทำบน branch `feat/payment` (แตกจาก `feat/order-registration`)
- Base path `/api/v1`; error body `{ code, message, details }`; error ทางธุรกิจโยนเป็น `AppException` (Domain) ผ่าน factory ใน `PaymentErrors`/`OrderErrors`
- Infrastructure ห้ามอ้าง Api; เวลา UTC; เงิน `decimal` 2 ตำแหน่ง
- secret (`SlipOk:BranchId`, `SlipOk:ApiKey`, `Minio:AccessKey`, `Minio:SecretKey`) ห้ามอยู่ใน `appsettings*.json` หรือ git — dev ใช้ user-secrets
- นามสกุลที่รับ: `.jpg .jpeg .png .webp` (SlipOK ไม่รับ gif/HEIC) และต้องผ่าน magic bytes
- `VerificationHoldMinutes = 90`, `RejectGraceMinutes = 10`, ตรวจซ้ำสูงสุด 6 ครั้ง หรือ 60 นาทีนับจากสร้าง Payment, backoff `Unavailable` = 1, 2, 5, 10, 15, 30 นาที, 1009 → 15 นาที
- ต้องเปิด Docker ตอนรัน test
- **Deviation จาก spec §5.3 (ruling ตอนวางแผน):** อัปโหลด MinIO **ภายใน transaction A** หลัง insert Payment แล้วจึง commit — ถ้าอัปโหลดล้ม transaction rollback เอง ไม่ต้องลบ Payment ทีหลัง (ผลเหมือนที่ spec ต้องการ: ไม่มี Payment ค้าง)
- **Deviation จาก spec §5.2:** Payment ใหม่ตั้ง `NextCheckAt = now + 5 นาที` (ไม่ใช่ null) — เป็นทั้งการจองงานให้ request ปัจจุบัน และตาข่ายกันพลาด: ถ้าแอปตายก่อนตรวจ job จะหยิบไปตรวจเองหลัง 5 นาที

## Review Focus

1. อัปโหลดสลิปตอนออเดอร์ใกล้หมดเวลา ขณะ job หมดเวลากำลังรัน → ต้องไม่มีออเดอร์ `Expired` ที่มี Payment `Verified` — test ใน Task 7
2. แอปตาย/ลูกค้าปิดหน้าเว็บหลัง Payment ถูกบันทึกแต่ก่อนตรวจเสร็จ → job ตรวจต่อให้เมื่อพ้นช่วงจอง — test ใน Task 8
3. ไฟล์ gif/pdf ที่ถูกเปลี่ยนชื่อเป็น `.jpg` → `400 VALIDATION_ERROR` ไม่ส่งไป SlipOK — test ใน Task 6
4. SlipOK/proxy ตอบ 502 เป็น HTML ไม่ใช่ JSON → `Unavailable` (ตรวจซ้ำทีหลัง) ไม่ใช่ 500 — test ใน Task 5
5. นามสกุลตัวพิมพ์ใหญ่ (`SLIP.JPG` จากมือถือ) → รับได้ — test ใน Task 6

---

## File Structure

```
src/HuebRun.Domain/
  PromptPayPayload.cs                 QR PromptPay (EMVCo + CRC16) pure
  Entities.cs                         (Payment: SlipObjectKey, TransferredAt, NextCheckAt, CheckAttempts; PaymentProvider, PaymentRejectReason)
src/HuebRun.Infrastructure/
  AppDbContext.cs                     (+ index Payment)
  DependencyInjection.cs              (+ options, HttpClient, MinIO, payment services)
  HuebRun.Infrastructure.csproj       (+ Minio 6.0.5)
  Migrations/*_PaymentSlip.cs         (generated + RenameColumn)
  Orders/OrderPaymentService.cs       (ใช้ transaction ที่เปิดอยู่ได้)
  Orders/OrderExpiryService.cs        (+ เงื่อนไข ExpiresAt <= now ตอน update)
  Orders/OrderService.cs              (ยอด 0 → Paid ทันที)
  Payments/SlipCheckResult.cs         ผลตรวจ + ISlipVerifier + SlipImage
  Payments/SlipOkOptions.cs
  Payments/SlipOkVerifier.cs          เรียก SlipOK แปลง error code
  Payments/MinioOptions.cs
  Payments/ISlipStorage.cs            + MinioSlipStorage
  Payments/SlipFile.cs                validate นามสกุล/ขนาด/magic bytes
  Payments/PaymentOptions.cs
  Payments/PaymentErrors.cs
  Payments/PaymentModels.cs           PaymentInfo, PaymentSummary, SlipSubmission
  Payments/PaymentService.cs          GetPaymentInfo, SubmitSlip, ApplyResult
  Payments/PaymentRecheckService.cs   ตรวจซ้ำ Payment ที่ถึงเวลา
src/HuebRun.Api/
  Controllers/PaymentController.cs
  Controllers/OrdersController.cs     (+ payment ล่าสุด)
  Contracts/OrderContracts.cs         (+ OrderPaymentResponse)
  Jobs/PaymentRecheckWorker.cs
  Program.cs                          (+ worker)
  appsettings.json / appsettings.Development.json
tests/HuebRun.Tests/
  HuebRun.Tests.csproj                (+ Testcontainers.Minio)
  Infrastructure/ApiFactory.cs        (+ config ทดสอบ, fake verifier/storage)
  Infrastructure/FakeSlipVerifier.cs
  Infrastructure/InMemorySlipStorage.cs
  Infrastructure/Slips.cs             สร้างรูปปลอมที่ hash ไม่ซ้ำ
  Domain/PromptPayPayloadTests.cs
  Data/PaymentSchemaTests.cs
  Data/OrderPaymentServiceTests.cs    (+ transaction ภายนอก)
  Data/FreeOrderTests.cs
  Data/SlipOkVerifierTests.cs
  Data/SlipFileTests.cs
  Data/MinioSlipStorageTests.cs
  Data/PaymentServiceTests.cs
  Data/PaymentRecheckTests.cs
  Api/PaymentEndpointTests.cs
docs/api-contract.md                  (endpoint payment + error code)
```

---

### Task 1: QR PromptPay payload

**Files:**
- Create: `src/HuebRun.Domain/PromptPayPayload.cs`
- Test: `tests/HuebRun.Tests/Domain/PromptPayPayloadTests.cs`

**Interfaces:**
- Produces: `PromptPayPayload.Create(string promptPayId, decimal amount) : string`, `PromptPayPayload.Crc16(string data) : ushort`

- [ ] **Step 1: Write the failing test**

```csharp
using HuebRun.Domain;

namespace HuebRun.Tests.Domain;

public class PromptPayPayloadTests
{
    [Fact]
    public void Crc16_matches_ccitt_false_check_value() =>
        Assert.Equal(0x29B1, PromptPayPayload.Crc16("123456789"));

    [Fact]
    public void Phone_number_payload_matches_reference()
    {
        // อ้างอิง promptpay-qr: generatePayload('0801234567', { amount: 4.22 })
        Assert.Equal("00020101021229370016A000000677010111011300668012345675802TH530376454044.226304E469",
            PromptPayPayload.Create("080-123-4567", 4.22m));
    }

    [Fact]
    public void National_id_uses_tag_02_and_two_decimal_amount()
    {
        var payload = PromptPayPayload.Create("1234567890123", 400.01m);
        Assert.Contains("29370016A00000067701011102131234567890123", payload);
        Assert.Contains("5406400.01", payload);
        Assert.Equal(PromptPayPayload.Crc16(payload[..^4]).ToString("X4"), payload[^4..]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1812345678")]
    public void Invalid_id_throws(string id) =>
        Assert.Throws<InvalidOperationException>(() => PromptPayPayload.Create(id, 100m));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~PromptPayPayloadTests`
Expected: FAIL — build error `PromptPayPayload` does not exist

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Globalization;
using System.Text;

namespace HuebRun.Domain;

/// string QR PromptPay แบบ dynamic (EMVCo) ที่ระบุยอด หน้าเว็บนำไปวาด QR เอง
public static class PromptPayPayload
{
    private const string Aid = "A000000677010111";

    public static string Create(string promptPayId, decimal amount)
    {
        var merchant = Tlv("00", Aid) + Target(promptPayId);
        var body = Tlv("00", "01") + Tlv("01", "12") + Tlv("29", merchant) + Tlv("58", "TH") + Tlv("53", "764")
                 + Tlv("54", amount.ToString("0.00", CultureInfo.InvariantCulture)) + "6304";
        return body + Crc16(body).ToString("X4");
    }

    // เบอร์โทร 10 หลัก → 0066 + 9 หลัก; เลขบัตร/ภาษี 13 หลัก; e-wallet 15 หลัก
    private static string Target(string id)
    {
        var digits = new string(id.Where(char.IsAsciiDigit).ToArray());
        return digits.Length switch
        {
            10 when digits[0] == '0' => Tlv("01", "0066" + digits[1..]),
            13 => Tlv("02", digits),
            15 => Tlv("03", digits),
            _ => throw new InvalidOperationException($"Invalid PromptPay ID (digits: {digits.Length})"),
        };
    }

    private static string Tlv(string tag, string value) => tag + value.Length.ToString("D2") + value;

    /// CRC-16/CCITT-FALSE (init 0xFFFF, poly 0x1021)
    public static ushort Crc16(string data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in Encoding.ASCII.GetBytes(data))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~PromptPayPayloadTests`
Expected: PASS (5 tests). ถ้า `Phone_number_payload_matches_reference` ล้มที่ 4 ตัวท้ายเท่านั้น ให้ตรวจ CRC ด้วย `Crc16_matches_ccitt_false_check_value` ก่อน — ถ้า check value ผ่าน แสดงว่าค่าอ้างอิงที่จำมาผิด ให้แทนด้วยค่าจาก `npx promptpay-qr 0801234567 4.22` แล้วบันทึกใน commit message

- [ ] **Step 5: Commit**

```bash
git add src/HuebRun.Domain/PromptPayPayload.cs tests/HuebRun.Tests/Domain/PromptPayPayloadTests.cs
git commit -m "feat(domain): PromptPay QR payload with CRC16"
```

---

### Task 2: Schema ของ Payment

**Files:**
- Modify: `src/HuebRun.Domain/Entities.cs` (class `Payment` ราวบรรทัด 212–235)
- Modify: `src/HuebRun.Infrastructure/AppDbContext.cs:177-181`
- Create: `src/HuebRun.Infrastructure/Migrations/*_PaymentSlip.cs` (generated)
- Test: `tests/HuebRun.Tests/Data/PaymentSchemaTests.cs`

**Interfaces:**
- Produces: `Payment.SlipObjectKey`, `Payment.TransferredAt`, `Payment.NextCheckAt`, `Payment.CheckAttempts`; `PaymentProvider.SlipOk = "SlipOK"`, `PaymentProvider.Free = "Free"`; `PaymentRejectReason.{InvalidSlip, AmountMismatch, WrongReceiver, DuplicateSlip, OrderExpired, DuplicateUnknown, VerifyTimeout}`; ชื่อ index `IX_Payments_OrderId_Verifying` (Task 7 ใช้แยก error ตอนชน unique)

- [ ] **Step 1: Write the failing test**

```csharp
using HuebRun.Domain;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class PaymentSchemaTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private static Payment Verifying(Guid orderId, string hash) => new()
    {
        OrderId = orderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.Verifying,
        ExpectedAmount = 400.01m, SlipHash = hash, CreatedAt = TestData.Utc(2026, 11, 15),
        NextCheckAt = TestData.Utc(2026, 11, 15), SlipObjectKey = "slips/x.png",
    };

    [Fact]
    public async Task Order_can_have_only_one_verifying_payment()
    {
        var created = await CreateOrderOk();
        await using var db = Pg.CreateDbContext();
        db.Payments.Add(Verifying(created.OrderId, Guid.NewGuid().ToString()));
        await db.SaveChangesAsync();

        db.Payments.Add(Verifying(created.OrderId, Guid.NewGuid().ToString()));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("IX_Payments_OrderId_Verifying", ((PostgresException)ex.InnerException!).ConstraintName);
    }

    [Fact]
    public async Task Rejected_payments_do_not_block_a_new_verifying_one()
    {
        var created = await CreateOrderOk();
        await using var db = Pg.CreateDbContext();
        var first = Verifying(created.OrderId, Guid.NewGuid().ToString());
        first.Status = PaymentStatus.Rejected;
        db.Payments.Add(first);
        db.Payments.Add(Verifying(created.OrderId, Guid.NewGuid().ToString()));
        await db.SaveChangesAsync();
        Assert.Equal(0, (await db.Payments.SingleAsync(p => p.Id == first.Id)).CheckAttempts);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~PaymentSchemaTests`
Expected: FAIL — build error (`PaymentProvider`, `SlipObjectKey`, `NextCheckAt` ไม่มี)

- [ ] **Step 3: แก้ entity**

ใน `src/HuebRun.Domain/Entities.cs` แทน class `Payment` ทั้งก้อนด้วย:

```csharp
// ---------- Payment (รองรับหลาย provider: Slip / Gateway) ----------
public class Payment
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public string Provider { get; set; } = "";         // PaymentProvider.*
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public decimal ExpectedAmount { get; set; }
    public decimal? PaidAmount { get; set; }

    public string? SlipObjectKey { get; set; }         // key ใน object storage (ไม่เก็บ URL)
    public string? SlipHash { get; set; }              // SHA-256 ของไฟล์ กันรูปซ้ำ
    public string? SlipTransRef { get; set; }          // เลขอ้างอิงธุรกรรมจากสลิป (unique)
    public DateTime? TransferredAt { get; set; }       // เวลาโอนตามสลิป
    public string? ExternalRef { get; set; }           // charge id ของ gateway (อนาคต)
    public string? ProviderResponse { get; set; }      // jsonb เก็บผลตรวจดิบครั้งล่าสุด

    public DateTime? NextCheckAt { get; set; }         // Verifying: เวลาที่ job ควรตรวจซ้ำ
    public int CheckAttempts { get; set; }

    public string? RejectReason { get; set; }          // PaymentRejectReason.*
    public string? ReviewedBy { get; set; }            // แอดมินที่ตรวจมือ
    public DateTime CreatedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
}

public static class PaymentProvider
{
    public const string SlipOk = "SlipOK";
    public const string Free = "Free";
}

/// เหตุผลที่ Payment ถูกปฏิเสธหรือต้องตรวจมือ (ใช้ค่าเดียวกันใน API)
public static class PaymentRejectReason
{
    public const string InvalidSlip = "INVALID_SLIP";
    public const string AmountMismatch = "AMOUNT_MISMATCH";
    public const string WrongReceiver = "WRONG_RECEIVER";
    public const string DuplicateSlip = "DUPLICATE_SLIP";
    public const string OrderExpired = "ORDER_EXPIRED";
    public const string DuplicateUnknown = "DUPLICATE_UNKNOWN";
    public const string VerifyTimeout = "VERIFY_TIMEOUT";
}
```

- [ ] **Step 4: เพิ่ม index**

ใน `src/HuebRun.Infrastructure/AppDbContext.cs` ต่อจากบรรทัด `b.Entity<Payment>().HasIndex(x => new { x.OrderId, x.Status });`:

```csharp
        // ออเดอร์มีสลิปรอตรวจได้ทีละใบ
        b.Entity<Payment>().HasIndex(x => x.OrderId, "IX_Payments_OrderId_Verifying")
            .IsUnique().HasFilter("\"Status\" = 'Verifying'");
        // ให้ job หา Payment ที่ถึงเวลาตรวจซ้ำเร็ว
        b.Entity<Payment>().HasIndex(x => x.NextCheckAt).HasFilter("\"Status\" = 'Verifying'");
```

- [ ] **Step 5: สร้าง migration แล้วแก้ให้เป็น rename**

Run:
```bash
dotnet ef migrations add PaymentSlip -p src/HuebRun.Infrastructure -s src/HuebRun.Api -o Migrations
grep -n "SlipImageUrl\|SlipObjectKey" src/HuebRun.Infrastructure/Migrations/*_PaymentSlip.cs
```
ถ้าไฟล์มี `DropColumn(name: "SlipImageUrl"...)` + `AddColumn<string>(name: "SlipObjectKey"...)` ให้แทนสองคำสั่งนั้นใน `Up` ด้วย:
```csharp
            migrationBuilder.RenameColumn(name: "SlipImageUrl", table: "Payments", newName: "SlipObjectKey");
```
และใน `Down` แทนคู่กลับด้วย:
```csharp
            migrationBuilder.RenameColumn(name: "SlipObjectKey", table: "Payments", newName: "SlipImageUrl");
```
(ถ้า EF สร้าง `RenameColumn` ให้เองแล้วไม่ต้องแก้)

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/HuebRun.Tests --filter "FullyQualifiedName~PaymentSchemaTests|FullyQualifiedName~SchemaTests"`
Expected: PASS (รวม `All_migrations_are_applied`)

- [ ] **Step 7: Commit**

```bash
git add src/HuebRun.Domain/Entities.cs src/HuebRun.Infrastructure/AppDbContext.cs src/HuebRun.Infrastructure/Migrations tests/HuebRun.Tests/Data/PaymentSchemaTests.cs
git commit -m "feat(infra): payment slip columns and one-verifying-per-order index"
```

---

### Task 3: `MarkPaidAsync` ใช้ transaction ร่วม + expiry ไม่ทับ hold

**Files:**
- Modify: `src/HuebRun.Infrastructure/Orders/OrderPaymentService.cs:13-47`
- Modify: `src/HuebRun.Infrastructure/Orders/OrderExpiryService.cs:25-49`
- Test: `tests/HuebRun.Tests/Data/OrderPaymentServiceTests.cs`, `tests/HuebRun.Tests/Data/OrderExpiryTests.cs`

**Interfaces:**
- Produces: `OrderPaymentService.MarkPaidAsync(Guid orderId, DateTime paidAtUtc, CancellationToken ct = default)` — signature เดิม; ถ้า `db.Database.CurrentTransaction != null` ใช้ตัวนั้นและไม่ commit เอง
- Produces: `OrderExpiryService` update ออเดอร์เฉพาะเมื่อ `ExpiresAt <= now` ณ ตอน update

- [ ] **Step 1: Write the failing tests**

เพิ่มใน `OrderPaymentServiceTests`:

```csharp
    [Fact]
    public async Task Joins_an_outer_transaction_and_rolls_back_with_it()
    {
        var created = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await new OrderPaymentService(db).MarkPaidAsync(created.OrderId, PaidAt);
            Assert.Same(tx, db.Database.CurrentTransaction);   // ไม่ได้ commit/ปิด transaction ของคนเรียก
            await tx.RollbackAsync();
        }

        Assert.Equal([null], await Bibs(created.OrderId));
        await using var check = Pg.CreateDbContext();
        Assert.Equal(OrderStatus.Pending, (await check.Orders.SingleAsync(o => o.Id == created.OrderId)).Status);
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).LastBibNumber);
    }
```

เพิ่มใน `OrderExpiryTests`:

```csharp
    [Fact]
    public async Task Order_whose_expiry_was_extended_after_selection_is_not_expired()
    {
        // ออเดอร์ถูกยืดเวลา (hold) ไปแล้ว → job ที่รันหลังเวลาเดิมต้องไม่แตะ; การแข่งจริงทดสอบใน Task 7
        var created = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
            await db.Orders.Where(o => o.Id == created.OrderId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, TestData.Utc(2026, 11, 15).AddMinutes(120)));

        await RunExpiry(AfterExpiry);

        Assert.Equal(OrderStatus.Pending, (await Snapshot(created.OrderId)).Status);
    }
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/HuebRun.Tests --filter "FullyQualifiedName~Joins_an_outer_transaction|FullyQualifiedName~expiry_was_extended"`
Expected: `Joins_an_outer_transaction...` FAIL (เปิด transaction ซ้อนไม่ได้ / commit ไปแล้ว); test expiry ผ่านอยู่แล้วเพราะ batch query กรองไว้ — เป็น regression guard ส่วนการแข่งจริงทดสอบใน Task 7

- [ ] **Step 3: แก้ `MarkPaidAsync`**

ใน `OrderPaymentService.MarkPaidAsync` แทนบรรทัด `await using var tx = await db.Database.BeginTransactionAsync(ct);` ด้วย:

```csharp
        // ใช้ transaction ของคนเรียกถ้ามี (เช่น อัปเดต Payment พร้อมกัน) ไม่งั้นเปิดเอง
        var outer = db.Database.CurrentTransaction;
        await using var owned = outer is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var tx = outer ?? owned!;
```

และแทนบรรทัดท้าย `await tx.CommitAsync(ct);` ด้วย:

```csharp
        if (owned is not null) await owned.CommitAsync(ct);
```

แก้ doc comment ของ class เป็น:

```csharp
/// เปลี่ยนออเดอร์เป็น Paid และออกเลขบิบ — ขั้นชำระเงินเรียกใช้ (ร่วม transaction ที่เปิดอยู่ได้)
```

- [ ] **Step 4: แก้ `OrderExpiryService`**

ใน `ExpireDueOrdersAsync` เปลี่ยน `if (await ExpireAsync(orderId, ct)) expired++;` เป็น `if (await ExpireAsync(orderId, now, ct)) expired++;`

แทน signature และ update แรกของ `ExpireAsync` ด้วย:

```csharp
    private async Task<bool> ExpireAsync(Guid orderId, DateTime now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // update แบบมีเงื่อนไข: ถ้าเพิ่งถูกจ่าย, ถูก job อีกตัวทำไปแล้ว หรือถูกยืดเวลา (รอตรวจสลิป) จะไม่คืนซ้ำ
        var updated = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending && o.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired), ct);
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/HuebRun.Tests --filter "FullyQualifiedName~OrderPaymentServiceTests|FullyQualifiedName~OrderExpiryTests"`
Expected: PASS ทั้งหมด

- [ ] **Step 6: Commit**

```bash
git add src/HuebRun.Infrastructure/Orders tests/HuebRun.Tests/Data/OrderPaymentServiceTests.cs tests/HuebRun.Tests/Data/OrderExpiryTests.cs
git commit -m "feat(infra): mark-paid joins outer transaction; expiry respects extended deadline"
```

---

### Task 4: ออเดอร์ยอด 0 บาทเป็น Paid ทันที

**Files:**
- Modify: `src/HuebRun.Infrastructure/Orders/OrderService.cs` (`TryCreateOrderAsync` ราวบรรทัด 77–113)
- Test: `tests/HuebRun.Tests/Data/FreeOrderTests.cs`

**Interfaces:**
- Consumes: `OrderPaymentService.MarkPaidAsync` (ร่วม transaction) จาก Task 3; `PaymentProvider.Free` จาก Task 2
- Produces: `CreatedOrder.PayableAmount == 0` สำหรับออเดอร์ฟรี; ออเดอร์มี Payment `Free`/`Verified`

- [ ] **Step 1: Write the failing test**

```csharp
using HuebRun.Domain;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class FreeOrderTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private Task UseFullDiscount() => AddPromoAsync(p => { p.Code = "FREE100"; p.Value = 100; });

    [Fact]
    public async Task Zero_total_order_is_paid_immediately_with_bib_and_free_payment()
    {
        await UseFullDiscount();
        var created = await CreateOrderOk(Request() with { PromoCode = "FREE100" });

        Assert.Equal(0m, created.PayableAmount);
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.Include(o => o.Registrations).Include(o => o.Payments).SingleAsync(o => o.Id == created.OrderId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal("B0001", order.Registrations.Single().BibNumber);
        var payment = order.Payments.Single();
        Assert.Equal((PaymentProvider.Free, PaymentStatus.Verified, 0m), (payment.Provider, payment.Status, payment.ExpectedAmount));
    }

    [Fact]
    public async Task Concurrent_free_orders_all_succeed()
    {
        await UseFullDiscount();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostOrder(Request() with { PromoCode = "FREE100" })));
        Assert.All(results, r => Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode));
    }

    [Fact]
    public async Task Paid_shipping_keeps_normal_flow()
    {
        await UseFullDiscount();
        var created = await CreateOrderOk(Request() with
        {
            PromoCode = "FREE100", ShippingOptionId = Post.Id,
            ShippingAddress = await TestAddressAsync(),
        });
        Assert.Equal(60.01m, created.PayableAmount);
    }

    private async Task<HuebRun.Infrastructure.Orders.AddressInput> TestAddressAsync()
    {
        await using var db = Pg.CreateDbContext();
        var sub = await db.Subdistricts.OrderBy(s => s.Id).FirstAsync();
        return new("สมชาย", "0812345678", "1 ถนนสุขุมวิท", sub.Id, sub.ZipCode);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~FreeOrderTests`
Expected: FAIL — `Zero_total...` ได้ `PayableAmount = 0.01` และ status `Pending`

- [ ] **Step 3: Implement**

ใน `TryCreateOrderAsync` แทนบรรทัด `var payable = await PickPayableAmountAsync(ev.Id, price.Total, ct);` ด้วย:

```csharp
        // ยอด 0 (โค้ดฟรี + รับเอง) ไม่ต้องโอน → ไม่ใช้เศษสตางค์ และจ่ายแล้วทันที
        var isFree = price.Total == 0;
        var payable = isFree ? 0m : await PickPayableAmountAsync(ev.Id, price.Total, ct);
```

และแทนสามบรรทัด

```csharp
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
```

ด้วย:

```csharp
        if (isFree)
            order.Payments.Add(new Payment
            {
                Provider = PaymentProvider.Free, Status = PaymentStatus.Verified,
                ExpectedAmount = 0, PaidAmount = 0, CreatedAt = now, VerifiedAt = now,
            });
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        if (isFree) await new OrderPaymentService(db).MarkPaidAsync(order.Id, now, ct);
        await tx.CommitAsync(ct);
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/HuebRun.Tests --filter "FullyQualifiedName~FreeOrderTests|FullyQualifiedName~CreateOrder"`
Expected: PASS ทั้งหมด

- [ ] **Step 5: Commit**

```bash
git add src/HuebRun.Infrastructure/Orders/OrderService.cs tests/HuebRun.Tests/Data/FreeOrderTests.cs
git commit -m "feat(infra): zero-total orders are paid on creation"
```

---

### Task 5: ตัวตรวจสลิป SlipOK

**Files:**
- Create: `src/HuebRun.Infrastructure/Payments/SlipCheckResult.cs`
- Create: `src/HuebRun.Infrastructure/Payments/SlipOkOptions.cs`
- Create: `src/HuebRun.Infrastructure/Payments/SlipOkVerifier.cs`
- Test: `tests/HuebRun.Tests/Data/SlipOkVerifierTests.cs`

**Interfaces:**
- Produces:
  - `record SlipImage(byte[] Content, string Extension, string ContentType)` + `string Sha256`
  - `interface ISlipVerifier { Task<SlipCheckResult> VerifyAsync(SlipImage image, decimal expectedAmount, CancellationToken ct); }`
  - `abstract record SlipCheckResult(string? RawJson)` ชนิดย่อย `Verified(string TransRef, decimal Amount, DateTime? TransferredAt, string Raw)`, `Duplicate(string? TransRef, string Raw)`, `Rejected(string Reason, string Raw)`, `Retry(TimeSpan After, string Raw)`, `Unavailable(string? Raw)`
  - `SlipOkOptions { BaseUrl, BranchId, ApiKey, TimeoutSeconds }`
  - `SlipOkVerifier(HttpClient http, IOptions<SlipOkOptions> options, ILogger<SlipOkVerifier> logger)`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using System.Text;
using HuebRun.Domain;
using HuebRun.Infrastructure.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HuebRun.Tests.Data;

public class SlipOkVerifierTests
{
    private sealed class StubHandler(HttpStatusCode status, string body, string contentType = "application/json") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public Exception? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (Throw is not null) throw Throw;
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        }
    }

    private static readonly SlipImage Image = new([0x89, 0x50, 0x4E, 0x47, 1, 2, 3], ".png", "image/png");

    private static async Task<(SlipCheckResult Result, StubHandler Handler)> Verify(StubHandler handler, decimal amount = 400.01m)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.slipok.test/") };
        var options = Options.Create(new SlipOkOptions { BaseUrl = "https://api.slipok.test", BranchId = "78336", ApiKey = "KEY" });
        var result = await new SlipOkVerifier(http, options, NullLogger<SlipOkVerifier>.Instance).VerifyAsync(Image, amount, default);
        return (result, handler);
    }

    private const string OkBody = """
        { "success": true, "data": { "success": true, "message": "✅", "transRef": "0100921015076",
          "transTimestamp": "2026-11-15T00:10:07.000Z", "amount": 400.01 } }
        """;

    [Fact]
    public async Task Sends_file_log_amount_and_api_key()
    {
        var (_, h) = await Verify(new StubHandler(HttpStatusCode.OK, OkBody));
        Assert.Equal("https://api.slipok.test/api/line/apikey/78336", h.Request!.RequestUri!.ToString());
        Assert.Equal("KEY", h.Request.Headers.GetValues("x-authorization").Single());
        Assert.Contains("name=files", h.RequestBody);
        Assert.Contains("name=log", h.RequestBody);
        Assert.Contains("true", h.RequestBody);
        Assert.Contains("400.01", h.RequestBody);
    }

    [Fact]
    public async Task Success_is_verified_with_slip_data()
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.OK, OkBody));
        var v = Assert.IsType<SlipCheckResult.Verified>(r);
        Assert.Equal(("0100921015076", 400.01m), (v.TransRef, v.Amount));
        Assert.Equal(new DateTime(2026, 11, 15, 0, 10, 7, DateTimeKind.Utc), v.TransferredAt);
        Assert.Equal(DateTimeKind.Utc, v.TransferredAt!.Value.Kind);
        Assert.Contains("0100921015076", v.RawJson);
    }

    [Theory]
    [InlineData(1013, PaymentRejectReason.AmountMismatch)]
    [InlineData(1014, PaymentRejectReason.WrongReceiver)]
    [InlineData(1000, PaymentRejectReason.InvalidSlip)]
    [InlineData(1005, PaymentRejectReason.InvalidSlip)]
    [InlineData(1006, PaymentRejectReason.InvalidSlip)]
    [InlineData(1007, PaymentRejectReason.InvalidSlip)]
    [InlineData(1008, PaymentRejectReason.InvalidSlip)]
    [InlineData(1011, PaymentRejectReason.InvalidSlip)]
    public async Task Slip_errors_are_rejected(int code, string reason)
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.BadRequest, $$"""{ "code": {{code}}, "message": "x" }"""));
        Assert.Equal(reason, Assert.IsType<SlipCheckResult.Rejected>(r).Reason);
    }

    [Fact]
    public async Task Duplicate_carries_trans_ref_from_data()
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.BadRequest,
            """{ "code": 1012, "message": "สลิปซ้ำ", "data": { "success": true, "transRef": "T-1" } }"""));
        Assert.Equal("T-1", Assert.IsType<SlipCheckResult.Duplicate>(r).TransRef);
    }

    [Fact]
    public async Task Bank_delay_retries_after_given_minutes()
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.BadRequest,
            """{ "code": 1010, "message": "รอ", "data": { "bankCode": "002", "delay": 8 } }"""));
        Assert.Equal(TimeSpan.FromMinutes(8), Assert.IsType<SlipCheckResult.Retry>(r).After);
    }

    [Fact]
    public async Task Bank_outage_retries_after_15_minutes()
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.BadRequest, """{ "code": 1009, "message": "x" }"""));
        Assert.Equal(TimeSpan.FromMinutes(15), Assert.IsType<SlipCheckResult.Retry>(r).After);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": 1002, "message": "Authorization Header ไม่ถูกต้อง" }""")]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": 1003, "message": "หมดอายุ" }""")]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": 1004, "message": "เกินโควตา" }""")]
    [InlineData(HttpStatusCode.InternalServerError, """{ "message": "boom" }""")]
    public async Task Configuration_or_server_errors_are_unavailable(HttpStatusCode status, string body)
    {
        var (r, _) = await Verify(new StubHandler(status, body));
        Assert.IsType<SlipCheckResult.Unavailable>(r);
    }

    [Fact]
    public async Task Non_json_gateway_error_is_unavailable()
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>", "text/html"));
        var u = Assert.IsType<SlipCheckResult.Unavailable>(r);
        Assert.Null(u.RawJson);   // ไม่ใช่ JSON เก็บลง jsonb ไม่ได้
    }

    [Fact]
    public async Task Network_failure_and_timeout_are_unavailable()
    {
        Assert.IsType<SlipCheckResult.Unavailable>((await Verify(new StubHandler(HttpStatusCode.OK, "") { Throw = new HttpRequestException("down") })).Result);
        Assert.IsType<SlipCheckResult.Unavailable>((await Verify(new StubHandler(HttpStatusCode.OK, "") { Throw = new TaskCanceledException("timeout") })).Result);
    }

    [Fact]
    public void Sha256_is_lowercase_hex() =>
        Assert.Matches("^[0-9a-f]{64}$", Image.Sha256);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~SlipOkVerifierTests`
Expected: FAIL — build error namespace `HuebRun.Infrastructure.Payments` ไม่มี

- [ ] **Step 3: Write `SlipCheckResult.cs`**

```csharp
using System.Security.Cryptography;

namespace HuebRun.Infrastructure.Payments;

/// รูปสลิปที่ผ่านการตรวจชนิดไฟล์แล้ว (Extension/ContentType มาจาก magic bytes)
public sealed record SlipImage(byte[] Content, string Extension, string ContentType)
{
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Content));
}

/// ตัวตรวจสลิป — ไม่ผูกกับช่องทางรับสลิป (หน้าเว็บ/LINE ใช้ร่วมกันได้)
public interface ISlipVerifier
{
    Task<SlipCheckResult> VerifyAsync(SlipImage image, decimal expectedAmount, CancellationToken ct);
}

/// ผลตรวจสลิป RawJson = คำตอบดิบ (ต้องเป็น JSON ที่ถูกต้องหรือ null เพราะเก็บลง jsonb)
public abstract record SlipCheckResult(string? RawJson)
{
    public sealed record Verified(string TransRef, decimal Amount, DateTime? TransferredAt, string Raw) : SlipCheckResult(Raw);
    public sealed record Duplicate(string? TransRef, string Raw) : SlipCheckResult(Raw);
    public sealed record Rejected(string Reason, string Raw) : SlipCheckResult(Raw);
    public sealed record Retry(TimeSpan After, string Raw) : SlipCheckResult(Raw);
    public sealed record Unavailable(string? Raw) : SlipCheckResult(Raw);
}
```

- [ ] **Step 4: Write `SlipOkOptions.cs`**

```csharp
using System.ComponentModel.DataAnnotations;

namespace HuebRun.Infrastructure.Payments;

/// config `SlipOk` — BranchId/ApiKey มาจาก user-secrets หรือ env var เท่านั้น
public sealed class SlipOkOptions
{
    [Required] public string BaseUrl { get; set; } = "https://api.slipok.com";
    [Required] public string BranchId { get; set; } = "";
    [Required] public string ApiKey { get; set; } = "";
    [Range(1, 120)] public int TimeoutSeconds { get; set; } = 15;
}
```

- [ ] **Step 5: Write `SlipOkVerifier.cs`**

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HuebRun.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HuebRun.Infrastructure.Payments;

/// ตรวจสลิปกับ SlipOK (API guide v1.13) ส่ง log=true ให้ SlipOK กันสลิปซ้ำและเช็กบัญชีผู้รับที่ผูกใน LIFF
public sealed class SlipOkVerifier(HttpClient http, IOptions<SlipOkOptions> options, ILogger<SlipOkVerifier> logger) : ISlipVerifier
{
    public async Task<SlipCheckResult> VerifyAsync(SlipImage image, decimal expectedAmount, CancellationToken ct)
    {
        var o = options.Value;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        form.Add(file, "files", "slip" + image.Extension);
        form.Add(new StringContent("true"), "log");
        form.Add(new StringContent(expectedAmount.ToString("0.00", CultureInfo.InvariantCulture)), "amount");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/line/apikey/{o.BranchId}") { Content = form };
        request.Headers.TryAddWithoutValidation("x-authorization", o.ApiKey);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            return Map(response.StatusCode, raw);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "SlipOK request failed");
            return new SlipCheckResult.Unavailable(null);
        }
    }

    private SlipCheckResult Map(HttpStatusCode status, string raw)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            logger.LogError("SlipOK returned non-JSON response (HTTP {Status})", (int)status);
            return new SlipCheckResult.Unavailable(null);
        }

        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : (JsonElement?)null;
        if (status == HttpStatusCode.OK && IsTrue(root, "success") && data is { } ok)
        {
            if (!IsTrue(ok, "success")) return new SlipCheckResult.Rejected(PaymentRejectReason.InvalidSlip, raw);
            if (Str(ok, "transRef") is { } transRef && ok.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var amount))
                return new SlipCheckResult.Verified(transRef, amount, Timestamp(ok), raw);
        }

        var code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
        switch (code)
        {
            case 1012:
                return new SlipCheckResult.Duplicate(data is { } dup ? Str(dup, "transRef") : null, raw);
            case 1013:
                return new SlipCheckResult.Rejected(PaymentRejectReason.AmountMismatch, raw);
            case 1014:
                return new SlipCheckResult.Rejected(PaymentRejectReason.WrongReceiver, raw);
            case 1000 or 1005 or 1006 or 1007 or 1008 or 1011:
                return new SlipCheckResult.Rejected(PaymentRejectReason.InvalidSlip, raw);
            case 1010:
                var delay = data is { } wait && wait.TryGetProperty("delay", out var dl) && dl.TryGetInt32(out var m) ? m : 5;
                return new SlipCheckResult.Retry(TimeSpan.FromMinutes(Math.Max(1, delay)), raw);
            case 1009:
                return new SlipCheckResult.Retry(TimeSpan.FromMinutes(15), raw);
            default:
                // 1001–1004, 1015, 5xx: config หรือแพ็กเกจผิด ต้องมีคนดู
                logger.LogError("SlipOK unavailable: HTTP {Status}, code {Code}", (int)status, code);
                return new SlipCheckResult.Unavailable(raw);
        }
    }

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static DateTime? Timestamp(JsonElement e) =>
        Str(e, "transTimestamp") is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
```

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~SlipOkVerifierTests`
Expected: PASS ทั้งหมด

- [ ] **Step 7: Commit**

```bash
git add src/HuebRun.Infrastructure/Payments tests/HuebRun.Tests/Data/SlipOkVerifierTests.cs
git commit -m "feat(infra): SlipOK slip verifier"
```

---

### Task 6: ตรวจไฟล์สลิป + เก็บใน MinIO

**Files:**
- Modify: `src/HuebRun.Infrastructure/HuebRun.Infrastructure.csproj` (+ `Minio` 6.0.5)
- Modify: `tests/HuebRun.Tests/HuebRun.Tests.csproj` (+ `Testcontainers.Minio` 4.16.0)
- Create: `src/HuebRun.Infrastructure/Payments/MinioOptions.cs`
- Create: `src/HuebRun.Infrastructure/Payments/ISlipStorage.cs`
- Create: `src/HuebRun.Infrastructure/Payments/SlipFile.cs`
- Create: `src/HuebRun.Infrastructure/Payments/PaymentErrors.cs`
- Create: `tests/HuebRun.Tests/Infrastructure/Slips.cs`
- Test: `tests/HuebRun.Tests/Data/SlipFileTests.cs`, `tests/HuebRun.Tests/Data/MinioSlipStorageTests.cs`

**Interfaces:**
- Consumes: `SlipImage` จาก Task 5
- Produces:
  - `MinioOptions { Endpoint, AccessKey, SecretKey, Bucket, UseSsl, MaxFileSizeBytes, AllowedExtensions }`
  - `interface ISlipStorage { Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct); Task<byte[]> GetAsync(string key, CancellationToken ct); Task DeleteAsync(string key, CancellationToken ct); }`
  - `MinioSlipStorage(IMinioClient client, IOptions<MinioOptions> options)`
  - `SlipFile.ReadAsync(string? fileName, long length, Stream content, MinioOptions options, CancellationToken ct) : Task<SlipImage>` (ไม่ผ่าน → `AppException` 400 `VALIDATION_ERROR` details `{ file: [..] }`)
  - `SlipFile.FromBytes(byte[] content) : SlipImage?`
  - `PaymentErrors.{InvalidFile(string), OrderAlreadyPaid(), OrderNotPayable(OrderStatus), PaymentInProgress(), SlipDuplicate(), SlipRejected(int paymentId, string reason)}`
  - test helper `Slips.Png(int seed) : byte[]`, `Slips.Image(int seed) : SlipImage`

- [ ] **Step 1: เพิ่ม package**

```bash
dotnet add src/HuebRun.Infrastructure package Minio --version 6.0.5
dotnet add tests/HuebRun.Tests package Testcontainers.Minio --version 4.16.0
```

- [ ] **Step 2: Write test helper `tests/HuebRun.Tests/Infrastructure/Slips.cs`**

```csharp
using HuebRun.Infrastructure.Payments;

namespace HuebRun.Tests.Infrastructure;

/// รูปปลอมที่ magic bytes ถูกต้อง เปลี่ยน seed แล้ว hash ไม่ซ้ำ
public static class Slips
{
    public static byte[] Png(int seed) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. BitConverter.GetBytes(seed), .. BitConverter.GetBytes(Environment.TickCount64)];

    public static SlipImage Image(int seed = 0) => new(Png(seed), ".png", "image/png");
}
```

- [ ] **Step 3: Write the failing tests**

`tests/HuebRun.Tests/Data/SlipFileTests.cs`:

```csharp
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Data;

public class SlipFileTests
{
    private static readonly MinioOptions Options = new()
    {
        MaxFileSizeBytes = 1024, AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp"],
    };

    private static Task<SlipImage> Read(string name, byte[] bytes, long? length = null) =>
        SlipFile.ReadAsync(name, length ?? bytes.Length, new MemoryStream(bytes), Options, default);

    private static async Task AssertInvalid(Task<SlipImage> read)
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => read);
        Assert.Equal((400, "VALIDATION_ERROR"), (ex.StatusCode, ex.Code));
        Assert.True(JsonSerializer.SerializeToElement(ex.Details).TryGetProperty("file", out _));
    }

    [Theory]
    [InlineData("slip.png")]
    [InlineData("SLIP.PNG")]
    public async Task Png_is_accepted_regardless_of_extension_case(string name)
    {
        var image = await Read(name, Slips.Png(1));
        Assert.Equal((".png", "image/png"), (image.Extension, image.ContentType));
    }

    [Fact]
    public async Task Jpeg_and_webp_are_detected_from_magic_bytes()
    {
        Assert.Equal("image/jpeg", (await Read("a.JPG", [0xFF, 0xD8, 0xFF, 0xE0, 1])).ContentType);
        Assert.Equal("image/webp", (await Read("a.webp", [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, 1])).ContentType);
    }

    [Fact]
    public async Task Gif_renamed_to_jpg_is_rejected() =>
        await AssertInvalid(Read("slip.jpg", [.. "GIF89a"u8, 1, 2, 3]));

    [Theory]
    [InlineData("slip.gif")]
    [InlineData("slip.heic")]
    [InlineData("slip")]
    public async Task Unsupported_extension_is_rejected(string name) =>
        await AssertInvalid(Read(name, Slips.Png(1)));

    [Fact]
    public async Task Empty_and_oversized_files_are_rejected()
    {
        await AssertInvalid(Read("slip.png", []));
        await AssertInvalid(Read("slip.png", Slips.Png(1), length: 2048));
    }
}
```

`tests/HuebRun.Tests/Data/MinioSlipStorageTests.cs`:

```csharp
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Testcontainers.Minio;

namespace HuebRun.Tests.Data;

public sealed class MinioSlipStorageTests : IAsyncLifetime
{
    private readonly MinioContainer _minio = new MinioBuilder("minio/minio:RELEASE.2023-01-31T02-24-19Z").Build();
    private MinioSlipStorage _storage = null!;

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();
        var client = new MinioClient()
            .WithEndpoint(new Uri(_minio.GetConnectionString()).Authority)
            .WithCredentials(_minio.GetAccessKey(), _minio.GetSecretKey())
            .WithSSL(false)
            .Build();
        await client.MakeBucketAsync(new MakeBucketArgs().WithBucket("slips-test"));
        _storage = new MinioSlipStorage(client, Options.Create(new MinioOptions { Bucket = "slips-test" }));
    }

    public Task DisposeAsync() => _minio.DisposeAsync().AsTask();

    [Fact]
    public async Task Put_get_delete_round_trip()
    {
        var bytes = Slips.Png(7);
        await _storage.PutAsync("slips/1/abc/1.png", bytes, "image/png", default);
        Assert.Equal(bytes, await _storage.GetAsync("slips/1/abc/1.png", default));

        await _storage.DeleteAsync("slips/1/abc/1.png", default);
        await Assert.ThrowsAnyAsync<Exception>(() => _storage.GetAsync("slips/1/abc/1.png", default));
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/HuebRun.Tests --filter "FullyQualifiedName~SlipFileTests|FullyQualifiedName~MinioSlipStorageTests"`
Expected: FAIL — build error (`MinioOptions`, `SlipFile`, `MinioSlipStorage` ไม่มี)

- [ ] **Step 5: Write `MinioOptions.cs`**

```csharp
using System.ComponentModel.DataAnnotations;

namespace HuebRun.Infrastructure.Payments;

/// config `Minio` — AccessKey/SecretKey มาจาก user-secrets หรือ env var เท่านั้น; bucket ต้องเป็น private
public sealed class MinioOptions
{
    [Required] public string Endpoint { get; set; } = "";
    [Required] public string AccessKey { get; set; } = "";
    [Required] public string SecretKey { get; set; } = "";
    [Required] public string Bucket { get; set; } = "";
    public bool UseSsl { get; set; }
    [Range(1, long.MaxValue)] public long MaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;
    public string[] AllowedExtensions { get; set; } = [];
}
```

- [ ] **Step 6: Write `ISlipStorage.cs`**

```csharp
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace HuebRun.Infrastructure.Payments;

/// ที่เก็บรูปสลิป (ข้อมูลการเงินส่วนบุคคล — ห้ามเปิด public)
public interface ISlipStorage
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);
    Task<byte[]> GetAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed class MinioSlipStorage(IMinioClient client, IOptions<MinioOptions> options) : ISlipStorage
{
    private string Bucket => options.Value.Bucket;

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        using var stream = new MemoryStream(content);
        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(Bucket).WithObject(key)
            .WithStreamData(stream).WithObjectSize(content.Length)
            .WithContentType(contentType), ct);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await client.GetObjectAsync(new GetObjectArgs()
            .WithBucket(Bucket).WithObject(key)
            .WithCallbackStream((stream, token) => stream.CopyToAsync(buffer, token)), ct);
        return buffer.ToArray();
    }

    public Task DeleteAsync(string key, CancellationToken ct) =>
        client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(key), ct);
}
```

- [ ] **Step 7: Write `PaymentErrors.cs`**

```csharp
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;

namespace HuebRun.Infrastructure.Payments;

public static class PaymentErrors
{
    public static AppException InvalidFile(string message) =>
        OrderErrors.Validation(new Dictionary<string, string[]> { ["file"] = [message] });

    public static AppException OrderAlreadyPaid() =>
        new(409, "ORDER_ALREADY_PAID", "ใบสมัครนี้ชำระเงินแล้ว");

    public static AppException OrderNotPayable(OrderStatus status) =>
        new(409, "ORDER_NOT_PAYABLE", "ใบสมัครนี้ไม่อยู่ในสถานะที่ชำระเงินได้", new { status = status.ToString() });

    public static AppException PaymentInProgress() =>
        new(409, "PAYMENT_IN_PROGRESS", "กำลังตรวจสอบสลิปก่อนหน้า กรุณารอสักครู่");

    public static AppException SlipDuplicate() =>
        new(409, "SLIP_DUPLICATE", "สลิปนี้ถูกส่งเข้ามาแล้ว");

    public static AppException SlipRejected(int paymentId, string reason) =>
        new(422, "SLIP_REJECTED", "สลิปไม่ผ่านการตรวจสอบ", new { paymentId, reason });
}
```

- [ ] **Step 8: Write `SlipFile.cs`**

```csharp
namespace HuebRun.Infrastructure.Payments;

/// ตรวจไฟล์สลิปที่อัปโหลด: ขนาด, นามสกุลที่อนุญาต และชนิดจริงจาก magic bytes (SlipOK รับแค่ JPEG/PNG/WebP)
public static class SlipFile
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    public static async Task<SlipImage> ReadAsync(string? fileName, long length, Stream content, MinioOptions options, CancellationToken ct)
    {
        if (length <= 0) throw PaymentErrors.InvalidFile("กรุณาแนบรูปสลิป");
        if (length > options.MaxFileSizeBytes)
            throw PaymentErrors.InvalidFile($"ไฟล์ต้องมีขนาดไม่เกิน {options.MaxFileSizeBytes / 1024 / 1024} MB");

        var ext = Path.GetExtension(fileName ?? "");
        if (!Supported.Contains(ext) || !options.AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw PaymentErrors.InvalidFile("รองรับเฉพาะไฟล์ .jpg .jpeg .png .webp");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > options.MaxFileSizeBytes)
            throw PaymentErrors.InvalidFile($"ไฟล์ต้องมีขนาดไม่เกิน {options.MaxFileSizeBytes / 1024 / 1024} MB");
        return FromBytes(buffer.ToArray()) ?? throw PaymentErrors.InvalidFile("ไฟล์ไม่ใช่รูปภาพที่รองรับ");
    }

    /// ชนิดจาก magic bytes; null = ไม่ใช่ JPEG/PNG/WebP
    public static SlipImage? FromBytes(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return new(b, ".jpg", "image/jpeg");
        if (b.Length >= 8 && b.AsSpan(0, 8).SequenceEqual(PngMagic)) return new(b, ".png", "image/png");
        if (b.Length >= 12 && b.AsSpan(0, 4).SequenceEqual("RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return new(b, ".webp", "image/webp");
        return null;
    }

    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
}
```

- [ ] **Step 9: Run tests**

Run: `dotnet test tests/HuebRun.Tests --filter "FullyQualifiedName~SlipFileTests|FullyQualifiedName~MinioSlipStorageTests"`
Expected: PASS (ครั้งแรกดึง image MinIO อาจใช้เวลาเป็นนาที)

- [ ] **Step 10: Commit**

```bash
git add src/HuebRun.Infrastructure tests/HuebRun.Tests
git commit -m "feat(infra): slip file validation and MinIO storage"
```

---

### Task 7: `PaymentService` — ดู QR, อัปโหลดสลิป, นำผลตรวจไปใช้

**Files:**
- Create: `src/HuebRun.Infrastructure/Payments/PaymentOptions.cs`
- Create: `src/HuebRun.Infrastructure/Payments/PaymentModels.cs`
- Create: `src/HuebRun.Infrastructure/Payments/PaymentService.cs`
- Create: `tests/HuebRun.Tests/Infrastructure/FakeSlipVerifier.cs`
- Create: `tests/HuebRun.Tests/Infrastructure/InMemorySlipStorage.cs`
- Test: `tests/HuebRun.Tests/Data/PaymentServiceTests.cs`

**Interfaces:**
- Consumes: `PromptPayPayload.Create` (Task 1); entity/constant ของ Task 2; `MarkPaidAsync` ร่วม transaction (Task 3); `ISlipVerifier`, `SlipCheckResult`, `SlipImage` (Task 5); `ISlipStorage`, `PaymentErrors` (Task 6); `OrderErrors.OrderNotFound`
- Produces:
  - `PaymentOptions { VerificationHoldMinutes = 90, RejectGraceMinutes = 10 }`
  - `record PaymentSummary(int PaymentId, PaymentStatus Status, string? RejectReason, DateTime CreatedAt)`
  - `record PaymentInfo(Guid OrderId, string OrderNo, decimal Amount, string AccountName, string QrPayload, DateTime ExpiresAt, PaymentSummary? Payment)`
  - `record SlipSubmission(int PaymentId, PaymentStatus Status, OrderStatus OrderStatus, DateTime? NextCheckAt, string? Reason)`
  - `PaymentService(AppDbContext db, OrderPaymentService orders, ISlipVerifier verifier, ISlipStorage storage, TimeProvider time, IOptions<PaymentOptions> options)`
    - `GetPaymentInfoAsync(Guid orderId, CancellationToken ct) : Task<PaymentInfo>`
    - `SubmitSlipAsync(Guid orderId, SlipImage image, CancellationToken ct) : Task<SlipSubmission>` (Rejected → throw `SlipRejected`)
    - `ApplyResultAsync(int paymentId, SlipCheckResult result, CancellationToken ct) : Task<SlipSubmission>` (ไม่ throw ตามผล)
    - `const int MaxAttempts = 6`, `static readonly TimeSpan MaxVerifyAge = 60 นาที`, `static readonly TimeSpan ClaimWindow = 5 นาที`
  - test `FakeSlipVerifier { Enqueue(SlipCheckResult), int Calls }`, `InMemorySlipStorage { Objects, bool FailPut }`

- [ ] **Step 1: Write test doubles**

`tests/HuebRun.Tests/Infrastructure/FakeSlipVerifier.cs`:

```csharp
using System.Collections.Concurrent;
using HuebRun.Infrastructure.Payments;

namespace HuebRun.Tests.Infrastructure;

/// คืนผลตามคิวที่กำหนด; คิวว่าง = Unavailable
public sealed class FakeSlipVerifier : ISlipVerifier
{
    private readonly ConcurrentQueue<SlipCheckResult> _results = new();
    private int _calls;

    public int Calls => _calls;

    public FakeSlipVerifier Enqueue(params SlipCheckResult[] results)
    {
        foreach (var r in results) _results.Enqueue(r);
        return this;
    }

    public Task<SlipCheckResult> VerifyAsync(SlipImage image, decimal expectedAmount, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(_results.TryDequeue(out var r) ? r : new SlipCheckResult.Unavailable(null));
    }

    public static SlipCheckResult Ok(decimal amount, string? transRef = null) =>
        new SlipCheckResult.Verified(transRef ?? Guid.NewGuid().ToString("N"), amount, new DateTime(2026, 11, 15, 0, 5, 0, DateTimeKind.Utc), "{}");
}
```

`tests/HuebRun.Tests/Infrastructure/InMemorySlipStorage.cs`:

```csharp
using System.Collections.Concurrent;
using HuebRun.Infrastructure.Payments;

namespace HuebRun.Tests.Infrastructure;

public sealed class InMemorySlipStorage : ISlipStorage
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public bool FailPut { get; set; }

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        if (FailPut) throw new IOException("storage down");
        Objects[key] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]> GetAsync(string key, CancellationToken ct) =>
        Objects.TryGetValue(key, out var v) ? Task.FromResult(v) : throw new FileNotFoundException(key);

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/HuebRun.Tests/Data/PaymentServiceTests.cs`:

```csharp
using HuebRun.Domain;
using HuebRun.Infrastructure;
using HuebRun.Infrastructure.Orders;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class PaymentServiceTests(PostgresFixture pg) : OrderTestBase(pg)
{
    // ออเดอร์สร้างตอน 00:00Z หมดอายุ 00:30Z
    private static FakeTimeProvider At(int minute, int second = 0) =>
        new(new DateTimeOffset(2026, 11, 15, 0, minute, second, TimeSpan.Zero));

    private readonly FakeSlipVerifier _verifier = new();
    private readonly InMemorySlipStorage _storage = new();

    private PaymentService Service(AppDbContext db, TimeProvider time) =>
        new(db, new OrderPaymentService(db), _verifier, _storage, time, Options.Create(new PaymentOptions()));

    private async Task<SlipSubmission> Submit(Guid orderId, SlipImage image, TimeProvider? time = null)
    {
        await using var db = Pg.CreateDbContext();
        return await Service(db, time ?? At(5)).SubmitSlipAsync(orderId, image, default);
    }

    private async Task<AppException> SubmitFails(Guid orderId, SlipImage image, TimeProvider? time = null) =>
        await Assert.ThrowsAsync<AppException>(() => Submit(orderId, image, time));

    private async Task<(Order Order, List<Payment> Payments)> Load(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        var order = await db.Orders.Include(o => o.Registrations).SingleAsync(o => o.Id == orderId);
        return (order, await db.Payments.Where(p => p.OrderId == orderId).OrderBy(p => p.Id).ToListAsync());
    }

    [Fact]
    public async Task Verified_slip_pays_order_and_issues_bib()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount, "TR-1"));

        var r = await Submit(created.OrderId, Slips.Image(1));

        Assert.Equal((PaymentStatus.Verified, OrderStatus.Paid), (r.Status, r.OrderStatus));
        var (order, payments) = await Load(created.OrderId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal("B0001", order.Registrations.Single().BibNumber);
        var p = payments.Single();
        Assert.Equal(("TR-1", (decimal?)created.PayableAmount, PaymentProvider.SlipOk), (p.SlipTransRef, p.PaidAmount, p.Provider));
        Assert.NotNull(p.VerifiedAt);
        Assert.Null(p.NextCheckAt);
        Assert.Equal($"slips/{Ev.Id}/{created.OrderId}/{p.Id}.png", p.SlipObjectKey);
        Assert.True(_storage.Objects.ContainsKey(p.SlipObjectKey!));
    }

    [Fact]
    public async Task Rejected_slip_throws_422_keeps_order_pending_and_gives_grace_time()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Rejected(PaymentRejectReason.WrongReceiver, "{}"));

        var ex = await SubmitFails(created.OrderId, Slips.Image(1), At(29));

        Assert.Equal((422, "SLIP_REJECTED"), (ex.StatusCode, ex.Code));
        var (order, payments) = await Load(created.OrderId);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(new DateTime(2026, 11, 15, 0, 39, 0, DateTimeKind.Utc), order.ExpiresAt);   // 00:29 + 10 นาที
        Assert.Equal((PaymentStatus.Rejected, PaymentRejectReason.WrongReceiver), (payments.Single().Status, payments.Single().RejectReason));

        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));   // อัปโหลดใหม่ได้
        Assert.Equal(PaymentStatus.Verified, (await Submit(created.OrderId, Slips.Image(2), At(31))).Status);
    }

    [Fact]
    public async Task Verified_amount_that_differs_is_rejected()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount + 1));
        var ex = await SubmitFails(created.OrderId, Slips.Image(1));
        Assert.Contains(PaymentRejectReason.AmountMismatch, System.Text.Json.JsonSerializer.Serialize(ex.Details));
    }

    [Fact]
    public async Task Same_image_twice_is_duplicate_without_calling_verifier()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Rejected(PaymentRejectReason.InvalidSlip, "{}"));
        var image = Slips.Image(1);
        await SubmitFails(created.OrderId, image);

        var ex = await SubmitFails(created.OrderId, image);
        Assert.Equal("SLIP_DUPLICATE", ex.Code);
        Assert.Equal(1, _verifier.Calls);
    }

    [Fact]
    public async Task Trans_ref_used_by_another_order_is_rejected()
    {
        var first = await CreateOrderOk();
        var second = await CreateOrderOk();
        _verifier.Enqueue(FakeSlipVerifier.Ok(first.PayableAmount, "TR-SAME"), FakeSlipVerifier.Ok(second.PayableAmount, "TR-SAME"));
        await Submit(first.OrderId, Slips.Image(1));

        var ex = await SubmitFails(second.OrderId, Slips.Image(2));
        Assert.Contains(PaymentRejectReason.DuplicateSlip, System.Text.Json.JsonSerializer.Serialize(ex.Details));
    }

    [Fact]
    public async Task Second_upload_while_verifying_is_rejected_as_in_progress()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Retry(TimeSpan.FromMinutes(8), "{}"));
        var r = await Submit(created.OrderId, Slips.Image(1));
        Assert.Equal(PaymentStatus.Verifying, r.Status);
        Assert.Equal(new DateTime(2026, 11, 15, 0, 13, 0, DateTimeKind.Utc), r.NextCheckAt);   // 00:05 + 8

        Assert.Equal("PAYMENT_IN_PROGRESS", (await SubmitFails(created.OrderId, Slips.Image(2))).Code);
    }

    [Fact]
    public async Task Verifying_holds_the_order_past_its_original_expiry()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Retry(TimeSpan.FromMinutes(8), "{}"));
        await Submit(created.OrderId, Slips.Image(1), At(29));

        await using (var db = Pg.CreateDbContext())
            await new OrderExpiryService(db, At(31)).ExpireDueOrdersAsync();

        var (order, _) = await Load(created.OrderId);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(new DateTime(2026, 11, 15, 1, 59, 0, DateTimeKind.Utc), order.ExpiresAt);   // 00:29 + 90
    }

    [Fact]
    public async Task Valid_slip_for_expired_order_needs_review_without_reserving_again()
    {
        var created = await CreateOrderOk();
        await using (var db = Pg.CreateDbContext())
            await new OrderExpiryService(db, At(31)).ExpireDueOrdersAsync();
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount, "TR-LATE"));

        var r = await Submit(created.OrderId, Slips.Image(1), At(40));

        Assert.Equal((PaymentStatus.NeedsReview, PaymentRejectReason.OrderExpired), (r.Status, r.Reason));
        var (order, payments) = await Load(created.OrderId);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal("TR-LATE", payments.Single().SlipTransRef);
        await using var check = Pg.CreateDbContext();
        Assert.Equal(0, (await check.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
    }

    [Fact]
    public async Task Unknown_duplicate_needs_review()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(new SlipCheckResult.Duplicate("TR-UNKNOWN", "{}"));
        var r = await Submit(created.OrderId, Slips.Image(1));
        Assert.Equal((PaymentStatus.NeedsReview, PaymentRejectReason.DuplicateUnknown), (r.Status, r.Reason));
    }

    [Fact]
    public async Task Storage_failure_leaves_no_payment_behind()
    {
        var created = await CreateOrderOk();
        _storage.FailPut = true;
        await Assert.ThrowsAsync<IOException>(() => Submit(created.OrderId, Slips.Image(1)));
        Assert.Empty((await Load(created.OrderId)).Payments);
        Assert.Equal(0, _verifier.Calls);
    }

    [Fact]
    public async Task Paid_and_unknown_orders_are_refused()
    {
        var created = await CreateOrderOk();
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));
        await Submit(created.OrderId, Slips.Image(1));

        Assert.Equal("ORDER_ALREADY_PAID", (await SubmitFails(created.OrderId, Slips.Image(2))).Code);
        Assert.Equal("ORDER_NOT_FOUND", (await SubmitFails(Guid.NewGuid(), Slips.Image(3))).Code);
    }

    [Fact]
    public async Task Payment_info_has_qr_payload_and_latest_payment()
    {
        var created = await CreateOrderOk();
        await using var db = Pg.CreateDbContext();
        var info = await Service(db, At(5)).GetPaymentInfoAsync(created.OrderId, default);

        Assert.Equal((created.PayableAmount, "SECRET ACCOUNT"), (info.Amount, info.AccountName));
        Assert.Equal(PromptPayPayload.Create("0812345678", created.PayableAmount), info.QrPayload);
        Assert.Null(info.Payment);
    }

    [Fact]
    public async Task Payment_info_refuses_paid_and_expired_orders()
    {
        var paid = await CreateOrderOk();
        _verifier.Enqueue(FakeSlipVerifier.Ok(paid.PayableAmount));
        await Submit(paid.OrderId, Slips.Image(1));
        var expired = await CreateOrderOk();

        await using var db = Pg.CreateDbContext();
        var ex = await Assert.ThrowsAsync<AppException>(() => Service(db, At(5)).GetPaymentInfoAsync(paid.OrderId, default));
        Assert.Equal("ORDER_ALREADY_PAID", ex.Code);
        ex = await Assert.ThrowsAsync<AppException>(() => Service(db, At(31)).GetPaymentInfoAsync(expired.OrderId, default));
        Assert.Equal("ORDER_NOT_PAYABLE", ex.Code);
    }

    [Fact]
    public async Task Slip_racing_expiry_never_leaves_expired_order_with_verified_payment()
    {
        for (var round = 0; round < 10; round++)
        {
            var created = await CreateOrderOk();
            _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));
            await using var payDb = Pg.CreateDbContext();
            await using var expireDb = Pg.CreateDbContext();

            var submit = Task.Run(async () =>
            {
                try { return (await Service(payDb, At(29, 59)).SubmitSlipAsync(created.OrderId, Slips.Image(round), default)).Status; }
                catch (AppException) { return PaymentStatus.Rejected; }
            });
            var expire = Task.Run(() => new OrderExpiryService(expireDb, At(31)).ExpireDueOrdersAsync());
            await Task.WhenAll(submit, expire);

            var (order, payments) = await Load(created.OrderId);
            var verified = payments.Any(p => p.Status == PaymentStatus.Verified);
            Assert.Equal(verified, order.Status == OrderStatus.Paid);
            Assert.False(order.Status == OrderStatus.Expired && verified);
        }

        await using var db = Pg.CreateDbContext();
        var paidCount = await db.Orders.CountAsync(o => o.EventId == Ev.Id && o.Status == OrderStatus.Paid);
        var pendingCount = await db.Orders.CountAsync(o => o.EventId == Ev.Id && o.Status == OrderStatus.Pending);
        Assert.Equal(paidCount + pendingCount, (await db.EventDistances.SingleAsync(d => d.Id == FiveK.Id)).ReservedCount);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~PaymentServiceTests`
Expected: FAIL — build error (`PaymentService`, `PaymentOptions`, `SlipSubmission` ไม่มี)

- [ ] **Step 4: Write `PaymentOptions.cs` และ `PaymentModels.cs`**

```csharp
using System.ComponentModel.DataAnnotations;

namespace HuebRun.Infrastructure.Payments;

/// config `Payments`
public sealed class PaymentOptions
{
    /// ยืดเวลาหมดอายุของออเดอร์เมื่อรับสลิป ให้ครอบช่วงตรวจซ้ำทั้งหมด
    [Range(1, 1440)] public int VerificationHoldMinutes { get; set; } = 90;
    /// สลิปไม่ผ่าน: เวลาที่เหลือให้อัปโหลดใหม่
    [Range(1, 1440)] public int RejectGraceMinutes { get; set; } = 10;
}
```

```csharp
using HuebRun.Domain;

namespace HuebRun.Infrastructure.Payments;

public sealed record PaymentSummary(int PaymentId, PaymentStatus Status, string? RejectReason, DateTime CreatedAt);

public sealed record PaymentInfo(
    Guid OrderId, string OrderNo, decimal Amount, string AccountName, string QrPayload, DateTime ExpiresAt, PaymentSummary? Payment);

public sealed record SlipSubmission(int PaymentId, PaymentStatus Status, OrderStatus OrderStatus, DateTime? NextCheckAt, string? Reason);
```

- [ ] **Step 5: Write `PaymentService.cs`**

```csharp
using HuebRun.Domain;
using HuebRun.Infrastructure.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HuebRun.Infrastructure.Payments;

/// ชำระเงินด้วยสลิป: ข้อมูล QR, รับสลิป, และนำผลตรวจไปเปลี่ยนสถานะ (ใช้ร่วมกับ job ตรวจซ้ำ)
public sealed class PaymentService(
    AppDbContext db, OrderPaymentService orders, ISlipVerifier verifier, ISlipStorage storage,
    TimeProvider time, IOptions<PaymentOptions> options)
{
    public const int MaxAttempts = 6;
    public static readonly TimeSpan MaxVerifyAge = TimeSpan.FromMinutes(60);
    /// Payment ใหม่ถูกจองให้ request ที่รับสลิปช่วงนี้ job จะยังไม่แตะ
    public static readonly TimeSpan ClaimWindow = TimeSpan.FromMinutes(5);
    private const string VerifyingIndex = "IX_Payments_OrderId_Verifying";
    private static readonly int[] BackoffMinutes = [1, 2, 5, 10, 15, 30];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<PaymentInfo> GetPaymentInfoAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Event).FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw OrderErrors.OrderNotFound(orderId);
        var status = order.GetEffectiveStatus(Now);
        if (status == OrderStatus.Paid) throw PaymentErrors.OrderAlreadyPaid();
        if (status != OrderStatus.Pending) throw PaymentErrors.OrderNotPayable(status);

        var latest = await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == orderId).OrderByDescending(p => p.Id)
            .Select(p => new PaymentSummary(p.Id, p.Status, p.RejectReason, p.CreatedAt))
            .FirstOrDefaultAsync(ct);
        return new PaymentInfo(order.Id, order.OrderNo, order.PayableAmount, order.Event.PromptPayAccountName,
            PromptPayPayload.Create(order.Event.PromptPayId, order.PayableAmount), order.ExpiresAt, latest);
    }

    /// รับสลิป: hold ออเดอร์ + บันทึก Payment + เก็บรูป ใน transaction เดียว แล้วตรวจทันที
    public async Task<SlipSubmission> SubmitSlipAsync(Guid orderId, SlipImage image, CancellationToken ct)
    {
        var now = Now;
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw OrderErrors.OrderNotFound(orderId);
        if (order.Status == OrderStatus.Paid) throw PaymentErrors.OrderAlreadyPaid();
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Refunded) throw PaymentErrors.OrderNotPayable(order.Status);

        var hash = image.Sha256;
        if (await db.Payments.AnyAsync(p => p.SlipHash == hash, ct)) throw PaymentErrors.SlipDuplicate();
        if (await db.Payments.AnyAsync(p => p.OrderId == orderId && p.Status == PaymentStatus.Verifying, ct))
            throw PaymentErrors.PaymentInProgress();

        var payment = new Payment
        {
            OrderId = orderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.Verifying,
            ExpectedAmount = order.PayableAmount, SlipHash = hash, CreatedAt = now, NextCheckAt = now + ClaimWindow,
        };
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // hold: ยืดเวลาหมดอายุให้ครอบช่วงตรวจ (หมดเวลาไปแล้วหรือยืดอยู่แล้วก็ไม่แตะ)
            var holdUntil = now.AddMinutes(options.Value.VerificationHoldMinutes);
            await db.Orders
                .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending && o.ExpiresAt > now && o.ExpiresAt < holdUntil)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, holdUntil), ct);

            db.Payments.Add(payment);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
            {
                throw pg.ConstraintName == VerifyingIndex ? PaymentErrors.PaymentInProgress() : PaymentErrors.SlipDuplicate();
            }

            payment.SlipObjectKey = $"slips/{order.EventId}/{orderId}/{payment.Id}{image.Extension}";
            await storage.PutAsync(payment.SlipObjectKey, image.Content, image.ContentType, ct);   // ล้ม → rollback ทั้งหมด
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var result = await verifier.VerifyAsync(image, payment.ExpectedAmount, ct);
        var submission = await ApplyResultAsync(payment.Id, result, ct);
        if (submission.Status == PaymentStatus.Rejected) throw PaymentErrors.SlipRejected(submission.PaymentId, submission.Reason!);
        return submission;
    }

    /// นำผลตรวจไปใช้กับ Payment ที่ยัง Verifying (ล็อกแถวไว้ คนอื่นจัดการไปแล้ว = คืนสถานะปัจจุบัน)
    public async Task<SlipSubmission> ApplyResultAsync(int paymentId, SlipCheckResult result, CancellationToken ct)
    {
        var now = Now;
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payment = (await db.Payments
            .FromSql($"SELECT * FROM \"Payments\" WHERE \"Id\" = {paymentId} FOR UPDATE")
            .ToListAsync(ct)).Single();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == payment.OrderId, ct);
        if (payment.Status != PaymentStatus.Verifying) return Summary(payment, order.Status);

        if (result.RawJson is { } raw) payment.ProviderResponse = raw;
        switch (result)
        {
            case SlipCheckResult.Verified v:
                await ApplyVerifiedAsync(payment, order, v, now, ct);
                break;
            case SlipCheckResult.Duplicate d:
                var ours = d.TransRef is not null
                    && await db.Payments.AnyAsync(p => p.Id != payment.Id && p.SlipTransRef == d.TransRef, ct);
                // ไม่รู้จัก = SlipOK อาจบันทึกครั้งก่อนที่เราไม่ได้รับคำตอบ → ให้คนตรวจ ไม่ตีกลับคนที่จ่ายจริง
                if (ours) Reject(payment, PaymentRejectReason.DuplicateSlip);
                else Review(payment, PaymentRejectReason.DuplicateUnknown);
                break;
            case SlipCheckResult.Rejected r:
                Reject(payment, r.Reason);
                break;
            case SlipCheckResult.Retry r:
                Postpone(payment, r.After, now);
                break;
            case SlipCheckResult.Unavailable:
                Postpone(payment, TimeSpan.FromMinutes(BackoffMinutes[Math.Min(payment.CheckAttempts, BackoffMinutes.Length - 1)]), now);
                break;
        }
        if (payment.Status == PaymentStatus.Verifying && (payment.CheckAttempts >= MaxAttempts || now - payment.CreatedAt > MaxVerifyAge))
            Review(payment, PaymentRejectReason.VerifyTimeout);

        // ไม่ผ่าน: ปลด hold เหลือเวลาให้อัปโหลดใหม่
        if (payment.Status == PaymentStatus.Rejected && order.Status == OrderStatus.Pending)
        {
            var grace = now.AddMinutes(options.Value.RejectGraceMinutes);
            await db.Orders.Where(o => o.Id == order.Id && o.Status == OrderStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, grace), ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Summary(payment, payment.Status == PaymentStatus.Verified ? OrderStatus.Paid : order.Status);
    }

    private async Task ApplyVerifiedAsync(Payment payment, Order order, SlipCheckResult.Verified v, DateTime now, CancellationToken ct)
    {
        if (v.Amount != payment.ExpectedAmount)
        {
            Reject(payment, PaymentRejectReason.AmountMismatch);
            return;
        }
        if (await db.Payments.AnyAsync(p => p.Id != payment.Id && p.SlipTransRef == v.TransRef, ct))
        {
            Reject(payment, PaymentRejectReason.DuplicateSlip);
            return;
        }

        payment.SlipTransRef = v.TransRef;
        payment.PaidAmount = v.Amount;
        payment.TransferredAt = v.TransferredAt;
        payment.NextCheckAt = null;
        if (order.Status != OrderStatus.Pending)
        {
            Review(payment, PaymentRejectReason.OrderExpired);
            return;
        }

        payment.Status = PaymentStatus.Verified;
        payment.VerifiedAt = now;
        try
        {
            await orders.MarkPaidAsync(order.Id, now, ct);
        }
        catch (InvalidOperationException)
        {
            // job หมดเวลาชนะไปก่อนระหว่างตรวจ
            payment.VerifiedAt = null;
            Review(payment, PaymentRejectReason.OrderExpired);
        }
    }

    private static void Reject(Payment p, string reason) =>
        (p.Status, p.RejectReason, p.NextCheckAt) = (PaymentStatus.Rejected, reason, null);

    private static void Review(Payment p, string reason) =>
        (p.Status, p.RejectReason, p.NextCheckAt) = (PaymentStatus.NeedsReview, reason, null);

    private static void Postpone(Payment p, TimeSpan after, DateTime now)
    {
        p.CheckAttempts++;
        p.NextCheckAt = now + after;
    }

    private static SlipSubmission Summary(Payment p, OrderStatus orderStatus) =>
        new(p.Id, p.Status, orderStatus, p.NextCheckAt, p.RejectReason);
}
```

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~PaymentServiceTests`
Expected: PASS ทั้งหมด

- [ ] **Step 7: Commit**

```bash
git add src/HuebRun.Infrastructure/Payments tests/HuebRun.Tests
git commit -m "feat(infra): payment service for slip upload and verification results"
```

---

### Task 8: ตรวจซ้ำด้วย background job

**Files:**
- Create: `src/HuebRun.Infrastructure/Payments/PaymentRecheckService.cs`
- Create: `src/HuebRun.Api/Jobs/PaymentRecheckWorker.cs`
- Test: `tests/HuebRun.Tests/Data/PaymentRecheckTests.cs`

**Interfaces:**
- Consumes: `PaymentService.ApplyResultAsync`, `PaymentService.ClaimWindow` (Task 7); `ISlipVerifier`, `ISlipStorage`, `SlipFile.FromBytes` (Task 5–6)
- Produces: `PaymentRecheckService(AppDbContext db, PaymentService payments, ISlipVerifier verifier, ISlipStorage storage, TimeProvider time, ILogger<PaymentRecheckService>? logger = null)` + `RecheckDueAsync(CancellationToken ct = default) : Task<int>`; `PaymentRecheckWorker` (config `Jobs:PaymentRecheck:Enabled`)

- [ ] **Step 1: Write the failing tests**

```csharp
using HuebRun.Domain;
using HuebRun.Infrastructure;
using HuebRun.Infrastructure.Orders;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HuebRun.Tests.Data;

[Collection("db")]
public sealed class PaymentRecheckTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private readonly FakeSlipVerifier _verifier = new();
    private readonly InMemorySlipStorage _storage = new();

    private static FakeTimeProvider At(int minute) => new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute));

    private PaymentService Payments(AppDbContext db, TimeProvider time) =>
        new(db, new OrderPaymentService(db), _verifier, _storage, time, Options.Create(new PaymentOptions()));

    private async Task<int> Recheck(int minute)
    {
        await using var db = Pg.CreateDbContext();
        var time = At(minute);
        return await new PaymentRecheckService(db, Payments(db, time), _verifier, _storage, time).RecheckDueAsync();
    }

    private async Task<SlipSubmission> SubmitRetrying(Guid orderId, int minute = 5)
    {
        _verifier.Enqueue(new SlipCheckResult.Retry(TimeSpan.FromMinutes(8), "{}"));
        await using var db = Pg.CreateDbContext();
        return await Payments(db, At(minute)).SubmitSlipAsync(orderId, Slips.Image(minute), default);
    }

    private async Task<(OrderStatus Order, Payment Payment)> Load(Guid orderId)
    {
        await using var db = Pg.CreateDbContext();
        return ((await db.Orders.SingleAsync(o => o.Id == orderId)).Status,
            await db.Payments.SingleAsync(p => p.OrderId == orderId));
    }

    [Fact]
    public async Task Delayed_slip_is_verified_on_recheck()
    {
        var created = await CreateOrderOk();
        await SubmitRetrying(created.OrderId);
        Assert.Equal(0, await Recheck(12));                      // ยังไม่ถึง 00:13

        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));
        Assert.Equal(1, await Recheck(13));

        var (order, payment) = await Load(created.OrderId);
        Assert.Equal((OrderStatus.Paid, PaymentStatus.Verified), (order, payment.Status));
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_needs_review()
    {
        var created = await CreateOrderOk();
        await SubmitRetrying(created.OrderId);                  // attempt 1, ต่อไป 00:13
        var minute = 13;
        for (var i = 0; i < 6; i++)                              // คิวว่าง = Unavailable ทุกครั้ง
        {
            await Recheck(minute);
            var p = (await Load(created.OrderId)).Payment;
            if (p.Status != PaymentStatus.Verifying) break;
            minute = (int)(p.NextCheckAt!.Value - TestData.Utc(2026, 11, 15)).TotalMinutes;
        }

        var (order, payment) = await Load(created.OrderId);
        Assert.Equal((PaymentStatus.NeedsReview, PaymentRejectReason.VerifyTimeout), (payment.Status, payment.RejectReason));
        Assert.Equal(OrderStatus.Pending, order);
    }

    [Fact]
    public async Task Abandoned_payment_is_picked_up_after_claim_window()
    {
        // จำลองแอปตายหลังบันทึก Payment แต่ก่อนตรวจ: ใส่แถว Verifying ตรง ๆ
        var created = await CreateOrderOk();
        var bytes = Slips.Png(42);
        await using (var db = Pg.CreateDbContext())
        {
            var p = new Payment
            {
                OrderId = created.OrderId, Provider = PaymentProvider.SlipOk, Status = PaymentStatus.Verifying,
                ExpectedAmount = created.PayableAmount, SlipHash = Guid.NewGuid().ToString(),
                CreatedAt = TestData.Utc(2026, 11, 15).AddMinutes(5), NextCheckAt = TestData.Utc(2026, 11, 15).AddMinutes(10),
                SlipObjectKey = "slips/abandoned.png",
            };
            db.Payments.Add(p);
            await db.SaveChangesAsync();
        }
        _storage.Objects["slips/abandoned.png"] = bytes;
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));

        Assert.Equal(0, await Recheck(9));
        Assert.Equal(1, await Recheck(10));
        Assert.Equal(OrderStatus.Paid, (await Load(created.OrderId)).Order);
    }

    [Fact]
    public async Task Concurrent_runs_check_each_payment_once()
    {
        var created = await CreateOrderOk();
        await SubmitRetrying(created.OrderId);
        var callsBefore = _verifier.Calls;
        _verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));

        await Task.WhenAll(Recheck(13), Recheck(13), Recheck(13));

        Assert.Equal(callsBefore + 1, _verifier.Calls);
        Assert.Equal(OrderStatus.Paid, (await Load(created.OrderId)).Order);
    }

    [Fact]
    public async Task Missing_image_counts_as_unavailable_attempt()
    {
        var created = await CreateOrderOk();
        await SubmitRetrying(created.OrderId);
        _storage.Objects.Clear();

        await Recheck(13);

        var (_, payment) = await Load(created.OrderId);
        Assert.Equal((PaymentStatus.Verifying, 2), (payment.Status, payment.CheckAttempts));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~PaymentRecheckTests`
Expected: FAIL — build error (`PaymentRecheckService` ไม่มี)

- [ ] **Step 3: Write `PaymentRecheckService.cs`**

```csharp
using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HuebRun.Infrastructure.Payments;

/// ตรวจซ้ำ Payment ที่ Verifying และถึงเวลา (ธนาคารหน่วง / SlipOK ใช้ไม่ได้ / แอปตายก่อนตรวจ)
public sealed class PaymentRecheckService(
    AppDbContext db, PaymentService payments, ISlipVerifier verifier, ISlipStorage storage,
    TimeProvider time, ILogger<PaymentRecheckService>? logger = null)
{
    public const int BatchSize = 50;

    public async Task<int> RecheckDueAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Verifying && p.NextCheckAt <= now)
            .OrderBy(p => p.NextCheckAt)
            .Take(BatchSize)
            .Select(p => new { p.Id, p.NextCheckAt, p.SlipObjectKey, p.ExpectedAmount })
            .ToListAsync(ct);

        var checkedCount = 0;
        foreach (var p in due)
        {
            try
            {
                // จองงาน: เครื่อง/รอบอื่นที่เห็นค่าเดิมจะแก้ไม่ได้
                var claimed = await db.Payments
                    .Where(x => x.Id == p.Id && x.Status == PaymentStatus.Verifying && x.NextCheckAt == p.NextCheckAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextCheckAt, now + PaymentService.ClaimWindow), ct);
                if (claimed == 0) continue;

                SlipCheckResult result;
                try
                {
                    var image = SlipFile.FromBytes(await storage.GetAsync(p.SlipObjectKey!, ct))
                        ?? throw new InvalidOperationException($"Stored slip {p.SlipObjectKey} is not an image");
                    result = await verifier.VerifyAsync(image, p.ExpectedAmount, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogError(ex, "Recheck of payment {PaymentId} could not reach storage/verifier", p.Id);
                    result = new SlipCheckResult.Unavailable(null);
                }
                await payments.ApplyResultAsync(p.Id, result, ct);
                checkedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogError(ex, "Failed to recheck payment {PaymentId}; skipped", p.Id);
            }
        }
        return checkedCount;
    }
}
```

- [ ] **Step 4: Write `PaymentRecheckWorker.cs`**

```csharp
using HuebRun.Infrastructure.Payments;

namespace HuebRun.Api.Jobs;

/// เรียก PaymentRecheckService ทุก 1 นาที (รันหลายเครื่องได้ เพราะจองงานด้วย update แบบมีเงื่อนไข)
public sealed class PaymentRecheckWorker(IServiceScopeFactory scopes, ILogger<PaymentRecheckWorker> logger) : BackgroundService
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
                var count = await scope.ServiceProvider.GetRequiredService<PaymentRecheckService>()
                    .RecheckDueAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Rechecked {Count} payment slips", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Payment recheck run failed; will retry next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

(worker ถูกลงทะเบียนใน Task 9 พร้อม DI)

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~PaymentRecheckTests`
Expected: PASS ทั้งหมด

- [ ] **Step 6: Commit**

```bash
git add src/HuebRun.Infrastructure/Payments/PaymentRecheckService.cs src/HuebRun.Api/Jobs/PaymentRecheckWorker.cs tests/HuebRun.Tests/Data/PaymentRecheckTests.cs
git commit -m "feat: recheck delayed payment slips every minute"
```

---

### Task 9: API endpoint, DI, config และเอกสาร

**Files:**
- Modify: `src/HuebRun.Infrastructure/DependencyInjection.cs`
- Modify: `src/HuebRun.Api/Program.cs`
- Modify: `src/HuebRun.Api/appsettings.json`, `src/HuebRun.Api/appsettings.Development.json`
- Create: `src/HuebRun.Api/Controllers/PaymentController.cs`
- Modify: `src/HuebRun.Api/Controllers/OrdersController.cs`, `src/HuebRun.Api/Contracts/OrderContracts.cs`
- Modify: `tests/HuebRun.Tests/Infrastructure/ApiFactory.cs`
- Modify: `docs/api-contract.md`
- Test: `tests/HuebRun.Tests/Api/PaymentEndpointTests.cs`

**Interfaces:**
- Consumes: ทุกอย่างจาก Task 1–8
- Produces: `GET /api/v1/orders/{orderId}/payment` → `PaymentInfo`; `POST /api/v1/orders/{orderId}/payment/slip` (multipart `file`) → `200`/`202` `SlipSubmission`; `GET /orders/{id}` มี `payment: { paymentId, status, rejectReason } | null`; `ApiFactory.Verifier : FakeSlipVerifier`, `ApiFactory.Storage : InMemorySlipStorage`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class PaymentEndpointTests(PostgresFixture pg) : OrderTestBase(pg)
{
    private Task<HttpResponseMessage> Upload(Guid orderId, byte[] bytes, string name = "slip.png") =>
        Client.PostAsync($"/api/v1/orders/{orderId}/payment/slip",
            new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } });

    [Fact]
    public async Task Get_payment_returns_qr_payload_and_amount()
    {
        var created = await CreateOrderOk();
        var res = await Client.GetAsync($"/api/v1/orders/{created.OrderId}/payment");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(created.PayableAmount, body.GetProperty("amount").GetDecimal());
        Assert.StartsWith("000201010212", body.GetProperty("qrPayload").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("payment").ValueKind);
        Assert.False(body.TryGetProperty("promptPayId", out _));
    }

    [Fact]
    public async Task Valid_slip_returns_200_and_order_shows_paid_payment()
    {
        var created = await CreateOrderOk();
        Factory.Verifier.Enqueue(FakeSlipVerifier.Ok(created.PayableAmount));

        var res = await Upload(created.OrderId, Slips.Png(1));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Verified", "Paid"), (body.GetProperty("status").GetString(), body.GetProperty("orderStatus").GetString()));

        var order = await (await Client.GetAsync($"/api/v1/orders/{created.OrderId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Paid", order.GetProperty("status").GetString());
        Assert.Equal("Verified", order.GetProperty("payment").GetProperty("status").GetString());
        Assert.Single(Factory.Storage.Objects);
    }

    [Fact]
    public async Task Delayed_slip_returns_202_verifying()
    {
        var created = await CreateOrderOk();
        Factory.Verifier.Enqueue(new SlipCheckResult.Retry(TimeSpan.FromMinutes(8), "{}"));
        var res = await Upload(created.OrderId, Slips.Png(1));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal("Verifying", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Rejected_slip_returns_422_with_reason()
    {
        var created = await CreateOrderOk();
        Factory.Verifier.Enqueue(new SlipCheckResult.Rejected(PaymentRejectReason.AmountMismatch, "{}"));
        var body = await AssertError(await Upload(created.OrderId, Slips.Png(1)), HttpStatusCode.UnprocessableEntity, "SLIP_REJECTED");
        Assert.Equal("AMOUNT_MISMATCH", body.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Missing_or_invalid_file_is_400()
    {
        var created = await CreateOrderOk();
        var empty = await Client.PostAsync($"/api/v1/orders/{created.OrderId}/payment/slip", new MultipartFormDataContent());
        await AssertError(empty, HttpStatusCode.BadRequest, "VALIDATION_ERROR");

        var gif = await AssertError(await Upload(created.OrderId, [.. "GIF89a"u8, 1], "slip.jpg"), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.True(gif.GetProperty("details").TryGetProperty("file", out _));
        Assert.Equal(0, Factory.Verifier.Calls);
    }

    [Fact]
    public async Task Free_order_payment_page_is_already_paid()
    {
        await AddPromoAsync(p => { p.Code = "FREE100"; p.Value = 100; });
        var created = await CreateOrderOk(Request() with { PromoCode = "FREE100" });
        await AssertError(await Client.GetAsync($"/api/v1/orders/{created.OrderId}/payment"), HttpStatusCode.Conflict, "ORDER_ALREADY_PAID");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HuebRun.Tests --filter FullyQualifiedName~PaymentEndpointTests`
Expected: FAIL — build error (`Factory.Verifier` ไม่มี)

- [ ] **Step 3: DI ใน `DependencyInjection.cs`**

แทนทั้งไฟล์ด้วย:

```csharp
using HuebRun.Infrastructure.Orders;
using HuebRun.Infrastructure.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Minio;

namespace HuebRun.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("DefaultConnection") ?? ""));
        services.AddScoped<OrderService>();
        services.AddScoped<OrderPaymentService>();
        services.AddScoped<OrderExpiryService>();

        // ขาด secret แล้วแอปไม่ขึ้น ดีกว่าไปพังตอนนักวิ่งอัปสลิป
        services.AddOptions<SlipOkOptions>().Bind(config.GetSection("SlipOk")).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<MinioOptions>().Bind(config.GetSection("Minio")).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PaymentOptions>().Bind(config.GetSection("Payments")).ValidateDataAnnotations().ValidateOnStart();

        services.AddHttpClient<ISlipVerifier, SlipOkVerifier>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<SlipOkOptions>>().Value;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        });
        services.AddSingleton<IMinioClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
            return new MinioClient().WithEndpoint(o.Endpoint).WithCredentials(o.AccessKey, o.SecretKey).WithSSL(o.UseSsl).Build();
        });
        services.AddSingleton<ISlipStorage, MinioSlipStorage>();
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentRecheckService>();
        return services;
    }
}
```

- [ ] **Step 4: ลงทะเบียน worker ใน `Program.cs`**

ต่อจากบรรทัด `builder.Services.AddHostedService<OrderExpiryWorker>();`:

```csharp
if (builder.Configuration.GetValue("Jobs:PaymentRecheck:Enabled", true))
    builder.Services.AddHostedService<PaymentRecheckWorker>();
```

- [ ] **Step 5: Config**

`src/HuebRun.Api/appsettings.json` — ต่อจาก `"Jobs"` (แทนก้อน `"Jobs"` เดิม):

```json
  "Jobs": {
    "OrderExpiry": { "Enabled": true },
    "PaymentRecheck": { "Enabled": true }
  },
  "SlipOk": {
    "BaseUrl": "https://api.slipok.com",
    "TimeoutSeconds": 15
  },
  "Minio": {
    "UseSsl": true,
    "MaxFileSizeBytes": 5242880,
    "AllowedExtensions": [ ".jpg", ".jpeg", ".png", ".webp" ]
  },
  "Payments": {
    "VerificationHoldMinutes": 90,
    "RejectGraceMinutes": 10
  },
```

`src/HuebRun.Api/appsettings.Development.json` — เพิ่ม (ไม่มี key/secret):

```json
  "Minio": {
    "Endpoint": "192.168.155.208:9000",
    "Bucket": "huebrun-slips",
    "UseSsl": false
  },
```

- [ ] **Step 6: `ApiFactory` ใส่ config ทดสอบ + fake**

แทนทั้งไฟล์ `tests/HuebRun.Tests/Infrastructure/ApiFactory.cs`:

```csharp
using HuebRun.Infrastructure.Payments;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace HuebRun.Tests.Infrastructure;

public sealed class ApiFactory(string connectionString, bool jobsEnabled = false) : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));
    public FakeSlipVerifier Verifier { get; } = new();
    public InMemorySlipStorage Storage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("Jobs:OrderExpiry:Enabled", jobsEnabled ? "true" : "false");
        builder.UseSetting("Jobs:PaymentRecheck:Enabled", jobsEnabled ? "true" : "false");
        // ค่าปลอมให้ผ่าน ValidateOnStart — ตัวจริงถูกแทนด้วย fake ด้านล่าง
        builder.UseSetting("SlipOk:BranchId", "test");
        builder.UseSetting("SlipOk:ApiKey", "test");
        builder.UseSetting("Minio:Endpoint", "localhost:9000");
        builder.UseSetting("Minio:AccessKey", "test");
        builder.UseSetting("Minio:SecretKey", "test");
        builder.UseSetting("Minio:Bucket", "test");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
            s.RemoveAll<ISlipVerifier>();
            s.AddSingleton<ISlipVerifier>(Verifier);
            s.RemoveAll<ISlipStorage>();
            s.AddSingleton<ISlipStorage>(Storage);
        });
    }
}
```

- [ ] **Step 7: `PaymentController.cs`**

```csharp
using HuebRun.Domain;
using HuebRun.Infrastructure.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/orders/{orderId}/payment")]
public sealed class PaymentController(PaymentService payments, IOptions<MinioOptions> storage) : ControllerBase
{
    [HttpGet]
    public Task<PaymentInfo> Get(Guid orderId, CancellationToken ct) =>
        payments.GetPaymentInfoAsync(orderId, ct);

    [HttpPost("slip")]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024)]
    public async Task<IActionResult> UploadSlip(Guid orderId, IFormFile? file, CancellationToken ct)
    {
        if (file is null) throw PaymentErrors.InvalidFile("กรุณาแนบรูปสลิป");
        await using var stream = file.OpenReadStream();
        var image = await SlipFile.ReadAsync(file.FileName, file.Length, stream, storage.Value, ct);
        var result = await payments.SubmitSlipAsync(orderId, image, ct);
        return result.Status == PaymentStatus.Verified ? Ok(result) : Accepted(result);
    }
}
```

- [ ] **Step 8: `GET /orders/{id}` เพิ่ม payment ล่าสุด**

ใน `src/HuebRun.Api/Contracts/OrderContracts.cs` เปลี่ยน `OrderResponse` เป็น:

```csharp
public sealed record OrderResponse(
    Guid OrderId, string OrderNo, OrderStatus Status,
    decimal SubTotal, decimal DiscountAmount, decimal ShippingFee, decimal Total, decimal PayableAmount,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? PaidAt, string? PromoCode,
    OrderContactResponse Contact, OrderShippingResponse Shipping, IReadOnlyList<OrderRunnerResponse> Runners,
    OrderPaymentResponse? Payment);

public sealed record OrderPaymentResponse(int PaymentId, PaymentStatus Status, string? RejectReason);
```

ใน `OrdersController.Get` เพิ่มก่อน `return` และส่งเป็นอาร์กิวเมนต์สุดท้าย:

```csharp
        var payment = await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == orderId).OrderByDescending(p => p.Id)
            .Select(p => new OrderPaymentResponse(p.Id, p.Status, p.RejectReason))
            .FirstOrDefaultAsync(ct);
```

และปิดท้าย `new OrderResponse(...)` ด้วย `..., r.PricePaid, r.BibNumber)).ToList(), payment);`

- [ ] **Step 9: Run tests**

Run: `dotnet test tests/HuebRun.Tests`
Expected: PASS ทั้งหมด (รวม test เดิม)

- [ ] **Step 10: อัปเดต `docs/api-contract.md`**

- แทนบรรทัด `ยังไม่ทำ: GET /orders/{id}/payment, POST /orders/{id}/payment/slip (รอเลือกบริการตรวจสลิป)` ด้วยหัวข้อ:

```markdown
### GET `/orders/{orderId}/payment`

`200 { orderId, orderNo, amount, accountName, qrPayload, expiresAt, payment: { paymentId, status, rejectReason, createdAt } | null }`
error: `404 ORDER_NOT_FOUND`, `409 ORDER_ALREADY_PAID`, `409 ORDER_NOT_PAYABLE`

### POST `/orders/{orderId}/payment/slip`

multipart ฟิลด์ `file` (.jpg .jpeg .png .webp ไม่เกิน 5 MB)
- `200 { paymentId, status: "Verified", orderStatus: "Paid" }`
- `202 { paymentId, status: "Verifying", nextCheckAt }` — poll `GET /orders/{id}/payment`
- `202 { paymentId, status: "NeedsReview", reason }` — รอเจ้าหน้าที่
- `422 SLIP_REJECTED` details `{ paymentId, reason: INVALID_SLIP | AMOUNT_MISMATCH | WRONG_RECEIVER | DUPLICATE_SLIP }` — อัปโหลดใหม่ได้
- `400 VALIDATION_ERROR` (`details.file`), `404 ORDER_NOT_FOUND`, `409 ORDER_ALREADY_PAID`, `409 ORDER_NOT_PAYABLE`, `409 PAYMENT_IN_PROGRESS`, `409 SLIP_DUPLICATE`

`GET /orders/{id}` มีฟิลด์ `payment: { paymentId, status, rejectReason } | null` (ล่าสุด)
ออเดอร์ยอด 0 บาท (โค้ดฟรี + รับเอง) เป็น `Paid` ทันทีตอนสร้าง `payableAmount = 0`
```

- [ ] **Step 11: Commit**

```bash
git add src tests docs/api-contract.md
git commit -m "feat(api): payment info and slip upload endpoints"
```

---

## หลังทำครบ (ผู้ใช้ทำเอง)

```bash
dotnet user-secrets set "SlipOk:BranchId" "<branch id>" --project src/HuebRun.Api
dotnet user-secrets set "SlipOk:ApiKey" "<api key>" --project src/HuebRun.Api
dotnet user-secrets set "Minio:AccessKey" "<access key>" --project src/HuebRun.Api
dotnet user-secrets set "Minio:SecretKey" "<secret key>" --project src/HuebRun.Api
dotnet ef database update -p src/HuebRun.Infrastructure -s src/HuebRun.Api
```

สร้าง bucket `huebrun-slips` แบบ private ใน MinIO และผูกบัญชี PromptPay ของงานใน SlipOK LIFF
