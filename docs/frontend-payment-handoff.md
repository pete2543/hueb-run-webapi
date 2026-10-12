# Handoff หน้าบ้าน: หน้าชำระเงิน (QR PromptPay + อัปโหลดสลิป)

เอกสารนี้สำหรับคน/AI ที่ทำหน้าบ้านต่อ ครอบคลุมหน้า 5 `/register/payment/[orderId]` และการพาไปหน้า 6 `/register/success/[orderId]`
หลังบ้าน implement และมีเทสต์แล้วบน branch `feat/payment` (ข้อมูลอ้างอิงละเอียด: `docs/api-contract.md`, spec `docs/superpowers/specs/2026-10-11-payment-slip-design.md`)

## หลักทั่วไป

- Base path `/api/v1`, JSON, เวลาเป็น ISO 8601 UTC → แสดงเป็นเวลาไทย (`Asia/Bangkok`)
- enum ส่งเป็น string (`"Pending"`, `"Verified"` …), ชื่อฟิลด์ camelCase, ฟิลด์ที่ไม่มีค่าส่งเป็น `null` (ไม่ถูกตัดทิ้ง)
- เงินเป็นตัวเลขทศนิยม 2 ตำแหน่ง หน่วยบาท — **ยอดที่ต้องโอนมีเศษสตางค์** (เช่น `400.01`) ใช้แยกออเดอร์ ห้ามปัดเศษตอนแสดง
- Error ทุกตัวรูปแบบเดียวกัน: `{ "code": "SLIP_DUPLICATE", "message": "ข้อความภาษาไทย", "details": {...} | null }`
  ตัดสินใจจาก `code` เสมอ; `message` แสดงให้ผู้ใช้ได้ (ปลอดภัย) แต่ควรมีข้อความของหน้าบ้านเองตามตารางด้านล่าง
- ไม่ต้อง login — ใช้ `orderId` (GUID) ใน URL

## TypeScript types

```ts
type OrderStatus = "Pending" | "Paid" | "Expired" | "Cancelled" | "Refunded";
type PaymentStatus = "Pending" | "Verifying" | "NeedsReview" | "Verified" | "Rejected";
type RejectReason =
  | "INVALID_SLIP" | "AMOUNT_MISMATCH" | "WRONG_RECEIVER" | "DUPLICATE_SLIP"   // ถูกปฏิเสธ → อัปโหลดใหม่ได้
  | "ORDER_EXPIRED" | "DUPLICATE_UNKNOWN" | "VERIFY_TIMEOUT";                  // NeedsReview → รอเจ้าหน้าที่

interface ApiError { code: string; message: string; details: Record<string, unknown> | null }

// GET /orders/{orderId}/payment
interface PaymentInfo {
  orderId: string;
  orderNo: string;
  amount: number;            // ยอดที่ต้องโอน (มีเศษสตางค์)
  accountName: string;       // ชื่อบัญชีผู้รับ แสดงให้ผู้ใช้ตรวจก่อนโอน
  qrPayload: string;         // string EMVCo — นำไปวาด QR เอง
  expiresAt: string;         // ISO UTC — ใช้นับถอยหลัง
  payment: { paymentId: number; status: PaymentStatus; rejectReason: RejectReason | null; createdAt: string } | null;
}

// POST /orders/{orderId}/payment/slip (200/202)
interface SlipSubmission {
  paymentId: number;
  status: PaymentStatus;     // "Verified" | "Verifying" | "NeedsReview"
  orderStatus: OrderStatus;  // "Paid" เมื่อ Verified
  nextCheckAt: string | null;// มีค่าเมื่อ Verifying
  reason: RejectReason | null;// มีค่าเมื่อ NeedsReview
}

// GET /orders/{orderId} — มีฟิลด์ใหม่ (ฟิลด์อื่นเหมือนเดิม)
interface OrderResponse {
  orderId: string; orderNo: string; status: OrderStatus;
  payableAmount: number; expiresAt: string; paidAt: string | null;
  // ...contact, shipping, runners ฯลฯ ตามเดิม
  payment: { paymentId: number; status: PaymentStatus; rejectReason: RejectReason | null } | null;  // ใหม่: Payment ล่าสุด
}
```

## Endpoint

### 1. `GET /api/v1/orders/{orderId}/payment` — ข้อมูลหน้าชำระเงิน

