# Admin API — คู่มือสำหรับหน้าบ้าน

อัปเดต: 2026-10-12 · branch `feat/admin-auth` · สัญญาแบบย่ออยู่ที่ `docs/api-contract.md` (หัวข้อ Admin) ไฟล์นี้ลงรายละเอียดรูปแบบ request/response ที่ทำเสร็จแล้ว

ทำแล้ว: auth, ผู้ใช้, ตรวจสลิป, ออเดอร์, ตั้งค่างาน, โค้ดส่วนลด, แดชบอร์ด, นักวิ่ง/export, เลขพัสดุ, เช็กอิน, คืนเงิน, audit, ประกาศ (LINE)
ยังไม่มี (อย่าเรียก): –

## 1. กติกากลาง

- Base path: `/api/v1/admin/...` · JSON camelCase · enum เป็น string · เวลา UTC แบบ ISO 8601 (`2026-11-15T00:00:00Z`) · เงินเป็นตัวเลข 2 ตำแหน่ง
- ส่งเวลาเข้า API ต้องมีเขตเวลา (เช่น ลงท้าย `Z`) ไม่งั้น `400`
- เรียกจากฝั่ง server (server component / action) เท่านั้น เก็บ token ใน session cookie เดิม แล้วส่ง `Authorization: Bearer <token>` ทุก request
- Error ทุกแบบ: `{ "code": "...", "message": "ข้อความไทย", "details": ... }`
  - `400 VALIDATION_ERROR` — `details` = `{ "ชื่อฟิลด์": ["ข้อความ"] }` (ฟิลด์ใน array ใช้ `distances[0].quota`)
  - `401 UNAUTHORIZED` — ไม่มี/หมดอายุ/ถูกเพิกถอน token → ล้าง session แล้วไป login
  - `403 FORBIDDEN` — บทบาทไม่พอ หรือไม่มีสิทธิ์งานนั้น
  - `429 TOO_MANY_REQUESTS` — login (10 ครั้ง/นาที/IP) · ฝั่งนักวิ่ง: สร้างออเดอร์ 10, อัปสลิป 20 และตรวจโค้ดส่วนลด 30 ครั้ง/10 นาที/IP · มี header `Retry-After` (วินาที)
- รายการแบ่งหน้า: query `page` (เริ่ม 1), `pageSize` (ค่าเริ่ม 20, สูงสุด 100) → `{ items, page, pageSize, total }`
- **สิทธิ์ต้องตรงกับตารางนี้** (เช็กเมนู/ปุ่มฝั่งหน้าบ้านด้วย แต่ API เป็นตัวตัดสิน)

| กลุ่ม | SuperAdmin | Organizer | Finance | Staff |
|---|:-:|:-:|:-:|:-:|
| แดชบอร์ด, ออเดอร์ | ✓ | ✓ | ✓ | |
| ตรวจสลิป | ✓ | | ✓ | |
| ตั้งค่างาน (อ่าน/แก้), export, เลขพัสดุ, ยกเลิกเช็กอิน | ✓ | ✓ | | |
| รายชื่อนักวิ่ง | ✓ | ✓ | ✓ | |
| เช็กอิน (สแกน/ยืนยัน/รายการ) | ✓ | ✓ | | ✓ |
| สร้างงานใหม่ | ✓ | | | |
| ผู้ใช้ | ✓ | | | |
| `auth/me`, `auth/change-password` | ✓ | ✓ | ✓ | ✓ |

- Organizer/Finance/Staff ใช้ได้เฉพาะงานที่อยู่ใน `eventIds` ของตัวเอง งานอื่น `403` · SuperAdmin ได้ทุกงาน (`eventIds` ว่างเสมอ)
- 1 คนมี 1 บทบาท

## 2. Auth

