# HuebRun API — Payment ด้วยสลิป (Design)

วันที่: 2026-10-11 · สถานะ: รอรีวิว · ต่อจาก `2026-10-11-order-registration-design.md` (branch `feat/payment` แตกจาก `feat/order-registration`)

## 1. เป้าหมาย

นักวิ่งเห็น QR PromptPay ของออเดอร์ โอนเงิน แล้วอัปโหลดสลิป ระบบตรวจสลิปกับ SlipOK เก็บรูปไว้ใน MinIO และถ้าผ่านให้ออเดอร์เป็น `Paid` พร้อมออกเลขบิบอัตโนมัติ สลิปที่ตรวจทันทีไม่ได้ (ธนาคารหน่วง/ระบบล่ม) ถูกตรวจซ้ำโดย background job

### ความสำเร็จวัดจาก

- สลิปที่ถูกต้อง ยอดตรง `PayableAmount` → ออเดอร์ `Paid` + บิบ ใน request เดียว
- ไม่มีทางที่ออเดอร์เป็น `Paid` โดยไม่มี Payment `Verified` (หรือ `Free`) ที่ยอดตรง และกลับกัน (อัปเดตใน transaction เดียว)
- สลิปเดียวกันใช้จ่ายได้ครั้งเดียว (รูปซ้ำ, `transRef` ซ้ำ, SlipOK แจ้งซ้ำ)
- ออเดอร์ไม่หมดเวลาระหว่างรอตรวจสลิป และ job หมดเวลาไม่ชนกับการอัปโหลด
- ออเดอร์ยอด 0 บาทเป็น `Paid` ทันทีตอนสร้าง

### นอกขอบเขต

- หน้า/endpoint แอดมินสำหรับคิว `NeedsReview` (approve/reject) และ presigned URL ดูสลิป — เฟส admin
- แจ้งเตือน LINE, รับสลิปผ่านแชท LINE OA (webhook forwarding) — เฟส LINE; ตัวตรวจสลิปออกแบบให้ไม่ผูกช่องทาง
- payment gateway (Omise/2C2P) และ interface กลางของ gateway — เฟส 2
- คืนเงิน, rate limit

## 2. การตัดสินใจ

| เรื่อง | ตัดสินใจ | เหตุผล |
|---|---|---|
| บริการตรวจสลิป | SlipOK (API guide v1.13) ส่งรูปแบบ `files` พร้อม `log: true` และ `amount` | ฟรี 100 สลิป/เดือน; `log: true` ให้ SlipOK กันสลิปซ้ำ (1012) และเช็กบัญชีผู้รับที่ผูกใน LIFF (1014) โดยไม่คิดโควตาซ้ำ |
| บัญชีผู้รับ | ผูกบัญชี PromptPay ของงานใน LINE LIFF ของ SlipOK (ทำมือ) | ไม่ต้องเทียบเลขบัญชีที่ถูก mask เอง; ต้องอัปเดต LIFF เมื่อเปลี่ยนบัญชีของงาน |
| เวลาตรวจ | ตรวจทันทีใน request; ถ้าได้ผลไม่ชัด → `Verifying` แล้ว job ตรวจซ้ำ | ส่วนใหญ่ได้ผลทันที; job จำเป็นอยู่แล้วสำหรับ 1010 (BBL/SCB หน่วง) |
| สลิปมาหลังออเดอร์หมดเวลา | ตรวจตามปกติ ถ้าผ่าน → `NeedsReview` (`ORDER_EXPIRED`) ไม่จองโควตาใหม่ | กรณีน้อย เรื่องเงินให้คนตัดสิน (เปิดออเดอร์ใหม่หรือคืนเงิน) |
| ยอด 0 บาท | `Paid` + บิบทันทีตอนสร้าง ไม่บวกเศษสตางค์ บันทึก Payment provider `Free` | ใช้โค้ดฟรี 100% (แขก/สปอนเซอร์) ได้ ไม่ต้องโอน 0.xx บาท |
| กันหมดเวลาระหว่างตรวจ | ยืด `Order.ExpiresAt` ด้วย update แบบมีเงื่อนไขตอนรับสลิป (hold) | ใช้กลไกหมดเวลาเดิม ไม่ต้องให้ job/`GetEffectiveStatus` รู้จัก Payment; atomic ต่อแถว |
| ที่เก็บรูป | MinIO ผ่านแพ็กเกจ `Minio` bucket แยก private | ตรงกับ config ที่ผู้ใช้มีอยู่; สลิปเป็นข้อมูลการเงินส่วนบุคคล ห้าม public |
| Interface | `ISlipVerifier` เฉพาะสลิป (แทน `IPaymentProvider` ใน plan.md) | สลิปกับ gateway ทำงานต่างกัน (ส่งรูปตรวจ vs สร้าง charge + รอ webhook) interface เดียวจะไม่เข้ากับทั้งคู่ |

