# Admin API — แผนงานต่อ

วันที่: 2026-10-12 · สถานะ: ร่าง รอเลือกเรื่องที่ต้องตัดสินใจ (หัวข้อ 3) แล้วค่อยทำ spec ต่อเฟส

## 1. ตอนนี้อยู่ตรงไหน

**ฝั่งนักวิ่งใช้งานจริงได้แล้ว** บน Railway: ดูงาน → สมัคร (จองโควตาแบบ atomic) → QR PromptPay → อัปสลิป → ตรวจกับ SlipOK → `Paid` + เลขบิบ, ออเดอร์ไม่จ่ายหมดเวลาเอง, สลิปที่ตรวจไม่ได้ทันทีมี job ตรวจซ้ำ

**ฝั่งแอดมินยังไม่มี API เลย** — ตอนนี้ทุกอย่างทำผ่าน SQL (`docs/sql/create-event.sql`, แก้ราคา, ดูสลิป `NeedsReview`)

มีอยู่แล้วที่ใช้ต่อได้:

| ส่วน | อยู่ที่ |
|---|---|
| ตาราง Identity (`AspNetUsers` ฯลฯ), `AdminUser`, `AdminEventAccess`, role 4 ตัว (SuperAdmin, Organizer, Finance, Staff) seed แล้ว | `Domain/AdminEntities.cs`, `Infrastructure/SeedData.cs` |
| `AuditLog`, `CheckInLog`, `Refund`, `Broadcast`, `Notification` (outbox), `NotificationTemplate`, `LineAccount` | `Domain/AdminEntities.cs` (ตารางมีแล้ว ยังไม่มีโค้ดใช้) |
| รายการ endpoint ที่ออกแบบไว้ + บทบาทที่ทำได้ | `docs/api-contract.md` หัวข้อ Admin |
| หน้าแอดมินฝั่งหน้าบ้าน 11 หน้า (ใช้ข้อมูลจำลอง) + login/session + สิทธิ์ต่อหน้า | repo `hueb-run-webapp`: `src/app/admin/`, `src/features/admin/auth/access.ts` |
| `OrderPaymentService.MarkPaidAsync` (ออกบิบ, ร่วม transaction ได้), `PaymentService.ApplyResultAsync`, `ISlipStorage` | `Infrastructure/Orders`, `Infrastructure/Payments` |

สิทธิ์ต่อหน้าฝั่งหน้าบ้าน (ต้องตรงกับ API):

| หน้า | SuperAdmin | Organizer | Finance | Staff |
|---|:-:|:-:|:-:|:-:|
| แดชบอร์ด, ออเดอร์, นักวิ่ง | ✓ | ✓ | ✓ | |
| ตรวจสลิป, คืนเงิน | ✓ | | ✓ | |
| เช็กอิน | ✓ | ✓ | | ✓ |
| โควตา/ตั้งค่างาน, โค้ดส่วนลด, ประกาศ | ✓ | ✓ | | |
| ผู้ใช้, audit | ✓ | | | |

## 2. ลำดับงานที่แนะนำ

แบ่งเป็นเฟส แต่ละเฟสทำ spec → plan → implement แยกกัน (แบบเดียวกับ order registration / payment)

### เฟส A — ต้องมีก่อนเปิดรับสมัครจริง

| # | งาน | endpoint | หมายเหตุ |
|---|---|---|---|
| A1 | **Auth + ผู้ใช้แอดมิน** | `POST /admin/auth/login`, `GET /admin/auth/me`, `GET/POST/PATCH /admin/users` | ฐานของทุกอย่าง — ดูหัวข้อ 3.1–3.3 |
| A2 | **Audit log กลาง** | (ไม่มี endpoint — ใช้ภายใน) | ทุก endpoint ที่เขียนข้อมูลต้องบันทึก `AuditLog` ใน transaction เดียวกัน |
| A3 | **ตรวจสลิป** | `GET /admin/events/{id}/payments?status=NeedsReview`, `GET /admin/payments/{id}/slip` (presigned URL), `POST /admin/payments/{id}/approve` · `/reject` · `/recheck` | งานประจำของ Finance — กติกาในหัวข้อ 4.1 |
| A4 | **ออเดอร์** | `GET /admin/events/{id}/orders?status=&q=&page=`, `GET /admin/orders/{id}` | ค้นด้วยเลขออเดอร์/ชื่อ/เบอร์, ดูประวัติ Payment ทุกใบ |
| A5 | **ตั้งค่างาน (แทน SQL)** | `PUT /admin/events/{id}`, `GET/PUT /admin/events/{id}/distances`, `GET/PUT …/shirt-sizes`, `GET/PUT …/shipping-options`, `POST /admin/events` (SuperAdmin) | PromptPay, วันเปิด/ปิด, ราคา, early bird, โควตา — กติกาหัวข้อ 4.3 |
| A6 | **โค้ดส่วนลด** | `GET/POST/PUT /admin/events/{id}/promos` | ปิดโค้ดแทนการลบ (`IsActive`), ลด `MaxUses` ต่ำกว่า `UsedCount` ไม่ได้ |
| A7 | **แดชบอร์ด** | `GET /admin/events/{id}/dashboard` | จำนวนสมัคร/จ่ายแล้ว/รอจ่าย/รอตรวจ, รายได้, โควตาคงเหลือต่อระยะ, สต็อกเสื้อ |
| A8 | **Rate limit ฝั่งนักวิ่ง** (ไม่ใช่ admin แต่ต้องทำก่อนเปิด) | – | จำกัด `POST /orders` และ `POST /payment/slip` ต่อ IP — กัน bot จองค้าง 30 นาที และเผาโควตา SlipOK |