### `POST /admin/auth/login`
```json
// request
{ "email": "a@b.com", "password": "..." }
// 200
{ "token": "eyJ...", "expiresAt": "2026-10-12T16:00:00Z",
  "user": { "id": "guid", "name": "…", "email": "…", "role": "Finance", "isActive": true,
            "eventIds": [1], "createdAt": "…", "lastLoginAt": "…" } }
```
- ผิดทุกกรณี (ไม่มีอีเมล/รหัสผิด/บัญชีปิด/ถูกล็อก) → `401 INVALID_CREDENTIALS` ข้อความเดียวกัน
- ผิดครบ 5 ครั้งล็อก 15 นาที · token อายุ 8 ชม.
- token ถูกเพิกถอนทันทีเมื่อ: ปิดบัญชี, เปลี่ยนบทบาท, เปลี่ยน/รีเซ็ตรหัส → request ถัดไปได้ `401`
- บทบาทและ `eventIds` อ่านจาก DB ทุก request ไม่อยู่ใน token (อย่า decode token เพื่อดูสิทธิ์ ให้เก็บจาก `user` ตอน login และ refresh ด้วย `me`)

### `GET /admin/auth/me` → `user` รูปเดียวกับด้านบน
### `POST /admin/auth/change-password`
`{ "currentPassword", "newPassword" }` → `204` (token เดิมใช้ไม่ได้หลังนี้ ต้อง login ใหม่) · `400 WRONG_PASSWORD` · `400 WEAK_PASSWORD` (`details.reasons[]`) · รหัสผ่านใหม่ ≥ 10 ตัวอักษร ไม่บังคับรูปแบบอื่น

## 3. ผู้ใช้ (SuperAdmin)

- `GET /admin/users` → array ของ `user` (เรียงตามชื่อ)
- `POST /admin/users` `{ name, email, password, role, eventIds? }` → `201` + user
  - `role` ∈ `SuperAdmin | Organizer | Finance | Staff`
  - ข้อผิดพลาด: `409 EMAIL_TAKEN`, `400 WEAK_PASSWORD`, `400 VALIDATION_ERROR` (อีเมล/บทบาท/งานที่ไม่มีอยู่)
- `PATCH /admin/users/{id}` ส่งเฉพาะฟิลด์ที่แก้: `{ name?, role?, isActive?, eventIds?, newPassword? }` → user ใหม่
  - `newPassword` = แอดมินรีเซ็ตรหัสให้
  - `409 CANNOT_CHANGE_SELF` (ปิดบัญชี/เปลี่ยนบทบาทตัวเอง), `409 LAST_SUPER_ADMIN`, `404 USER_NOT_FOUND`
  - เปลี่ยนบทบาทเป็น SuperAdmin → `eventIds` ถูกล้าง

## 4. ตรวจสลิป (SuperAdmin, Finance)

### `GET /admin/events/{eventId}/payments?status=&page=&pageSize=`
`status` ∈ `Pending | Verifying | NeedsReview | Verified | Rejected` (ไม่ส่ง = ทั้งหมด) ใหม่สุดก่อน · งานประจำ: `status=NeedsReview`
```json
{ "paymentId": 12, "orderId": "guid", "orderNo": "RUN27-000123", "orderStatus": "Pending",
  "contactName": "…", "phone": "0812345678", "runnerCount": 2,
  "expectedAmount": 1000.13, "paidAmount": null, "status": "NeedsReview",
  "rejectReason": "VERIFY_TIMEOUT", "reviewNote": null,
  "createdAt": "…", "transferredAt": null, "reviewedBy": null }
```

### `GET /admin/payments/{id}/slip` → `{ "url": "https://…", "expiresAt": "…" }`
ลิงก์รูปอายุ **5 นาที** — ขอใหม่ทุกครั้งที่เปิดดู อย่าเก็บ/แคช (ใน dev โหมด Fake `url` เป็น `data:` URL) · `404 SLIP_NOT_FOUND`

### Action — ทั้งหมดใช้ได้เฉพาะ `status = NeedsReview` ไม่งั้น `409 PAYMENT_NOT_REVIEWABLE` (มีคนจัดการไปก่อน → รีเฟรชรายการ) และคืน payment item ใหม่
| endpoint | body | ผล |
|---|---|---|
| `POST /admin/payments/{id}/approve` | – | `Verified` + ออเดอร์ `Paid` + ออกเลขบิบ |
| `POST /admin/payments/{id}/reject` | `{ "note": "เหตุผล" }` บังคับ ≤ 500 ตัวอักษร | `Rejected` (`rejectReason = ADMIN_REJECTED`) นักวิ่งเห็น `note` และอัปสลิปใหม่ได้ถ้าออเดอร์ยัง Pending |
| `POST /admin/payments/{id}/recheck` | – | กลับเป็น `Verifying` ให้ระบบตรวจซ้ำ (ผลจะมาเองภายในไม่กี่นาที → รีเฟรช) |