`200` → `PaymentInfo`

ตัวอย่าง:

```json
{
  "orderId": "3f6c…", "orderNo": "HR-000123", "amount": 400.01,
  "accountName": "นายสมชาย ใจดี",
  "qrPayload": "00020101021229370016A000000677010111011300668123456785802TH53037645406400.016304D3AB",
  "expiresAt": "2026-11-15T00:30:00Z",
  "payment": null
}
```

| error | ความหมาย | หน้าบ้านทำอะไร |
|---|---|---|
| `404 ORDER_NOT_FOUND` | ไม่มีออเดอร์นี้ | หน้า "ไม่พบใบสมัคร" + ปุ่มกลับไปสมัคร |
| `409 ORDER_ALREADY_PAID` | จ่ายแล้ว (รวมออเดอร์ฟรี) | redirect ไปหน้า 6 `/register/success/[orderId]` |
| `409 ORDER_NOT_PAYABLE` `details.status` | `Expired`/`Cancelled`/`Refunded` | แสดง "ใบสมัครหมดเวลา" — **ก่อนแสดง ให้เรียก `GET /orders/{id}` ดู `payment`** ถ้า `payment.status` เป็น `NeedsReview`/`Verifying` แปลว่าส่งสลิปแล้ว ให้แสดงสถานะ "รอเจ้าหน้าที่ตรวจสอบ" แทน |

### 2. `POST /api/v1/orders/{orderId}/payment/slip` — อัปโหลดสลิป

- `multipart/form-data` ฟิลด์ชื่อ **`file`** รูปเดียว
- รับ `.jpg .jpeg .png .webp` ไม่เกิน **5 MB** (เช็กชนิดจริงจากเนื้อไฟล์ เปลี่ยนนามสกุลไม่ช่วย) — **ไม่รับ HEIC/GIF/PDF**
- เรียกครั้งเดียวอาจใช้เวลาหลายวินาที (ตรวจกับ SlipOK ทันที) แสดง loading และกันกดซ้ำ

```ts
const form = new FormData();
form.append("file", file);
const res = await fetch(`/api/v1/orders/${orderId}/payment/slip`, { method: "POST", body: form }); // ห้ามตั้ง Content-Type เอง
```

ผลลัพธ์:

| HTTP | body | หน้าบ้านทำอะไร |
|---|---|---|
| `200` | `SlipSubmission` `status: "Verified"`, `orderStatus: "Paid"` | แสดง "ชำระเงินสำเร็จ" แล้ว redirect หน้า 6 |
| `202` | `status: "Verifying"`, `nextCheckAt` | ธนาคารยังไม่ยืนยัน (ปกติหายภายในไม่กี่นาที) → แสดง "กำลังตรวจสอบสลิป" และ **poll** (ดูหัวข้อ polling) ไม่ต้องให้ผู้ใช้อัปโหลดซ้ำ |
| `202` | `status: "NeedsReview"`, `reason` | เงินน่าจะเข้าแล้วแต่ต้องให้เจ้าหน้าที่ตรวจ → แสดง "ได้รับสลิปแล้ว เจ้าหน้าที่จะตรวจสอบและแจ้งผล" ไม่ต้องอัปโหลดซ้ำ |
| `422 SLIP_REJECTED` | `details: { paymentId, reason }` | สลิปไม่ผ่าน → แสดงข้อความตาม `reason` (ตารางด้านล่าง) และให้อัปโหลดใหม่ได้ทันที **แล้ว refetch `GET /payment`** เพราะ `expiresAt` เปลี่ยน |
| `400 VALIDATION_ERROR` | `details.file: string[]` | แสดงข้อความใต้ช่องเลือกไฟล์ |
| `409 PAYMENT_IN_PROGRESS` | – | มีสลิปกำลังตรวจอยู่ → เปลี่ยนไปโหมด "กำลังตรวจสอบ" + poll |
| `409 SLIP_DUPLICATE` | – | "สลิปนี้ถูกส่งเข้ามาแล้ว" (รูปเดียวกันเป๊ะ) |
| `409 ORDER_ALREADY_PAID` | – | redirect หน้า 6 |
| `409 ORDER_NOT_PAYABLE` | `details.status` | ออเดอร์ถูกยกเลิก/คืนเงิน → แสดงข้อความ ไม่ให้อัปโหลด |
| `404 ORDER_NOT_FOUND` | – | หน้า "ไม่พบใบสมัคร" |
| `5xx` / network | – | "ระบบขัดข้อง ลองใหม่อีกครั้ง" ให้กดส่งใหม่ได้ (ถ้าได้ `SLIP_DUPLICATE` ตอนส่งซ้ำ แปลว่าครั้งก่อนเข้าระบบแล้ว → เรียก `GET /orders/{id}` ดูสถานะ) |