### เฟส B — ก่อนวันงาน

| # | งาน | endpoint |
|---|---|---|
| B1 | รายชื่อนักวิ่ง + export Excel | `GET /admin/events/{id}/registrations?distance=&q=`, `GET /admin/events/{id}/orders/export` |
| B2 | เลขพัสดุ (ส่ง EMS) | `PATCH /admin/orders/{id}/tracking` |
| B3 | เช็กอิน / รับของหน้างาน | `POST /admin/checkin/scan`, `POST /admin/checkin/{registrationId}/confirm` · `/undo`, `GET /admin/events/{id}/checkins` |

### เฟส C — หลังเปิดใช้งาน

| # | งาน | endpoint | ต้องมีก่อน |
|---|---|---|---|
| C1 | คืนเงิน | `GET /admin/events/{id}/refunds`, `POST /admin/refunds/{id}/approve` · `/reject` · `/complete`, `POST /admin/orders/{id}/refunds` (สร้างคำขอ) | – |
| C2 | ดู audit | `GET /admin/audit?userId=&action=&from=&to=` | A2 |
| C3 | ประกาศ (broadcast) | `GET/POST /admin/events/{id}/broadcasts`, `POST /admin/broadcasts/{id}/send` | ระบบ LINE OA + outbox ส่งข้อความ (เฟส LINE) |

## 3. เรื่องที่ต้องตัดสินใจก่อนเริ่ม

### 3.1 รูปแบบ auth ระหว่างหน้าบ้านกับ API

หน้าบ้านเรียก API จากฝั่ง server (server component / server action) เท่านั้น และมี session cookie ลงลายเซ็นของตัวเองอยู่แล้ว (`features/admin/auth/session.ts` เขียนไว้ว่า "เมื่อมี API จริง ให้เก็บ token ที่ API ออกให้แทน")

| ทางเลือก | สรุป |
|---|---|
| **A (แนะนำ) JWT bearer อายุ 8 ชม.** | `POST /admin/auth/login` คืน `{ token, expiresAt, user: { id, name, email, role, eventIds } }` → หน้าบ้านเก็บ token ไว้ใน session cookie เดิม แล้วส่ง `Authorization: Bearer` ทุก request · API ตรวจ `SecurityStamp` ของผู้ใช้ทุก request (query เบา ๆ) → ปิดบัญชี/เปลี่ยนรหัสแล้วมีผลทันที ไม่ต้องรอ token หมดอายุ |
| B Identity cookie ของ ASP.NET | ต้องให้หน้าบ้าน forward cookie ข้ามโดเมน ยุ่งกว่าเมื่อเรียกจาก server |
| C ให้หน้าบ้านตรวจรหัสเอง แล้วส่ง API key กลาง | ง่ายสุดแต่ API ไม่รู้ว่าใครทำ → audit log ไร้ประโยชน์ ไม่แนะนำ |

### 3.2 ผู้ใช้ 1 คนมีกี่บทบาท

หน้าบ้านออกแบบไว้ **1 คน 1 บทบาท** (`role: AdminRole`) — แนะนำให้ API บังคับแบบเดียวกัน (Identity เก็บได้หลาย role แต่เราใช้ตัวเดียว) จะได้ไม่ต้องแก้หน้าบ้าน

### 3.3 SuperAdmin คนแรกมาจากไหน

ยังไม่มีหน้าสมัคร และไม่ควรมี — แนะนำ: ตั้ง `Admin__BootstrapEmail` + `Admin__BootstrapPassword` ใน Railway ครั้งเดียว → ตอนแอปเริ่ม ถ้ายังไม่มี SuperAdmin เลยให้สร้างจากค่านี้ → ลบ variable ทิ้งหลัง login ได้ (เปลี่ยนรหัสผ่านทันทีครั้งแรก)

