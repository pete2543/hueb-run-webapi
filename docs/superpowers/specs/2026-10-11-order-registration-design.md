# HuebRun API — Order Registration (Design)

วันที่: 2026-10-11 · สถานะ: รอรีวิว · ต่อจาก `2026-10-11-api-foundation-design.md` (branch `feat/order-registration` แตกจาก `feat/api-foundation`)

## 1. เป้าหมาย

นักวิ่งส่งใบสมัคร (คนเดียวหรือกลุ่ม) ได้จริง โดยเซิร์ฟเวอร์คิดราคาเองและจองโควตา/สต็อกเสื้อ/โค้ดส่วนลดแบบ atomic ดูสถานะออเดอร์ได้ ออเดอร์ที่ไม่จ่ายภายในเวลาหมดอายุและคืนที่ให้คนอื่น และมี service ออกเลขบิบพร้อมให้ขั้นชำระเงินเรียกใช้

### ความสำเร็จวัดจาก

- แย่งที่นั่งสุดท้ายพร้อมกันแล้วไม่มีทางจองเกินโควตา (test ยิงพร้อมกันจริงบน Postgres)
- ยอดเงินคำนวณจากเซิร์ฟเวอร์เท่านั้น ตรงตามกติกาในหัวข้อ 4
- ออเดอร์หมดเวลาคืนโควตา/เสื้อ/โค้ดครบ และไม่คืนซ้ำ
- `MarkPaidAsync` ออกเลขบิบไม่ซ้ำ เรียงลำดับต่อระยะ และไม่ชนกับ job หมดเวลา

### นอกขอบเขต

- `GET /orders/{id}/payment` (QR PromptPay) และ `POST /orders/{id}/payment/slip` — รอเลือกบริการตรวจสลิป
- endpoint ที่ทำให้ออเดอร์เป็น `Paid` (มีแค่ service)
- LINE Login / แจ้งเตือน LINE, rate limiting, ตรวจคนสมัครซ้ำ, admin

## 2. การตัดสินใจ

| เรื่อง | ตัดสินใจ | เหตุผล |
|---|---|---|
| สิทธิ์ดูออเดอร์ | orderId (GUID v4) อย่างเดียว | ผู้ใช้เลือก; GUID เดาไม่ได้ ยอมรับความเสี่ยงลิงก์หลุด จึงไม่ส่ง `medicalNote`/ผู้ติดต่อฉุกเฉินใน GET |
| การจอง | `UPDATE ... WHERE ReservedCount + n <= Quota` ตรวจจำนวนแถวที่ถูกแก้ | atomic ในคำสั่งเดียว ไม่ต้อง retry แบบ xmin; check constraint เดิมเป็นด่านสำรอง |
| job หมดเวลา | `BackgroundService` รันทุก 1 นาที | ไม่เพิ่ม dependency/schema แอปเริ่มได้แม้ DB ล่ม; Hangfire เพิ่มตอนทำ outbox LINE |
| ออกเลขบิบ | ตัวนับ `EventDistance.LastBibNumber` เพิ่มด้วย `UPDATE ... RETURNING` | atomic ไม่ซ้ำ; unique index `(EventId, BibNumber)` เดิมเป็นด่านสำรอง |
| เลขออเดอร์ | Postgres sequence `order_no_seq` → `HR{yy}-{seq:D6}` (yy = `Event.Year % 100`) | ไม่ชนกันแม้สร้างพร้อมกัน |

## 3. Endpoint (base `/api/v1`)

### 3.1 `POST /events/{slug}/promo/validate`

คำขอ `{ "code": "EARLY10", "distanceIds": [1, 1, 2] }` (หนึ่งค่าต่อนักวิ่งหนึ่งคน)

- `200 { code, subTotal, discountAmount }` — `code` คืนตามที่เก็บใน DB
- ไม่เพิ่ม `UsedCount`
- error: event ไม่พบ → `404 EVENT_NOT_FOUND`; `distanceIds` ว่างหรือมีระยะที่ไม่ใช่ของงาน → `400 VALIDATION_ERROR`; `422 PROMO_INVALID` (`details.reason` = `NOT_FOUND` | `INACTIVE` | `NOT_STARTED` | `EXPIRED` | `MIN_ORDER_NOT_MET`); `409 PROMO_EXHAUSTED`