**ปุ่มที่ควรแสดงตาม `rejectReason` ของรายการ `NeedsReview`**

| `rejectReason` | ความหมายที่แสดงให้แอดมิน | approve | reject | recheck |
|---|---|:-:|:-:|:-:|
| `DUPLICATE_UNKNOWN` | SlipOK บอกสลิปซ้ำ แต่ระบบเราไม่มีสลิปใบนั้น — ดูรูปเทียบยอดก่อนอนุมัติ | ✓ | ✓ | |
| `VERIFY_TIMEOUT` | ตรวจอัตโนมัติไม่สำเร็จ (SlipOK ล่ม/โควตาหมด) | ✓ (หลังเช็กบัญชีธนาคาร) | ✓ | ✓ |
| `ORDER_EXPIRED` | เงินเข้าแต่ออเดอร์หมดเวลาแล้ว | ✓ (ระบบจองโควตาใหม่ให้) | ✓ | |
| `ALREADY_PAID` | ออเดอร์จ่ายแล้วด้วยสลิปอื่น สลิปนี้ซ้ำซ้อน | ✗ | ✓ | |

ข้อผิดพลาดของ action:
- `409 CAPACITY_FULL` (approve ออเดอร์หมดเวลา แต่โควตา/เสื้อเต็มแล้ว) — แสดงข้อความว่าต้องคืนเงิน; รายการยังเป็น `NeedsReview` (ระบบคืนเงินยังไม่มี → ปล่อยไว้/reject พร้อมหมายเหตุ)
- `409 APPROVE_NOT_ALLOWED` (ออเดอร์จ่ายแล้ว หรือ recheck ผิดเหตุผล), `409 ORDER_NOT_PAYABLE` (ออเดอร์ถูกยกเลิก/คืนเงินแล้ว), `409 PAYMENT_IN_PROGRESS` (recheck ตอนนักวิ่งส่งสลิปใหม่เข้ามาแล้ว)
- `400 VALIDATION_ERROR` (`details.note`) reject โดยไม่ใส่เหตุผล · `404 PAYMENT_NOT_FOUND`

## 5. ออเดอร์ (SuperAdmin, Organizer, Finance)

### `GET /admin/events/{eventId}/orders?status=&q=&page=&pageSize=`
- `status` ∈ `Pending | Paid | Expired | Cancelled | Refunded` · Pending ที่เลยเวลาแต่ระบบยังไม่ปิด แสดงเป็น `Expired` ทั้งตัวกรองและค่าที่คืน
- `q` ค้นเลขออเดอร์ / ชื่อผู้ติดต่อ / เบอร์ (พิมพ์มีขีดได้) / ชื่อนักวิ่ง / เลขบิบ (ตรงตัว)
```json
{ "orderId": "guid", "orderNo": "…", "status": "Paid", "contactName": "…", "phone": "…", "runnerCount": 2,
  "total": 1000, "payableAmount": 1000.13, "latestPaymentStatus": "Verified", "latestPaymentReason": null,
  "createdAt": "…", "expiresAt": "…", "paidAt": "…" }
```