## 3. Endpoint (base `/api/v1`)

### 3.1 `GET /orders/{orderId}/payment`

```
{ orderId, orderNo, amount, accountName, qrPayload, expiresAt,
  payment: { paymentId, status, rejectReason, createdAt } | null }
```

- `amount` = `PayableAmount`; `accountName` = `Event.PromptPayAccountName`; `qrPayload` = string EMVCo จาก `PromptPayPayload` (หน้าเว็บวาด QR เอง) — ไม่ส่ง `PromptPayId` แยก (อยู่ใน payload อยู่แล้ว)
- `payment` = Payment ล่าสุดของออเดอร์ (ไว้ให้หน้าเว็บแสดงผลตรวจล่าสุด)
- error: ไม่พบ → `404 ORDER_NOT_FOUND`; `Paid` → `409 ORDER_ALREADY_PAID`; สถานะที่มีผลเป็น `Expired`/`Cancelled`/`Refunded` → `409 ORDER_NOT_PAYABLE` details `{ status }`

### 3.2 `POST /orders/{orderId}/payment/slip`

คำขอ `multipart/form-data` ฟิลด์ `file` (รูปเดียว)

ผล:
- `200 { paymentId, status: "Verified", orderStatus: "Paid" }`
- `202 { paymentId, status: "Verifying", nextCheckAt }` — รอ job ตรวจซ้ำ หน้าเว็บ poll `GET /orders/{id}/payment`
- `202 { paymentId, status: "NeedsReview", reason }` — เงินดูเหมือนเข้าแต่ต้องให้เจ้าหน้าที่ตรวจ
- `422 SLIP_REJECTED` details `{ paymentId, reason }` โดย `reason` ∈ `INVALID_SLIP` | `AMOUNT_MISMATCH` | `WRONG_RECEIVER` | `DUPLICATE_SLIP` — Payment ถูกบันทึกเป็น `Rejected` แล้ว อัปโหลดใหม่ได้

error ก่อนตรวจ (ไม่บันทึก Payment, ไม่เรียก SlipOK):
- ไม่มีไฟล์, เกิน `Minio:MaxFileSizeBytes`, นามสกุลไม่อยู่ใน `AllowedExtensions` หรือ magic bytes ไม่ใช่ JPEG/PNG/WebP → `400 VALIDATION_ERROR` (`details.file`)
- ไม่พบ → `404 ORDER_NOT_FOUND`; `Paid` → `409 ORDER_ALREADY_PAID`; `Cancelled`/`Refunded` → `409 ORDER_NOT_PAYABLE`
- มี Payment `Verifying` ของออเดอร์นี้อยู่ → `409 PAYMENT_IN_PROGRESS`
- `SlipHash` (SHA-256 ของไฟล์) มีอยู่แล้ว → `409 SLIP_DUPLICATE`

หมายเหตุ: ออเดอร์ที่มีผลเป็น `Expired` **ยังรับสลิป** (ตามตัดสินใจหัวข้อ 2)

### 3.3 `GET /orders/{orderId}` (แก้ของเดิม)

เพิ่มฟิลด์ `payment: { paymentId, status, rejectReason } | null` (Payment ล่าสุด)

## 4. ข้อมูล

migration ใหม่ `PaymentSlip`:

- `Payment.SlipImageUrl` → เปลี่ยนชื่อเป็น `SlipObjectKey` (เก็บ object key ไม่เก็บ URL)
- เพิ่ม `Payment.TransferredAt timestamp null` (จาก `transTimestamp` ของสลิป), `Payment.NextCheckAt timestamp null`, `Payment.CheckAttempts int not null default 0`
- partial unique index `(OrderId) WHERE Status = 'Verifying'` — ออเดอร์มีสลิปรอตรวจได้ทีละใบ
- index `(NextCheckAt) WHERE Status = 'Verifying'` สำหรับ job
- index เดิมคงไว้: unique `SlipHash`, unique `SlipTransRef`

object key: `slips/{eventId}/{orderId}/{paymentId}.{ext}`

`Payment.Provider` = `"SlipOK"` หรือ `"Free"`; `ExpectedAmount` = `Order.PayableAmount` ตอนสร้าง; `ProviderResponse` = JSON ดิบจาก SlipOK ครั้งล่าสุด

`RejectReason` (string) ใช้ค่าเดียวกับ `reason` ใน API: `INVALID_SLIP`, `AMOUNT_MISMATCH`, `WRONG_RECEIVER`, `DUPLICATE_SLIP`, `ORDER_EXPIRED`, `DUPLICATE_UNKNOWN`, `VERIFY_TIMEOUT`

## 5. ขั้นตอนอัปโหลดสลิป — `PaymentService.SubmitSlipAsync`

1. validate ไฟล์ + โหลดออเดอร์ + ตรวจเงื่อนไขก่อนตรวจ (หัวข้อ 3.2) คำนวณ `SlipHash`
2. **transaction A**:
   - hold: `UPDATE Orders SET ExpiresAt = now + VerificationHoldMinutes WHERE Id=@id AND Status='Pending' AND ExpiresAt > now AND ExpiresAt < now + VerificationHoldMinutes` (ไม่แก้ได้ = หมดเวลาแล้วหรือ hold อยู่แล้ว ไม่เป็นไร)
   - insert Payment `Verifying` (`ExpectedAmount`, `SlipHash`, `SlipObjectKey`, `CheckAttempts = 0`) — ชน unique `SlipHash` → `409 SLIP_DUPLICATE`; ชน partial unique `Verifying` → `409 PAYMENT_IN_PROGRESS`
   - commit
3. อัปโหลดรูปขึ้น MinIO (ล้ม → ลบ Payment ที่เพิ่งสร้าง แล้วตอบ 500 ตามปกติ)
4. เรียก `ISlipVerifier.VerifyAsync(image, ExpectedAmount)` แล้วนำผลเข้า `ApplyResultAsync` (หัวข้อ 6) — ขั้นนี้ใช้ร่วมกับ job ตรวจซ้ำ

`VerificationHoldMinutes` = 90 (config `Payments:VerificationHoldMinutes`) ครอบเวลาตรวจซ้ำสูงสุดในหัวข้อ 7

## 6. นำผลตรวจไปใช้ — `ApplyResultAsync`

**transaction B** ต่อ Payment หนึ่งใบ (ล็อกแถว Payment ด้วย `SELECT ... FOR UPDATE` ก่อน; ถ้าไม่ใช่ `Verifying` แล้ว = มีคนจัดการไปแล้ว → จบ):