### 3.2 `POST /events/{slug}/orders`

คำขอตาม `docs/api-contract.md` (contact, runners[], shippingOptionId, shippingAddress, promoCode, acceptedPdpa)

ผล `201 { orderId, orderNo, total, payableAmount, expiresAt }` + header `Location: /api/v1/orders/{orderId}`

ขั้นตอน (ทั้งหมดใน transaction เดียว ระดับ Read Committed):

1. event published ไม่พบ → 404 `EVENT_NOT_FOUND`; ไม่อยู่ในช่วงเปิดรับ → `400 REGISTRATION_CLOSED`
2. validate (ไม่ผ่าน → `400 VALIDATION_ERROR` + `details` เป็น map ฟิลด์ → ข้อความ):
   - `acceptedPdpa == true`
   - contact: `name` 1–200 ตัว, `phone` ตัวเลข 9–10 หลัก (ยอม `-` และช่องว่างแล้วตัดทิ้ง), `email` ถ้ามีต้องเป็นรูปแบบอีเมล
   - runners 1–10 คน; แต่ละคน: `distanceId`/`shirtSizeId` เป็นของงานนี้, `firstName`/`lastName` 1–100, `gender` เป็นค่าของ enum, `birthDate` ไม่อยู่ในอนาคต, `nationalityId` active, `titleId`/`bloodTypeId`/`emergencyContact.relationId` ถ้ามีต้อง active, `emergencyContact.name`/`phone` ต้องมี
   - `shippingOptionId` เป็นของงานนี้และ active; ถ้าเป็น `Post` ต้องมี `shippingAddress` ครบ (`name`, `phone`, `line1`, `subdistrictId` ที่มีอยู่จริง, `postalCode` ตรงกับ `Subdistrict.ZipCode`)
3. คำนวณราคา (หัวข้อ 4)
4. จองแบบ atomic เรียงตาม Id เพื่อลด deadlock: ระยะ (`409 QUOTA_FULL` details `{ distanceId, name }`) → ไซซ์ (`409 SHIRT_OUT_OF_STOCK` details `{ shirtSizeId, size }`) → โค้ด (`409 PROMO_EXHAUSTED`) — ล้มที่ใดก็ rollback ทั้งหมด
5. `PayableAmount` (หัวข้อ 5)
6. สร้าง Order (`Pending`, `ExpiresAt = now + Event.ReservationMinutes`, snapshot ที่อยู่จาก master) และ Registration ต่อคน (`PricePaid` = ราคาระยะหลัง early bird, `CheckInToken` = 32 byte สุ่ม base64url)
7. commit; ถ้าชน unique index ของ `PayableAmount` → เริ่ม transaction ใหม่ทั้งหมด สูงสุด 3 ครั้ง แล้วจึงตอบ `409 PAYMENT_SLOT_BUSY`

### 3.3 `GET /orders/{orderId}`

```
{ orderId, orderNo, status, subTotal, discountAmount, shippingFee, total, payableAmount,
  createdAt, expiresAt, paidAt, promoCode,
  contact: { name, phone, email },
  shipping: { type, name, address: { name, phone, line1, subdistrict, district, province, postalCode } | null, trackingNo },
  runners: [ { registrationId, titleId, firstName, lastName, distance: { id, name }, shirtSize: { id, size }, pricePaid, bibNumber } ] }
```

- ไม่ส่ง `medicalNote`, ผู้ติดต่อฉุกเฉิน, `checkInToken`
- `status` = `Expired` ถ้าใน DB เป็น `Pending` แต่ `now >= ExpiresAt` (job ยังไม่ได้รัน)
- ไม่พบ → `404 ORDER_NOT_FOUND`

### 3.4 `GET /orders/{orderId}/tickets`

- `Paid` → `200 [ { registrationId, firstName, lastName, distanceName, bibNumber, checkInToken } ]` (ไม่รวม registration ที่ `IsCancelled`)
- สถานะอื่น → `409 ORDER_NOT_PAID` details `{ status }`; ไม่พบ → `404 ORDER_NOT_FOUND`