### `GET /admin/orders/{id}` (`404 ORDER_NOT_FOUND`)
```json
{ "orderId": "…", "orderNo": "…", "eventId": 1, "status": "Paid", "contactName": "…", "phone": "…", "email": "…|null",
  "subTotal": 0, "discountAmount": 0, "shippingFee": 0, "total": 0, "payableAmount": 0, "promoCode": "EARLY10|null",
  "createdAt": "…", "expiresAt": "…", "paidAt": "…|null",
  "shipping": { "name": "EMS", "type": "Pickup|Post", "fee": 60, "trackingNo": null, "shipName": "…|null", "shipPhone": "…|null", "address": "…|null" },
  "registrations": [ { "id": 1, "firstName": "…", "lastName": "…", "distance": "5K", "shirtSize": "M", "bibNumber": "B0001|null",
                        "pricePaid": 500, "teamName": null, "isCancelled": false, "checkedInAt": null,
                        "gender": "Male", "birthDate": "1990-01-01", "phone": "…", "email": "…", "idCardOrPassport": "…",
                        "emergencyContactName": "…", "emergencyContactPhone": "…", "medicalNote": "…" } ],
  "payments": [ { "paymentId": 12, "status": "…", "rejectReason": null, "reviewNote": null, "expectedAmount": 0, "paidAmount": null,
                  "createdAt": "…", "transferredAt": null, "verifiedAt": null, "reviewedBy": null } ] }
```
**ข้อมูลอ่อนไหว**: ฟิลด์ที่ระบุ `|null` ในกลุ่ม email / shipping (`shipName`, `shipPhone`, `address`) และกลุ่ม `gender … medicalNote` ของ registration จะเป็น `null` เสมอเมื่อผู้ดูเป็น **Finance** (Organizer/SuperAdmin เห็นครบ) — UI ต้องรับ `null` ได้และไม่ต้องแสดงช่องนั้น · `payments` เรียงใหม่สุดก่อน

## 6. ตั้งค่างาน (SuperAdmin, Organizer)

### งาน
- `POST /admin/events` (SuperAdmin) `{ name, slug, year, eventDate, registrationOpensAt, registrationClosesAt, promptPayId, promptPayAccountName, reservationMinutes? }` → `201` งาน **ยังไม่เผยแพร่** · `slug` ใช้ a-z 0-9 `-` แก้ภายหลังไม่ได้ · `409 SLUG_TAKEN`
- `GET /admin/events/{id}` → `{ id, name, slug, year, eventDate, registrationOpensAt, registrationClosesAt, promptPayId, promptPayAccountName, reservationMinutes, isPublished }`
- `PUT /admin/events/{id}` ส่งครบทุกฟิลด์ (ไม่มี slug/year): `{ name, eventDate, registrationOpensAt, registrationClosesAt, promptPayId, promptPayAccountName, reservationMinutes, isPublished }` → `{ event, warnings: string[] }`
  - **แสดง `warnings` ให้แอดมินเห็นชัด ๆ** — เปลี่ยน PromptPay แล้วต้องแก้บัญชีใน SlipOK LIFF ให้ตรงกัน ไม่งั้นทุกสลิปถูกปฏิเสธ `WRONG_RECEIVER`
  - PromptPay: เบอร์ 10 หลักขึ้นต้น 0 หรือเลข 13 หลัก (ใส่ขีดได้ ระบบเก็บเฉพาะตัวเลข) · `reservationMinutes` 5–240
  - เผยแพร่ (`isPublished: true`) ต้องมีระยะ/ไซซ์/วิธีรับของที่เปิดใช้อย่างละ ≥ 1 ไม่งั้น `409 NOT_READY_TO_PUBLISH` (`details.missing[]`)

### รายการย่อย — GET คืน array, PUT รับ array **ทั้งชุด** แล้วคืน array ใหม่
กติกา: มี `id` = แก้ · ไม่มี `id` = เพิ่ม · id ที่หายไปจากชุด = ลบ → ส่งชุดเต็มที่ได้จาก GET กลับมาเสมอ (แก้เฉพาะที่ต้องแก้)

| endpoint | รายการ (input) | เพิ่มเติมใน output |
|---|---|---|
| `…/distances` | `{ id?, name, distanceKm, bibPrefix, quota, price, earlyBirdPrice?, earlyBirdEndsAt? }` | `reservedCount`, `lastBibNumber` |
| `…/shirt-sizes` | `{ id?, size, detail?, stock }` | `reservedCount` |
| `…/shipping-options` | `{ id?, type: "Pickup"\|"Post", name, fee, isActive }` | – |