| ผลจาก verifier | การทำงาน |
|---|---|
| `Verified` และ `amount == ExpectedAmount` และ `transRef` ยังไม่มี Payment อื่นใช้ และออเดอร์ `Pending` | Payment `Verified` (`PaidAmount`, `SlipTransRef`, `TransferredAt`, `VerifiedAt`) + `MarkPaidAsync(orderId, verifiedAt)` ใน transaction เดียวกัน |
| `Verified` แต่ยอดไม่ตรง (กันไว้ซ้ำแม้ส่ง `amount` แล้ว) | `Rejected` `AMOUNT_MISMATCH` |
| `Verified` แต่ `transRef` ซ้ำกับ Payment อื่น | `Rejected` `DUPLICATE_SLIP` |
| `Verified` แต่ออเดอร์ไม่ใช่ `Pending` (หมดเวลาไปแล้ว) | `NeedsReview` `ORDER_EXPIRED` (เก็บ `SlipTransRef`/`PaidAmount` ไว้) |
| `Duplicate` (1012) และ `transRef` ในข้อมูลที่แนบมาไม่ตรงกับ Payment ไหนของเรา | `NeedsReview` `DUPLICATE_UNKNOWN` — เช่น ครั้งก่อน SlipOK บันทึกแล้วแต่เราไม่ได้รับคำตอบ (timeout) |
| `Duplicate` (1012) และ `transRef` ตรงกับ Payment อื่นของเรา | `Rejected` `DUPLICATE_SLIP` |
| `Rejected(reason)` | `Rejected` + reason |
| `Retry(after)` / `Unavailable` | คง `Verifying`, `CheckAttempts += 1`, `NextCheckAt = now + after` (Unavailable ใช้ backoff 1, 2, 5, 10, 15, 30 นาที) |

`MarkPaidAsync` ต้องใช้ transaction ที่เปิดอยู่ได้: ถ้า `db.Database.CurrentTransaction != null` ใช้ตัวนั้นและไม่ commit เอง (พฤติกรรมเดิมไม่เปลี่ยนเมื่อเรียกเดี่ยวๆ)

กรณี `Rejected` และออเดอร์ยัง `Pending`: ปลด hold โดยตั้ง `ExpiresAt = now + RejectGraceMinutes` (ค่าเริ่ม 10 นาที) ให้อัปโหลดใหม่ได้ทัน — ไม่คืนค่าเดิมก่อน hold เพราะไม่ได้เก็บไว้ (ยอมให้ได้เวลาเพิ่มเล็กน้อย)

กรณี `NeedsReview`: ไม่แตะ `ExpiresAt` — ออเดอร์ที่ยัง `Pending` จะหมดเวลาเมื่อ hold หมด แล้วรอแอดมินตัดสินเหมือนกรณี `ORDER_EXPIRED`

## 7. ตรวจซ้ำ — `PaymentRecheckService` + `PaymentRecheckWorker`

- `RecheckDueAsync(CancellationToken)` (Infrastructure, test เรียกตรง): หา Payment `Verifying` ที่ `NextCheckAt <= now` ครั้งละไม่เกิน 50 ใบ
- จองงานต่อใบ: `UPDATE Payments SET NextCheckAt = now + 5 นาที WHERE Id=@id AND Status='Verifying' AND NextCheckAt = @old` (แก้ไม่ได้ = เครื่องอื่นหยิบไปแล้ว) — รันหลาย instance ได้
- ดาวน์โหลดรูปจาก MinIO → `VerifyAsync` → `ApplyResultAsync`
- ถ้า `CheckAttempts >= 6` หรือ `now - CreatedAt > 60 นาที` และยังไม่ได้ผล → `NeedsReview` `VERIFY_TIMEOUT`
- error ต่อใบถูก log แล้วข้าม (แบบเดียวกับ `OrderExpiryService`)
- `PaymentRecheckWorker : BackgroundService` ทุก 60 วินาที; config `Jobs:PaymentRecheck:Enabled` (test ตั้ง `false`)

## 8. แก้ของเดิม

- `OrderExpiryService.ExpireAsync`: เงื่อนไข update เพิ่ม `AND ExpiresAt <= now` — กัน job ที่เลือกออเดอร์ไว้แล้วมาทับ hold ที่เพิ่งยืด
- `OrderService.CreateOrderAsync`: ถ้า `Total == 0` → `PayableAmount = 0` (ไม่เรียก `PickPayableAmountAsync`), สร้าง Payment `Free`/`Verified` แล้ว `MarkPaidAsync` ใน transaction เดียวกับการสร้าง; ผล `201` เหมือนเดิม (`payableAmount = 0`)
- `GET /orders/{id}` เพิ่ม `payment` (หัวข้อ 3.3)
- `docs/api-contract.md` อัปเดต endpoint payment และ error code ใหม่

## 9. SlipOK — `SlipOkVerifier : ISlipVerifier`