**ข้อความสำหรับ `reason`**

| reason | สถานะ | ข้อความแนะนำ |
|---|---|---|
| `INVALID_SLIP` | Rejected | อ่านสลิปไม่ได้หรือไม่ใช่สลิปโอนเงิน กรุณาอัปโหลดรูปสลิปที่ชัดเจน |
| `AMOUNT_MISMATCH` | Rejected | ยอดโอนไม่ตรง ต้องโอน **{amount} บาท** (รวมเศษสตางค์) |
| `WRONG_RECEIVER` | Rejected | บัญชีผู้รับไม่ถูกต้อง กรุณาโอนผ่าน QR ในหน้านี้ |
| `DUPLICATE_SLIP` | Rejected | สลิปนี้ถูกใช้ชำระไปแล้ว |
| `ORDER_EXPIRED` | NeedsReview | ได้รับสลิปหลังหมดเวลา เจ้าหน้าที่จะตรวจสอบและติดต่อกลับ |
| `DUPLICATE_UNKNOWN` | NeedsReview | ได้รับสลิปแล้ว รอเจ้าหน้าที่ตรวจสอบ |
| `VERIFY_TIMEOUT` | NeedsReview | ระบบตรวจสลิปอัตโนมัติไม่สำเร็จ เจ้าหน้าที่จะตรวจสอบให้ |

### 3. `GET /api/v1/orders/{orderId}` — เพิ่มฟิลด์ `payment`

Payment ล่าสุดของออเดอร์ (`null` ถ้ายังไม่เคยส่งสลิป) — ใช้ตอน poll และตอนกลับมาเปิดหน้าใหม่
`status` ของออเดอร์เป็น `Expired` ทันทีเมื่อเลย `expiresAt` (ไม่ต้องรอ job)

## Flow หน้า 5

```
เปิดหน้า → GET /orders/{id}/payment
  ├─ 409 ORDER_ALREADY_PAID → redirect หน้า 6
  ├─ 409 ORDER_NOT_PAYABLE  → GET /orders/{id} → payment NeedsReview/Verifying ? "รอตรวจสอบ" : "หมดเวลา"
  └─ 200
       ├─ payment?.status === "Verifying"   → โหมดกำลังตรวจสอบ + poll
       ├─ payment?.status === "NeedsReview" → โหมดรอเจ้าหน้าที่
       ├─ payment?.status === "Rejected"    → แสดงเหตุผลครั้งล่าสุด + ให้อัปโหลดใหม่
       └─ null                              → โหมดปกติ: QR + ยอด + นับถอยหลัง + ช่องอัปโหลด
อัปโหลด → POST /payment/slip → ตามตารางผลลัพธ์
```

### Polling (ตอน `Verifying`)

- poll **`GET /orders/{orderId}`** (ไม่ใช่ `/payment` เพราะตัวนั้นตอบ `409` ทันทีที่จ่ายแล้ว)
- ทุก 10–15 วินาที; หยุดเมื่อแท็บไม่ active แล้ว poll ใหม่ทันทีเมื่อกลับมา
- `status === "Paid"` → redirect หน้า 6
- `payment.status === "NeedsReview"` → โหมดรอเจ้าหน้าที่ (หยุด poll)
- `payment.status === "Rejected"` → แสดงเหตุผล + ให้อัปโหลดใหม่ (refetch `/payment` เพื่อเอา `expiresAt` ใหม่)
- ระบบตรวจซ้ำนานสุดราว 60 นาทีแล้วจะเปลี่ยนเป็น `NeedsReview` เอง — หน้าบ้านไม่ต้องจับเวลาเอง

### นับถอยหลัง / `expiresAt`