ข้อผิดพลาด:
- `409 QUOTA_BELOW_RESERVED` — ลด `quota`/`stock` ต่ำกว่า `reservedCount` (`details`: name, newLimit, reserved) → ในฟอร์มตั้ง min = `reservedCount`
- `409 BIB_PREFIX_LOCKED` — เปลี่ยน `bibPrefix` หลังออกบิบแล้ว (`lastBibNumber > 0`) → ล็อกช่องนี้เมื่อ `lastBibNumber > 0`
- `409 IN_USE` — ลบรายการที่มีผู้สมัคร/ออเดอร์อ้างถึง (ซ่อนปุ่มลบเมื่อ `reservedCount > 0`; วิธีรับของให้ใช้ `isActive=false` แทน)
- `409 DUPLICATE` — ชื่อ/ไซซ์/ตัวอักษรบิบซ้ำ · `400` — ค่าไม่ถูกต้อง · early bird ต้องใส่ราคาและวันตัดรอบพร้อมกัน และราคา ≤ ราคาปกติ · `bibPrefix` A–Z 0–9 ยาว 1–5 (ระบบแปลงเป็นตัวพิมพ์ใหญ่)
- ราคาใหม่มีผลกับออเดอร์ใหม่เท่านั้น (บอกแอดมินในฟอร์ม)
- งานที่เผยแพร่แล้วปิดวิธีรับของจนไม่เหลือที่เปิดอยู่ → `409 NOT_READY_TO_PUBLISH`

### โค้ดส่วนลด `…/events/{eventId}/promos` (SuperAdmin, Organizer)
- `GET` → array `{ id, code, type: "Percent"|"Fixed", value, minOrderAmount, startsAt, endsAt, maxUses, usedCount, isActive }`
- `POST` body `{ code, type, value, minOrderAmount?, startsAt?, endsAt?, maxUses?, isActive? }` → `201` · `code` A-Z 0-9 `_` `-` ยาว 3–30 (ระบบแปลงเป็นตัวพิมพ์ใหญ่) · `Percent` ค่า 0–100, `Fixed` > 0 · `maxUses` ว่าง = ไม่จำกัด · เวลาต้องมีเขตเวลา · `409 PROMO_CODE_TAKEN`
- `PUT /promos/{promoId}` ส่งครบทุกฟิลด์ (ค่า `code` ที่ส่งมาถูกเมินเพราะแก้ไม่ได้ → ล็อกช่องนี้) → โค้ดที่แก้แล้ว · ไม่มีลบ ให้ปิดด้วย `isActive: false`
- `409 MAX_USES_BELOW_USED` — ตั้ง `maxUses` ต่ำกว่า `usedCount` (ใช้ `usedCount` เป็นค่าต่ำสุดในฟอร์ม) · `404 PROMO_NOT_FOUND`
- แก้ไขมีผลกับออเดอร์ใหม่เท่านั้น

## 7. แดชบอร์ด `GET /admin/events/{eventId}/dashboard` (SuperAdmin, Organizer, Finance)
```json
{ "eventId": 1, "asOf": "…",
  "orders": { "total": 0, "paid": 0, "pending": 0, "expired": 0, "cancelled": 0, "refunded": 0 },
  "paidRunners": 0, "pendingRunners": 0,
  "payments": { "needsReview": 0, "verifying": 0 },
  "revenue": { "paid": 0, "pendingAmount": 0 },
  "distances": [ { "id": 1, "name": "5K", "quota": 100, "reserved": 0, "remaining": 100, "paidRunners": 0, "pendingRunners": 0 } ],
  "shirts": [ { "id": 1, "size": "M", "stock": 100, "reserved": 0, "remaining": 100, "paidRunners": 0 } ] }
```
- `payments.needsReview` ใช้ทำป้าย/ลิงก์ไปหน้าตรวจสลิป
- `revenue` นับจากยอดโอนจริง (`payableAmount` รวมเศษสตางค์) · Pending ที่เลยเวลานับเป็น expired
- `reserved` = จ่ายแล้ว + กำลังรอจ่าย (ที่ยังไม่หมดเวลา) ตามตัวนับโควตา

## 7.1 นักวิ่ง / export / เลขพัสดุ

- `GET /admin/events/{eventId}/registrations?distance=<distanceId>&q=&page=&pageSize=` (SuperAdmin, Organizer, Finance) — เฉพาะนักวิ่งที่**จ่ายแล้ว** เรียงตามนามสกุล · `q` ค้นชื่อ/เลขบิบ(ตรงตัว)/เลขออเดอร์/ทีม/เบอร์
  - item: `registrationId, orderId, orderNo, firstName, lastName, distance, shirtSize, bibNumber, teamName, checkedInAt` + กลุ่มอ่อนไหว `gender, birthDate, phone, email, emergencyContactName, emergencyContactPhone, medicalNote` (**Finance ได้ `null`**)