### 3.4 ข้อมูลอ่อนไหว

`MedicalNote`, ผู้ติดต่อฉุกเฉิน, เลขบัตร/ที่อยู่ — ใครเห็นได้บ้าง? แนะนำ: Organizer/SuperAdmin เห็นทั้งหมด (ใช้หน้างาน), Finance เห็นแค่ชื่อ/เบอร์/ยอด, Staff เห็นแค่ชื่อ/ระยะ/ไซซ์/บิบ และทุกครั้งที่ export บันทึก audit

### 3.5 Login ป้องกันเดารหัส

ใช้ lockout ของ Identity (เช่น ผิด 5 ครั้งล็อก 15 นาที) + rate limit ต่อ IP ที่ `/admin/auth/login` · 2FA ยังไม่ทำในเฟส A

## 4. กติกาธุรกิจที่ต้องเขียนลง spec

### 4.1 ตรวจสลิป (A3)

Payment `NeedsReview` มี 3 เหตุผล แต่ละแบบแอดมินทำต่อไม่เหมือนกัน:

| `RejectReason` | ความหมาย | approve ทำอะไร |
|---|---|---|
| `DUPLICATE_UNKNOWN` | SlipOK บอกสลิปซ้ำ แต่เราไม่มีสลิปใบนั้น (มักเป็นครั้งก่อน timeout) | ดูรูปสลิปเทียบยอด → approve = `Verified` + `MarkPaidAsync` ตามปกติ |
| `VERIFY_TIMEOUT` | ตรวจซ้ำครบ 6 ครั้งไม่ได้ผล (SlipOK ล่ม/โควตาหมด) | ปุ่ม **recheck** (ส่งเข้า job ใหม่) หรือ approve มือหลังเช็กบัญชีธนาคาร |
| `ORDER_EXPIRED` | เงินเข้าแต่ออเดอร์หมดเวลาไปแล้ว (คืนโควตาไปแล้ว) | approve ต้อง **จองโควตา/เสื้อใหม่แบบ atomic** ก่อน ถ้าเต็ม → ต้องคืนเงิน (สร้าง Refund) ห้าม approve |

- approve/reject ต้องเป็น update แบบมีเงื่อนไข (`WHERE Status = 'NeedsReview'`) กันกดซ้ำ/สองคนกดพร้อมกัน
- **แก้ของเดิมก่อน:** `ApplyVerifiedAsync` ตอนนี้ถือว่า `MarkPaidAsync` ที่คืน `AlreadyPaid: true` เป็นความสำเร็จ — พอมีปุ่ม approve จะเกิดกรณี "Payment สองใบ Verified บนออเดอร์เดียว" ได้ → ต้องเปลี่ยนให้ `AlreadyPaid` = `NeedsReview` (บันทึกไว้ในรีวิวรอบ payment)
- reject ต้องมีเหตุผล (ข้อความแสดงให้นักวิ่ง) และเปิดให้อัปโหลดใหม่ได้ถ้าออเดอร์ยัง `Pending`
- รูปสลิป: เพิ่ม `ISlipStorage.GetPresignedUrlAsync(key, ttl)` อายุสั้น (เช่น 5 นาที) — bucket ยังเป็น private เสมอ

### 4.2 ออเดอร์ (A4)

- รายการต้องมี: เลขออเดอร์, ผู้ติดต่อ, จำนวนนักวิ่ง, ยอด/ยอดโอน, สถานะ, สถานะ Payment ล่าสุด, เวลา
- pagination แบบ `{ items, page, pageSize, total }` ใช้รูปแบบเดียวทุก endpoint รายการ
- ยกเลิกออเดอร์ (ถ้าต้องการในเฟส A): Pending → Cancelled + คืนโควตา (ใช้ logic เดียวกับ `OrderExpiryService`) · Paid → ต้องผ่าน Refund เท่านั้น

### 4.3 ตั้งค่างาน (A5)

- ลด `Quota` ต่ำกว่า `ReservedCount` ไม่ได้ (409) · ลด `Stock` เสื้อต่ำกว่าที่จองไม่ได้
- เปลี่ยน `BibPrefix` หลังออกบิบแล้วไม่ได้
- ราคาใหม่มีผลกับออเดอร์ใหม่เท่านั้น (ออเดอร์เก็บ snapshot ไว้แล้ว)
- เปลี่ยน `PromptPayId` → เตือนในหน้าว่าต้องแก้บัญชีใน SlipOK LIFF ให้ตรงกัน ไม่งั้นทุกสลิปจะ `WRONG_RECEIVER`
- ทุกค่าที่แก้บันทึก `AuditLog` ทั้งค่าเก่าและใหม่