- `POST {BaseUrl}/api/line/apikey/{BranchId}` header `x-authorization: {ApiKey}` multipart: `files`, `log=true`, `amount`
- typed `HttpClient` timeout `SlipOk:TimeoutSeconds` (ค่าเริ่ม 15)

ผลลัพธ์ `SlipCheckResult` (record ปิดชุด): `Verified(transRef, amount, transferredAt, rawJson)`, `Duplicate(transRef?, rawJson)`, `Rejected(reason, rawJson)`, `Retry(after, rawJson)`, `Unavailable(rawJson?)`

| SlipOK | ผล |
|---|---|
| 200 `success: true` | `Verified` |
| 1012 สลิปซ้ำ | `Duplicate` (อ่าน `data.transRef` ถ้ามี) |
| 1013 ยอดไม่ตรง | `Rejected AMOUNT_MISMATCH` |
| 1014 บัญชีผู้รับไม่ตรง | `Rejected WRONG_RECEIVER` |
| 1000, 1005, 1006, 1007, 1008, 1011 | `Rejected INVALID_SLIP` |
| 1010 ธนาคารหน่วง | `Retry(data.delay นาที)` |
| 1009 ระบบธนาคารขัดข้อง | `Retry(15 นาที)` |
| 1001, 1002, 1003, 1004, 1015, 5xx, timeout, network | `Unavailable` + log error (config/แพ็กเกจผิด ต้องมีคนดู) |

## 10. MinIO — `MinioSlipStorage : ISlipStorage`

- `PutAsync(key, stream, contentType)`, `GetAsync(key)`, `DeleteAsync(key)` — ยังไม่ทำ presigned URL (เฟส admin)
- config `Minio` ตามที่ผู้ใช้ให้ แต่: bucket แยกสำหรับโปรเจกต์นี้ (private), `AllowedExtensions` = `.jpg .jpeg .png .webp` (SlipOK ไม่รับ gif/HEIC)
- เช็ก magic bytes: JPEG `FF D8 FF`, PNG `89 50 4E 47`, WebP `RIFF....WEBP`

## 11. QR PromptPay — `PromptPayPayload` (Domain, pure)

- EMVCo: `00` = `01`, `01` = `12` (dynamic), `29` = AID `A000000677010111` + เลข PromptPay (`01` เบอร์โทร 10 หลัก → `0066` + 9 หลัก, `02` เลขบัตร/ภาษี 13 หลัก, `03` e-wallet 15 หลัก), `53` = `764`, `54` = ยอด 2 ตำแหน่ง, `58` = `TH`, `63` = CRC16-CCITT (init `0xFFFF`, poly `0x1021`)
- `PromptPayId` รูปแบบไม่ถูกต้อง → `InvalidOperationException` (เป็นปัญหา config ของงาน)

## 12. Config และ secret

```
"SlipOk":   { "BaseUrl": "https://api.slipok.com", "BranchId": "", "ApiKey": "", "TimeoutSeconds": 15 },
"Minio":    { "Endpoint", "AccessKey", "SecretKey", "Bucket", "UseSsl", "MaxFileSizeBytes", "AllowedExtensions" },
"Payments": { "VerificationHoldMinutes": 90, "RejectGraceMinutes": 10 },
"Jobs":     { "PaymentRecheck": { "Enabled": true } }
```

- `ApiKey`, `BranchId`, `AccessKey`, `SecretKey` อยู่ใน user-secrets (dev) / env var (prod) เท่านั้น ห้าม commit
- ค่าที่ไม่ลับของ MinIO dev อยู่ใน `appsettings.Development.json`
- ตรวจ config ตอนเริ่มแอป (`ValidateOnStart`) — ขาด key แล้วแอปไม่ขึ้น ดีกว่าไปพังตอนนักวิ่งอัปสลิป

## 13. โครงสร้างโค้ด