- `GET /admin/events/{eventId}/orders/export` (SuperAdmin, Organizer) — ดาวน์โหลด `.xlsx` (เรียก server-to-server แล้ว stream ต่อให้เบราว์เซอร์; อย่าเก็บไฟล์) ทุกครั้งถูกบันทึก audit
- `PATCH /admin/orders/{id}/tracking` `{ trackingNo }` (SuperAdmin, Organizer) → `{ trackingNo }` · ส่งค่าว่างเพื่อล้าง · เฉพาะออเดอร์ที่จ่ายแล้วและเลือก `Post` ไม่งั้น `409 TRACKING_NOT_ALLOWED` (ซ่อนช่องนี้ถ้าไม่ใช่) · ยังไม่ส่ง LINE (รอระบบแจ้งเตือน)

## 7.2 เช็กอิน (SuperAdmin, Organizer, Staff)

หน้างานเห็นแค่ `CheckInRunner` = `{ registrationId, orderNo, firstName, lastName, distance, shirtSize, bibNumber, teamName, eligible, checkedInAt, checkedInBy }` (ไม่มีข้อมูลสุขภาพ/ติดต่อ แม้เป็น Organizer) — `checkedInBy` = ชื่อผู้เช็กอิน
- `POST /admin/checkin/scan` `{ token }` → `CheckInRunner` (ไม่เปลี่ยนอะไร) · `token` คือค่าใน QR บนตั๋ว (`checkInToken`) · `404 CHECKIN_TOKEN_NOT_FOUND`
  - `eligible=false` = ยังไม่จ่าย/ถูกยกเลิก/ยังไม่มีบิบ → ปิดปุ่มยืนยัน · `checkedInAt != null` = เช็กอินไปแล้ว → แสดงเวลา/คนที่เช็กอิน
- `POST /admin/checkin/{registrationId}/confirm` `{ station?, note? }` → `{ runner, alreadyCheckedIn }` · **เช็กอินซ้ำไม่ error** ได้ `alreadyCheckedIn: true` พร้อมเวลาเดิม (สองโต๊ะกดพร้อมกันได้ผลเดียว) · `409 NOT_ELIGIBLE`
- `POST /admin/checkin/{registrationId}/undo` `{ note }` (**SuperAdmin, Organizer เท่านั้น** Staff ได้ 403) → `CheckInRunner` · `note` บังคับ ≤ 200 · `409 NOT_CHECKED_IN`
- `GET /admin/events/{eventId}/checkins?checkedIn=true|false&distance=&q=&page=&pageSize=` → `{ runners: { items: CheckInRunner[], page, pageSize, total }, summary: { paid, checkedIn } }` · `q` ค้นชื่อ/เลขบิบ/เลขออเดอร์
- ทุกการเช็กอิน/ยกเลิกถูกบันทึกใน CheckInLog และ AuditLog

## 7.3 คืนเงิน (SuperAdmin, Finance)

คืน**ทั้งใบ**เท่านั้น (ไม่มีคืนบางคน) · flow: `Requested` → `Approved` → `Completed` หรือ `Requested` → `Rejected` · ทุก action คืน refund item ใหม่ และ `409 REFUND_WRONG_STATE` (`details.status`) ถ้ามีคนทำไปก่อน → รีเฟรช
```json
{ "refundId": 1, "orderId": "guid", "orderNo": "…", "contactName": "…", "phone": "…", "paymentId": 12,
  "kind": "Order|Payment", "amount": 1000.13, "reason": "…", "note": null, "status": "Requested",
  "externalRef": null, "requestedBy": "ชื่อ", "approvedBy": null, "createdAt": "…", "processedAt": null }
```
- `GET /admin/events/{eventId}/refunds?status=&page=&pageSize=` (`status` ∈ Requested | Approved | Completed | Rejected)
- `POST /admin/orders/{orderId}/refunds` `{ reason, paymentId?, amount? }` → `201`
  - `reason` บังคับ ≤ 500 · `amount` ไม่ส่ง = คืนเต็มยอดที่จ่าย (ส่งน้อยกว่าได้ เช่น หักค่าธรรมเนียม; มากกว่า → `400`)
  - ไม่ส่ง `paymentId` = ใช้ Payment ที่ผ่านแล้วของออเดอร์ที่จ่ายแล้ว (`kind: "Order"`) · ส่ง `paymentId` ของสลิป**รอตรวจ** (ซ้ำซ้อน `ALREADY_PAID` หรือโควตาเต็ม `CAPACITY_FULL`) = `kind: "Payment"`
  - `409 REFUND_NOT_ALLOWED` (ออเดอร์ยังไม่จ่าย/ถูกยกเลิก/สลิปไม่อยู่ในสถานะที่คืนได้) · `409 REFUND_EXISTS` (Payment นี้มีคำขอที่ยังไม่ถูกปฏิเสธอยู่แล้ว)
