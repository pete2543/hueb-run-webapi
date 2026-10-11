# API contract (ร่าง)

ร่างนี้สร้างจาก flow ของหน้าจอและ schema ใน `docs/db-schema/` ยังไม่ผ่านการ implement เปลี่ยนได้
ใช้เป็นฐานทำ mock (เช่น MSW) ระหว่างทำหน้าบ้านก่อน และเป็นข้อตกลงกับหลังบ้านตอนเขียนจริง
ควรแปลงเป็น OpenAPI (`openapi.yaml`) ภายหลังเพื่อ generate type ฝั่ง TypeScript

## หลักการ

- Base path: `/api/v1`, JSON, UTF-8, เวลาเป็น ISO 8601 UTC (แสดงเป็นเวลาไทยที่หน้าเว็บ)
- เงินเป็นตัวเลขทศนิยม 2 ตำแหน่ง หน่วยบาท
- Error รูปแบบเดียวกัน: `{ "code": "QUOTA_FULL", "message": "...", "details": {...} }`
- Public endpoint ไม่ต้อง login; ของนักวิ่งยืนยันผ่าน `orderId` (GUID) + token หรือ LINE Login
- Admin endpoint ต้อง login และตรวจบทบาท + สิทธิ์ต่อ Event

| HTTP | ความหมายในโปรเจกต์นี้ |
|---|---|
| 400 | ข้อมูลไม่ถูกต้อง (validation) |
| 401/403 | ยังไม่ login / ไม่มีสิทธิ์ |
| 404 | ไม่พบข้อมูล |
| 409 | ชน เช่น โควตาเต็ม, ไซซ์หมด, โค้ดครบจำนวน, สลิปซ้ำ, สถานะไม่ถูกต้อง |
| 410 | ออเดอร์หมดเวลาแล้ว |
| 422 | โค้ดส่วนลดใช้ไม่ได้ (หมดอายุ/ไม่ถึงขั้นต่ำ) |

## Public (ฝั่งนักวิ่ง)

| Method | Path | ใช้ในหน้า | หมายเหตุ |
|---|---|---|---|
| GET | `/events/{slug}` | 1, 2 | ข้อมูลงาน วันเปิด/ปิดรับ วันที่จัดงาน (ไม่รวมเลขบัญชี) |
| GET | `/events/{slug}/distances` | 1, 2 | ระยะ ราคา (รวม early bird ที่คิดแล้ว) โควตาคงเหลือ |
| GET | `/events/{slug}/shirt-sizes` | 3 | ไซซ์และจำนวนคงเหลือ |
| GET | `/events/{slug}/shipping-options` | 4 | รับเอง / ไปรษณีย์ พร้อมค่าส่ง |
| GET | `/lookups` | 3 | คำนำหน้า สัญชาติ กรุ๊ปเลือด ความสัมพันธ์ฉุกเฉิน (เรียงตาม `SortOrder`) |
| GET | `/geo/provinces` | 4 | |
| GET | `/geo/provinces/{id}/districts` | 4 | โหลดตามระดับ ไม่โหลดทั้งหมดทีเดียว |
| GET | `/geo/districts/{id}/subdistricts` | 4 | |
| GET | `/geo/zip/{zipCode}` | 4 | คืนจังหวัด/อำเภอ/ตำบลที่ตรงรหัสไปรษณีย์ |
| GET | `/geo/search?q=&limit=20` | 4 | ช่องค้นหาเดียว (autocomplete): ชื่อตำบล/อำเภอ/จังหวัด ไทยหรืออังกฤษ หรือต้นรหัสไปรษณีย์ หลายคำคั่นช่องว่าง (`ต.` `อ.` `จ.` `แขวง` `เขต` ระบุระดับได้) คืนที่อยู่ครบชุด + `label` · q < 2 ตัวอักษร → `[]` · limit 1–50 |
| POST | `/events/{slug}/promo/validate` | 4 | ตรวจโค้ด + คืนส่วนลดที่จะได้ (ยังไม่ใช้โค้ดจริง) |
| POST | `/events/{slug}/orders` | 4→5 | สร้าง Order + จองโควตา/เสื้อ/โค้ดใน transaction เดียว (ดูด้านล่าง) |
| GET | `/orders/{orderId}` | 5, 6 | สถานะ ยอด เวลาหมดอายุ รายชื่อนักวิ่ง |
| GET | `/orders/{orderId}/payment` | 5 | ข้อมูลสำหรับสร้าง QR (payload PromptPay, ยอดที่ต้องโอน, ชื่อบัญชี) |
| POST | `/orders/{orderId}/payment/slip` | 5 | อัปโหลดสลิป (multipart) |
| GET | `/orders/{orderId}/tickets` | 6 | เลขบิบ + `checkInToken` (หน้าเว็บสร้าง QR เอง) เฉพาะ `Paid` ไม่งั้น `409 ORDER_NOT_PAID` |
| POST | `/line/webhook` | – | follow/unfollow จาก LINE (ตรวจ signature) |
| POST | `/auth/line/callback` | – | LINE Login callback เชื่อม `LineAccount` กับ Order |

### POST `/events/{slug}/orders`

คำขอ (ย่อ):