## 4. กติกาคำนวณราคา

- `SubTotal` = ผลรวม `EventDistance.GetEffectivePrice(now)` ของทุกนักวิ่ง
- โค้ดส่วนลด (เทียบแบบไม่สนตัวพิมพ์, ตัดช่องว่างหัวท้าย; ว่าง = ไม่ใช้โค้ด) ใช้ได้เมื่อ: มีอยู่ใน event, `IsActive`, `StartsAt <= now` (ถ้ามี), `now < EndsAt` (ถ้ามี), `SubTotal >= MinOrderAmount` (ถ้ามี), `MaxUses == null || UsedCount < MaxUses`
- `Percent`: `round(SubTotal × Value / 100, 2, AwayFromZero)`; `Fixed`: `Value`; ทั้งสองแบบไม่เกิน `SubTotal`
- `ShippingFee` = `ShippingOption.Fee` (ต่อออเดอร์)
- `Total = SubTotal − DiscountAmount + ShippingFee`
- กติกาอยู่ในคลาส pure `OrderPricing` (Domain) ใช้ร่วมกันระหว่าง promo/validate และสร้างออเดอร์

## 5. PayableAmount

- `PayableAmount = Total + satang/100` โดย `satang` ∈ 1..99 ค่าน้อยสุดที่ยังไม่มีออเดอร์ `Pending` ของ event เดียวกันใช้ (`PayableAmount` ในช่วง `Total+0.01 .. Total+0.99`)
- ไม่มีช่องว่าง → `409 PAYMENT_SLOT_BUSY`
- ชนกันตอน insert (สองคำขอเลือกค่าเดียวกันพร้อมกัน) → retry ตามข้อ 3.2.7
- `Total = 0` (โค้ดลด 100% + รับเอง) ยังได้เศษสตางค์ตามกติกาเดียวกัน — ให้รอบ payment ตัดสินว่าจะยกเว้นการจ่ายหรือไม่

## 6. ออกเลขบิบ — `OrderPaymentService.MarkPaidAsync(Guid orderId, DateTime paidAtUtc)`

- ใน transaction: `UPDATE Orders SET Status='Paid', PaidAt=... WHERE Id=@id AND Status='Pending'` (ไม่ดู ExpiresAt — เงินเข้าแล้วถือว่ารับ ตราบใดที่ job ยังไม่คืนโควตา)
- แก้ได้ 1 แถว → จัดกลุ่ม registration ตามระยะ เรียงตาม `Registration.Id` แล้วจองเลขด้วย `UPDATE "EventDistances" SET "LastBibNumber" = "LastBibNumber" + n WHERE "Id" = @id RETURNING "LastBibNumber"` แล้วตั้ง `BibNumber = BibPrefix + number:D4` (เช่น `A0001`)
- แก้ได้ 0 แถว: ถ้า status เป็น `Paid` อยู่แล้ว → คืนผลเดิม (idempotent); สถานะอื่นหรือไม่พบ → throw `InvalidOperationException` ให้ขั้น payment จัดการ (เช่นคืนเงิน)
- คืนค่า `MarkPaidResult { bool AlreadyPaid }`
- schema: เพิ่มคอลัมน์ `EventDistance.LastBibNumber int not null default 0` + sequence `order_no_seq` ใน migration `OrderRegistration`

## 7. Job หมดเวลา — `OrderExpiryService` + `OrderExpiryWorker`

- `OrderExpiryService.ExpireDueOrdersAsync(CancellationToken)` (Infrastructure, test เรียกตรง): หา `Pending` ที่ `ExpiresAt <= now` ครั้งละไม่เกิน 200 ออเดอร์ แต่ละออเดอร์ใน transaction ของตัวเอง:
  - `UPDATE Orders SET Status='Expired' WHERE Id=@id AND Status='Pending'`
  - ถ้าแก้ได้ 1 แถว: ลด `ReservedCount` ของระยะ/ไซซ์ตามจำนวน registration, ลด `PromoCode.UsedCount` 1 ถ้ามีโค้ด
  - คืนจำนวนออเดอร์ที่หมดเวลา