- `POST /admin/refunds/{id}/approve` — `kind: "Order"`: ยกเลิกนักวิ่งทุกคน (เช็กอินไม่ได้) คืนโควตา/ไซซ์เสื้อ/การใช้โค้ด; ออเดอร์ยังเป็น `Paid` จนกว่าจะ complete · `kind: "Payment"`: สลิปนั้นออกจากคิวตรวจสลิป (เป็น `Rejected`) ออเดอร์ไม่เปลี่ยน
- `POST /admin/refunds/{id}/reject` `{ note }` (บังคับ ≤ 500) — ไม่แตะออเดอร์; ขอใหม่ได้
- `POST /admin/refunds/{id}/complete` `{ externalRef, note? }` — `externalRef` บังคับ (เลขอ้างอิงการโอนคืน ≤ 100) → ออเดอร์ `kind: "Order"` เป็น `Refunded`
- ระบบ**ไม่ได้โอนเงินให้** — แอดมินโอนคืนเองนอกระบบ แล้วค่อยกด complete พร้อมเลขอ้างอิง
- ปุ่มที่ควรแสดง: Requested → อนุมัติ/ปฏิเสธ · Approved → "โอนคืนแล้ว" (complete) · ปุ่ม "ขอคืนเงิน" ที่หน้ารายละเอียดออเดอร์ (สถานะ Paid) และที่รายการสลิปรอตรวจ `ALREADY_PAID`/`CAPACITY_FULL`

## 7.4 Audit (SuperAdmin)

`GET /admin/audit?userId=&action=&entityType=&entityId=&from=&to=&page=&pageSize=` → `{ items, page, pageSize, total }` ใหม่สุดก่อน (อ่านอย่างเดียว)
```json
{ "id": 1, "createdAt": "…", "userId": "guid|null", "userName": "ชื่อ|null", "action": "payment.approve",
  "entityType": "Payment", "entityId": "10", "oldValues": { … }|null, "newValues": { … }|null, "ipAddress": "1.2.3.4" }
```
- `oldValues`/`newValues` เป็น JSON object จริง (ไม่ใช่สตริง) รูปร่างต่างกันตาม `action` → แสดงแบบ key/value ทั่วไป หรือ diff
- `action` ตรงตัว หรือลงท้าย `.` เพื่อกรองทั้งกลุ่ม · กลุ่มที่มี: `auth.` `user.` `payment.` `refund.` `event.` `promo.` `order.` `checkin.` `export.`
- `userId` เป็น `null` ได้ (เช่น `auth.login_failed` ที่อีเมลไม่มีในระบบ — `entityId` คืออีเมลที่ลอง)
- `from` (รวม) / `to` (ไม่รวม) ต้องมีเขตเวลา มิฉะนั้น `400`

## 7.5 ประกาศ LINE (SuperAdmin, Organizer)