- นับจาก `expiresAt` ของ server เสมอ ไม่คำนวณเอง
- `expiresAt` **เปลี่ยนได้**: พอส่งสลิป ระบบยืดเวลาให้ระหว่างตรวจ (~90 นาที); ถ้าสลิปถูกปฏิเสธ จะเหลือเวลาอย่างน้อย 10 นาทีให้อัปโหลดใหม่ (ไม่สั้นกว่ากำหนดเดิม) → refetch หลังทุกครั้งที่อัปโหลด
- ระหว่าง `Verifying`/`NeedsReview` ซ่อนตัวนับถอยหลัง (แสดงสถานะแทน)
- นับถึง 0 ขณะยังไม่ได้ส่งสลิป → แสดง "หมดเวลา" ปิดช่องอัปโหลด และเรียก `GET /payment` อีกครั้งเพื่อยืนยัน

## QR PromptPay

- วาดจาก `qrPayload` ตรง ๆ ด้วยไลบรารี QR (เช่น `qrcode` / `qrcode.react`) error correction `M` ขึ้นไป ขนาดอย่างน้อย ~220px บนมือถือ
- payload มียอดเงินฝังอยู่แล้ว ผู้ใช้สแกนแล้วไม่ต้องพิมพ์ยอดเอง; แต่ยังต้องแสดง `amount` ตัวใหญ่ พร้อมปุ่มคัดลอกยอด และ `accountName` ให้ตรวจ
- ไม่มีเลข PromptPay แยกใน API (อยู่ใน payload แล้ว) — ไม่ต้องแสดง
- มือถือ: มีปุ่ม "บันทึกรูป QR" (ดาวน์โหลดเป็น PNG) เพราะผู้ใช้ส่วนใหญ่สแกนจากรูปในแอปธนาคาร

## อัปโหลดไฟล์ (มือถือ)

- `<input type="file" accept="image/jpeg,image/png,image/webp">` — บน iOS การระบุ accept แบบนี้ทำให้ระบบแปลง HEIC เป็น JPEG ให้เอง (อย่าใช้ `image/*` อย่างเดียว)
- เช็กฝั่งหน้าบ้านก่อนส่ง: ขนาด ≤ 5 MB และชนิดตามด้านบน (ช่วย UX เท่านั้น หลังบ้านเช็กซ้ำ)
- แสดง preview รูปก่อนกดส่ง; ปิดปุ่มระหว่างส่ง

## ออเดอร์ฟรี (ยอด 0 บาท)

ถ้าตอนสร้างออเดอร์ (`POST /events/{slug}/orders`) ได้ `payableAmount === 0` ออเดอร์เป็น `Paid` และมีเลขบิบแล้ว → **ข้ามหน้า 5** ไปหน้า 6 ได้เลย (ถ้าเปิดหน้า 5 จะได้ `409 ORDER_ALREADY_PAID` อยู่ดี)

## สถานการณ์สำหรับ mock / ทดสอบหน้าบ้าน

1. สลิปผ่าน → `200 Verified` → หน้า 6
2. ธนาคารหน่วง → `202 Verifying` → poll → `Paid`
3. ยอดไม่ตรง → `422 SLIP_REJECTED reason AMOUNT_MISMATCH` → อัปโหลดใหม่ → ผ่าน
4. รูปเดิมซ้ำ → `409 SLIP_DUPLICATE`
5. ส่งสลิปตอนกำลังตรวจอีกใบ → `409 PAYMENT_IN_PROGRESS`
6. ไฟล์ HEIC/PDF/ใหญ่เกิน → `400 VALIDATION_ERROR` + `details.file`
7. ส่งสลิปหลังหมดเวลา → `202 NeedsReview reason ORDER_EXPIRED`; เปิดหน้าใหม่ได้ `409 ORDER_NOT_PAYABLE` → ต้องแสดง "รอเจ้าหน้าที่" ไม่ใช่ "หมดเวลา"
8. ออเดอร์ฟรี → ข้ามไปหน้า 6
9. กลับมาเปิดหน้า 5 ระหว่าง `Verifying` (ปิดแอปไปแล้วเปิดใหม่) → เข้าโหมดกำลังตรวจสอบ + poll

## ยังไม่มี (อย่าเพิ่งทำ)

- หน้าแอดมินตรวจสลิป (`NeedsReview`) และดูรูปสลิป — เฟส admin
- แจ้งผลทาง LINE — เฟส LINE
- payment gateway (บัตรเครดิต) — เฟส 2