- `OrderExpiryWorker : BackgroundService` เรียก service ทุก 60 วินาทีผ่าน `PeriodicTimer`; exception ถูก log แล้วรอบถัดไปทำต่อ; เปิด/ปิดด้วย config `Jobs:OrderExpiry:Enabled` (ค่าเริ่ม `true`; `ApiFactory` ของ test ตั้ง `false` เพื่อไม่ให้รันพร้อม test)

## 8. โครงสร้างโค้ด

- Domain: `OrderPricing` (pure), `PromoRejection` enum
- Infrastructure: `OrderService` (สร้างออเดอร์ + validate promo), `OrderPaymentService`, `OrderExpiryService`, migration `OrderRegistration`
- Api: `RegistrationController` (`/events/{slug}/promo/validate`, `/events/{slug}/orders`), `OrdersController` (`/orders/...`), `Contracts/OrderContracts.cs`, `OrderExpiryWorker`
- ข้อผิดพลาดทางธุรกิจจาก service โยนเป็น `OrderRuleException(int statusCode, string code, string message, object? details)` (Infrastructure) แล้ว `AppExceptionHandler` ตอบในรูปแบบ error กลางเหมือน `AppException` — Infrastructure ไม่อ้าง Api

## 9. การทดสอบ

- **Unit (`OrderPricing`)**: early bird ผสมหลายระยะ, Percent ปัดสตางค์, Fixed เกินยอด → ไม่ติดลบ, ทุก `PromoRejection`, ค่าส่ง
- **Integration (Testcontainers)**:
  - สร้างออเดอร์สำเร็จ: ยอด, `ReservedCount` เพิ่ม, `UsedCount` เพิ่ม, snapshot ที่อยู่, `Location` header
  - ทุก error code ในหัวข้อ 3 และ validation หลัก (PDPA, runners 0/11 คน, ระยะของงานอื่น, รหัสไปรษณีย์ไม่ตรงตำบล)
  - **แย่งพร้อมกัน**: เหลือ 5 ที่ ยิง 20 คำขอพร้อมกัน → 201 ห้าครั้ง 409 `QUOTA_FULL` สิบห้าครั้ง, `ReservedCount == Quota`
  - `PayableAmount`: สองออเดอร์ยอดเท่ากันได้เศษต่างกัน; ออเดอร์ที่ไม่ใช่ Pending ไม่กันเศษ; สร้างพร้อมกันยอดเท่ากันไม่ล้ม
  - GET order: ไม่มี `medicalNote`/ผู้ติดต่อฉุกเฉิน; status `Expired` เมื่อเลยเวลา; 404
  - tickets: 409 เมื่อยังไม่จ่าย; หลัง `MarkPaidAsync` ได้บิบ
  - `MarkPaidAsync`: บิบเรียง `A0001..`, สองออเดอร์พร้อมกันบิบไม่ซ้ำ, เรียกซ้ำ idempotent, ออเดอร์ Expired → throw
  - `ExpireDueOrdersAsync`: คืนโควตา/ไซซ์/โค้ด; รันซ้ำไม่คืนซ้ำ; ออเดอร์ที่ยังไม่ถึงเวลาไม่ถูกแตะ; แข่งกับ `MarkPaidAsync` มีฝั่งเดียวชนะและยอดจองถูกต้อง

## 10. ความเสี่ยง / ยังไม่ทำ

- ไม่มี rate limit: bot จองค้างได้ครั้งละ 30 นาที — ต้องทำก่อนเปิดรับจริง (ร่วมกับ Cloudflare)
- ไม่ตรวจคนสมัครซ้ำ
- GUID อย่างเดียว: ลิงก์หลุด = เห็นชื่อ/เบอร์/ที่อยู่ — ทบทวนเมื่อทำ LINE Login
- job หมดเวลาหน่วงได้ถึง ~1 นาที (GET แสดง `Expired` ทันทีอยู่แล้ว แต่โควตาคืนช้าสุด 1 นาที)