### 4.4 คืนเงิน (C1)

- approve คืนเงิน = คืนโควตา/ไซซ์เสื้อ, ยกเลิกเลขบิบ (`Registration.IsCancelled`), ออเดอร์ → `Refunded` เมื่อ `complete` (โอนคืนแล้ว ใส่เลขอ้างอิง)
- คืนเงินเฉพาะบางคนในออเดอร์กลุ่ม — ต้องตัดสินใจว่ารองรับไหม (แนะนำ: เฟสแรกคืนทั้งออเดอร์เท่านั้น)

### 4.5 เช็กอิน (B3)

- `scan` ด้วย `checkInToken` จาก QR บนตั๋ว → คืนข้อมูลนักวิ่ง + สถานะ (ยังไม่เช็กอิน / เช็กอินแล้วเมื่อ … โดยใคร)
- เช็กอินซ้ำไม่ error — ตอบว่าเช็กอินไปแล้วพร้อมเวลา · ทุกการกระทำลง `CheckInLog`
- Staff เห็นเฉพาะงานที่มี `AdminEventAccess`

## 5. กติกากลางของ Admin API

- path อยู่ใต้ `/api/v1/admin/...` · error รูปแบบเดิม `{ code, message, details }`
- `401` ไม่มี/หมดอายุ token · `403 FORBIDDEN` บทบาทไม่พอ หรือไม่มีสิทธิ์งานนั้น (SuperAdmin เข้าได้ทุกงาน)
- สิทธิ์ตามบทบาทตรงกับตารางหัวข้อ 1 — เก็บไว้ที่เดียวเป็น policy (`[Authorize(Policy = "Finance")]` ฯลฯ)
- ทุก endpoint ที่เขียนข้อมูล: transaction เดียวกับ `AuditLog` (`Action` เช่น `payment.approve`, `event.update`, `promo.create`)
- เวลา UTC, เงิน `decimal` 2 ตำแหน่ง (เหมือนฝั่งนักวิ่ง)
- service อยู่ใน `Infrastructure/Admin/...` ตามแพตเทิร์นเดิม, controller บาง ๆ ใน `Api/Controllers/Admin/`
- เทสต์: integration test ทุก endpoint (Testcontainers) รวม **ทุกบทบาท × ทุก endpoint** ว่าได้ 403 ตรงตามตาราง และงานที่ไม่มีสิทธิ์ได้ 403

## 6. งานค้างจากรอบ payment (ควรเก็บพร้อมเฟส A)

- `AlreadyPaid` จาก `MarkPaidAsync` ต้องเป็น `NeedsReview` (หัวข้อ 4.1)
- SlipOK ตอบ JSON ที่ไม่ใช่ object / `code` ไม่ใช่ตัวเลข → ตอนนี้ได้ 500 (กู้เองตอน recheck) ควรเป็น `Unavailable`
- สองออเดอร์ใช้ `transRef` เดียวกันพร้อมกัน → ใบที่สองได้ 500 จาก unique index ควร catch เป็น `DUPLICATE_SLIP`
- grace หลังสลิปไม่ผ่านอาจทำให้ออเดอร์ที่เลยเวลาแต่ job ยังไม่ปิดกลับมา Pending → เพิ่ม `ExpiresAt > now` ในเงื่อนไข
- `Minio:AllowedExtensions` ว่างไม่ถูกจับตอนเริ่มแอป → เพิ่ม `[MinLength(1)]`

## 7. ก่อนเปิดรับสมัครจริง (ไม่ใช่โค้ด)

- คืนราคาจริง (ตอนนี้ตั้ง 10 บาทเพื่อทดสอบ) และล้างออเดอร์ทดสอบ (เลขออเดอร์/บิบ/โควตา)
- ซื้อแพ็ก SlipOK (ฟรี 100 สลิป/เดือน)
- เปลี่ยน key ที่เคยหลุด: SlipOK API key, password Postgres (Regenerate แล้ว redeploy API), credentials ของ bucket, key MinIO บริษัท
- ปิด TCP Proxy ของ Postgres ถ้าไม่ใช้, เปิด Backup
- PDPA: ระบุการเก็บข้อมูลบน cloud ต่างประเทศ (`docs/legal-checklist.md`)

## 8. ขั้นต่อไป

1. ตอบหัวข้อ 3 (โดยเฉพาะ 3.1 auth และ 3.3 SuperAdmin คนแรก)
2. ทำ spec เฟส A1+A2 (auth, ผู้ใช้, audit) — เป็นฐานของทุกหน้า
3. ตามด้วย A3 ตรวจสลิป (ใช้บ่อยที่สุด) แล้ว A5 ตั้งค่างาน (เลิกใช้ SQL)