```json
{
  "contact": { "name": "", "phone": "", "email": "" },
  "runners": [
    {
      "distanceId": 0, "shirtSizeId": 0,
      "titleId": 0, "firstName": "", "lastName": "", "gender": "Male",
      "birthDate": "2000-01-31", "nationalityId": 0, "bloodTypeId": 0,
      "phone": "", "email": "", "teamName": "",
      "emergencyContact": { "relationId": 0, "name": "", "phone": "" },
      "medicalNote": ""
    }
  ],
  "shippingOptionId": 0,
  "shippingAddress": { "name": "", "phone": "", "line1": "", "subdistrictId": 0, "postalCode": "" },
  "promoCode": "",
  "acceptedPdpa": true
}
```

ผลลัพธ์: `201` พร้อม `orderId`, `orderNo`, `total`, `payableAmount`, `expiresAt`
ข้อผิดพลาดที่ต้องรองรับในหน้าเว็บ: `409 QUOTA_FULL` (ระบุระยะ), `409 SHIRT_OUT_OF_STOCK`, `409 PROMO_EXHAUSTED`, `422 PROMO_INVALID`, `400 REGISTRATION_CLOSED`

ฝั่งเซิร์ฟเวอร์ต้อง: คำนวณราคาเอง (ไม่เชื่อราคาจากหน้าเว็บ), ตรวจเวลาเปิด/ปิดรับ, จองโควตา/ไซซ์/โค้ดแบบ atomic, สร้าง `PayableAmount` ที่ไม่ซ้ำ, ตั้ง `ExpiresAt`

สถานะที่ implement แล้ว (2026-10-11): validate/promo/create/GET order/tickets + job หมดเวลา ทุก 1 นาที
error เพิ่มเติม: `409 PAYMENT_SLOT_BUSY` (เศษสตางค์ของยอดนี้เต็ม 99 ช่อง), `404 ORDER_NOT_FOUND`
`GET /orders/{id}` ไม่ส่ง `medicalNote`, ผู้ติดต่อฉุกเฉิน, `checkInToken`; `status` แสดง `Expired` ทันทีเมื่อเลย `expiresAt`

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

## Admin

เส้นทางรวมอยู่ใต้ `/admin/...` ทุกตัวกรองตาม Event ที่ผู้ใช้มีสิทธิ์

| Method | Path | หน้า | บทบาทที่ทำได้ |
|---|---|---|---|
| POST | `/admin/auth/login` | – | ทุกบทบาท |
| GET | `/admin/events/{id}/dashboard` | 8 | Organizer, Finance, SuperAdmin |
| GET | `/admin/events/{id}/orders?status=&q=&page=` | 9 | Organizer, Finance, SuperAdmin |
| GET | `/admin/events/{id}/orders/export` | 9 | Organizer, SuperAdmin (Excel) |
| GET | `/admin/orders/{id}` | 9 | |
| PATCH | `/admin/orders/{id}/tracking` | 9 | Organizer (เลขพัสดุ → ส่ง LINE) |
| GET | `/admin/events/{id}/payments?status=NeedsReview` | 10 | Finance, SuperAdmin |
| POST | `/admin/payments/{id}/approve` | 10 | Finance (ออกเลขบิบ + ส่ง LINE + AuditLog) |
| POST | `/admin/payments/{id}/reject` | 10 | Finance (ต้องมีเหตุผล) |
| GET | `/admin/events/{id}/registrations?distance=&q=` | 11 | |
| GET/PUT | `/admin/events/{id}/distances` | 12 | Organizer (ลดโควตาต่ำกว่าที่จองไม่ได้) |
| GET/PUT | `/admin/events/{id}/shirt-sizes` | 12 | Organizer |
| PUT | `/admin/events/{id}` | 12 | Organizer (เวลาเปิด/ปิด, PromptPay ต่อ Event) |
| GET/POST/PUT | `/admin/events/{id}/promos` | 13 | Organizer |
| POST | `/admin/checkin/scan` | 14 | Staff, Organizer (`{ "token": "..." }`) |
| POST | `/admin/checkin/{registrationId}/confirm` | 14 | Staff, Organizer |
| POST | `/admin/checkin/{registrationId}/undo` | 14 | Organizer |
| GET | `/admin/events/{id}/checkins` | 14 | |
| GET/POST | `/admin/events/{id}/broadcasts` | 15 | Organizer |
| POST | `/admin/broadcasts/{id}/send` | 15 | Organizer (ตรวจจำนวนผู้รับก่อน) |
| GET | `/admin/events/{id}/refunds` | 16 | Finance |
| POST | `/admin/refunds/{id}/approve` · `/reject` · `/complete` | 16 | Finance |
| GET/POST/PATCH | `/admin/users` | 17 | SuperAdmin |
| GET | `/admin/audit?userId=&action=&from=&to=` | 18 | SuperAdmin |

เมื่ออนุมัติคืนเงิน: คืนโควตา/ไซซ์เสื้อ ยกเลิกเลขบิบ และส่งแจ้งเตือน
การเช็กอินซ้ำต้องไม่ผิดพลาด ตอบว่าเช็กอินไปแล้วพร้อมเวลา และบันทึกใน `CheckInLog`

## Mock ระหว่างทำหน้าบ้านก่อน

- ใช้ MSW (หรือ route handler ใน Next.js) ตอบตามรูปแบบข้างบน
- ใส่เคสผิดพลาดไว้ใน mock ด้วย (โควตาเต็ม, โค้ดไม่ถูกต้อง, ออเดอร์หมดเวลา) เพื่อให้ทำหน้ารับมือได้ครบ
- เก็บ type ที่ generate จาก OpenAPI ไว้ที่เดียว แล้วใช้ทั้ง mock และ client จริง