ผู้รับ = คนที่**ผูก LINE กับออเดอร์แล้วและยังเป็นเพื่อนกับ OA** เท่านั้น (ดู `docs/line-setup.md`) ออเดอร์ที่ไม่ผูก LINE ส่งไม่ถึง → UI ต้องแสดงตัวเลขให้เห็นก่อนส่ง
```json
{ "id": 1, "eventId": 1, "title": "…", "body": "…", "channel": "Line", "audienceDistanceId": null, "onlyPaid": true,
  "status": "Draft|Scheduled|Sending|Sent|Cancelled", "scheduledAt": null, "sentAt": null,
  "recipientCount": 0, "successCount": 0, "failCount": 0, "createdAt": "…", "createdBy": "ชื่อ",
  "audience": { "targetOrders": 120, "recipients": 45 } }
```
- `GET /admin/events/{eventId}/broadcasts?status=&page=&pageSize=` (ไม่มี `audience` ในรายการ)
- `POST /admin/events/{eventId}/broadcasts` `{ title ≤100, body ≤2000, audienceDistanceId?, onlyPaid? }` → `201` ร่าง + `audience` · `onlyPaid` ค่าเริ่ม `true`; `false` = รวมออเดอร์ที่ยังรอจ่าย (ไม่หมดเวลา)
- `GET /admin/broadcasts/{id}` — ร่างมี `audience` (คำนวณสดทุกครั้ง); `PUT /admin/broadcasts/{id}` แก้ได้เฉพาะ `Draft` (`409 BROADCAST_WRONG_STATE`)
- **flow ส่ง**: แสดง `audience.recipients` ให้แอดมินยืนยัน (และ `targetOrders − recipients` = ที่ส่งไม่ถึง) → `POST /admin/broadcasts/{id}/send` `{ expectedRecipients: <ตัวเลขที่ยืนยัน>, scheduledAt? }`
  - `409 AUDIENCE_CHANGED` (`details.current`) ถ้าจำนวนเปลี่ยนระหว่างรอ → แสดงตัวเลขใหม่ให้ยืนยันอีกรอบ · `409 NO_RECIPIENTS` · `409 BROADCAST_WRONG_STATE`
  - `scheduledAt` ต้องอนาคตและมีเขตเวลา → สถานะ `Scheduled`; ไม่ส่ง = `Sending` ทันที
  - ส่งจริงทีละข้อความโดย worker (ไม่กี่วินาทีถึงหลายนาที) → poll `GET /admin/broadcasts/{id}` ดู `successCount`/`failCount` จนสถานะเป็น `Sent`
- `POST /admin/broadcasts/{id}/cancel` — ใช้ได้กับ Draft/Scheduled/Sending; ข้อความที่ส่งไปแล้วเรียกคืนไม่ได้

## 8. การเปลี่ยนแปลงฝั่งนักวิ่ง (ผลข้างเคียง)
`GET /orders/{id}` มี `line: { linked, following }` — ได้รับแจ้งเตือนเมื่อทั้งคู่เป็น `true` (ไม่ส่ง LINE userId ออกมา) ใช้แสดงสถานะการ์ดผูก LINE

`POST /orders/{orderId}/line` `{ idToken }` → `204` ผูก LINE (LIFF `liff.getIDToken()`) กับออเดอร์เพื่อรับแจ้งเตือน: `400 INVALID_LINE_TOKEN`, `409 LINE_ALREADY_LINKED`, `409 ORDER_CLOSED`

`GET /orders/{id}/payment` → `payment` มี `note` เพิ่ม (เหตุผลที่เจ้าหน้าที่ปฏิเสธ แสดงให้นักวิ่งเห็นถ้าไม่ null)

## 9. ลองใช้บนเครื่อง dev
SuperAdmin คนแรกสร้างตอนแอปเริ่มจากค่าที่ตั้งไว้ (ทำครั้งเดียว):
```bash
dotnet user-secrets set "Admin:BootstrapEmail" "admin@example.com" --project src/HuebRun.Api
dotnet user-secrets set "Admin:BootstrapPassword" "ตั้งรหัสยาวอย่างน้อย10ตัว" --project src/HuebRun.Api
```
แล้ว login ที่ `POST /api/v1/admin/auth/login` · สร้างผู้ใช้บทบาทอื่นผ่าน `POST /admin/users` · `Admin:JwtSecret` ของ dev อยู่ใน `appsettings.Development.json` แล้ว · production ต้องตั้ง `Admin__JwtSecret` (≥ 32 ตัวอักษร) ไม่งั้นแอปไม่ขึ้น