- Domain: `PromptPayPayload`, `PaymentStatus` เดิม
- Infrastructure/Payments: `ISlipVerifier`, `SlipCheckResult`, `SlipOkVerifier`, `SlipOkOptions`, `ISlipStorage`, `MinioSlipStorage`, `MinioOptions`, `SlipFile` (validate + magic bytes + hash), `PaymentService`, `PaymentRecheckService`, `PaymentErrors`; migration `PaymentSlip`
- Api: `PaymentController` (`/orders/{orderId}/payment`, `/orders/{orderId}/payment/slip`), `Contracts/PaymentContracts.cs`, `Jobs/PaymentRecheckWorker`
- Tests: `FakeSlipVerifier` (คิวผลลัพธ์ที่กำหนด), `InMemorySlipStorage`

## 14. การทดสอบ

- **Unit**:
  - `PromptPayPayload`: payload + CRC ตรงกับตัวอย่างที่รู้ค่า (เบอร์โทร, เลขบัตร), ยอดทศนิยม 2 ตำแหน่ง, รูปแบบผิด → throw
  - `SlipOkVerifier` กับ fake `HttpMessageHandler`: ทุกแถวในตารางหัวข้อ 9, header/ฟิลด์ multipart ถูกต้อง
  - `SlipFile`: นามสกุลไม่ตรง, magic bytes ไม่ตรง, เกินขนาด
- **Integration (Testcontainers + Fake verifier/storage)**:
  - สลิปผ่าน → Payment `Verified`, Order `Paid`, มีบิบ, `200`
  - ยอดไม่ตรง / บัญชีผิด / สลิปปลอม → `422 SLIP_REJECTED`, Order ยัง `Pending`, `ExpiresAt` ได้ grace, อัปโหลดใหม่ได้
  - รูปซ้ำ → `409 SLIP_DUPLICATE` และ verifier ไม่ถูกเรียก; `transRef` ซ้ำข้ามออเดอร์ → `DUPLICATE_SLIP`
  - ระหว่าง `Verifying` อัปโหลดอีก → `409 PAYMENT_IN_PROGRESS`
  - `Retry` → `202`; เลื่อนเวลาแล้ว `RecheckDueAsync` ได้ `Verified` → `Paid`
  - ตรวจซ้ำครบ 6 ครั้ง → `NeedsReview VERIFY_TIMEOUT`
  - hold: อัปโหลดตอนเหลือ 1 นาที แล้วเลื่อนเวลาเกิน `ExpiresAt` เดิม → `ExpireDueOrdersAsync` ไม่ทำให้หมดเวลา
  - สลิปมาหลังหมดเวลา → `NeedsReview ORDER_EXPIRED`, Order ยัง `Expired`, โควตาไม่ถูกจองใหม่
  - `Duplicate` ที่ไม่รู้จัก → `NeedsReview DUPLICATE_UNKNOWN`
  - แข่งกัน: job หมดเวลา vs อัปโหลดสลิปพร้อมกัน — ผลลัพธ์สอดคล้อง (ไม่มี Order `Expired` ที่มี Payment `Verified`)
  - ออเดอร์ยอด 0 → `Paid` ตั้งแต่สร้าง, มี Payment `Free`, `GET /payment` → `409 ORDER_ALREADY_PAID`
  - `GET /orders/{id}` มี `payment` ล่าสุด
  - MinIO อัปโหลดล้ม → ไม่เหลือ Payment ค้าง `Verifying`

## 15. ความเสี่ยง / ยังไม่ทำ

- `NeedsReview` ยังไม่มีหน้าแอดมิน ช่วงแรกต้องดูจาก DB และจัดการมือ
- บัญชีใน SlipOK LIFF ต้องตรงกับ `Event.PromptPayId` — ถ้าลืมแก้ตอนเปลี่ยนงาน ทุกสลิปจะโดน `WRONG_RECEIVER`
- โควตาฟรี 100 สลิป/เดือน ไม่พอตอนเปิดรับจริง ต้องซื้อแพ็กเกจก่อน (หมดแล้วได้ 1003/1004 → `Unavailable` → `NeedsReview`)
- MinIO dev อยู่ใน LAN (`192.168.x`) — prod ต้องเข้าถึงได้จากเครื่อง API
- ยังไม่มี rate limit บน endpoint อัปโหลด (สลิปละ 1 โควตา SlipOK) — ทำพร้อม rate limit ของระบบ
