# DB schema (EF Core, PostgreSQL)

ไฟล์ `.cs` ในโฟลเดอร์นี้เป็น **ร่างอ้างอิง** ไม่ใช่โค้ดที่ build ใน repo เว็บ
**ยังไม่ได้ compile และยังไม่ได้สร้าง migration** ตรวจ build ก่อนใช้ใน repo API

> เก็บไว้ที่นี่ชั่วคราวระหว่างทำหน้าบ้านก่อน เมื่อมี repo API ให้ย้ายไปไว้ที่นั่น
> ตรวจว่า config ของ Next.js/TypeScript ไม่ได้อ่านโฟลเดอร์ `docs/`

| ไฟล์ | เนื้อหา |
|---|---|
| `Entities.cs` | Event, EventDistance, EventShirtSize, ShippingOption, PromoCode, Order, Registration, Payment และ enum |
| `MasterData.cs` | Province, District, Subdistrict, Title, Nationality, BloodType, EmergencyRelation |
| `AdminEntities.cs` | AdminUser (Identity), AdminEventAccess, AuditLog, CheckInLog, Refund, LineAccount, NotificationTemplate, Notification (outbox), Broadcast, AppSetting |
| `AppDbContext.cs` | mapping, index, constraint, concurrency token (สืบทอด `IdentityDbContext`) |
| `SeedData.cs` | seed ตัวเลือกทั่วไป, roles, เทมเพลต LINE, และอ่านข้อมูลจังหวัด/อำเภอ/ตำบลจากไฟล์ JSON |

namespace ในไฟล์ใช้ `RunEvent` ให้เปลี่ยนเป็นชื่อโปรเจกต์จริง (เช่น `HuebRun`) เมื่อยืนยันชื่อ

## ภาพรวมความสัมพันธ์

```
Event ─┬─ EventDistance (3/5/10K, โควตา, ราคา, early bird)
       ├─ EventShirtSize (สต็อกเสื้อ)
       ├─ ShippingOption (รับเอง / ไปรษณีย์)
       ├─ PromoCode
       └─ Order ─┬─ Registration (นักวิ่งแต่ละคน → เลขบิบ, QR เช็กอิน)
                 ├─ Payment (สลิป / gateway)
                 └─ Refund
Registration ── CheckInLog
Order ── LineAccount (สำหรับแจ้งเตือน)
Notification (outbox) ── Broadcast
AdminUser ── AdminEventAccess ── Event ;  AuditLog
```

## หลักออกแบบที่ใช้

- **ใช้ซ้ำข้ามปี**: ทุกอย่างผูกกับ `Event` เลขบัญชี PromptPay เก็บต่อ Event
- **สมัครกลุ่ม**: 1 `Order` (คนจ่าย) มีหลาย `Registration` (นักวิ่ง) ค่าส่งคิดต่อ Order
- **กันโควตา/สต็อก/โค้ดเกิน**: `ReservedCount` + check constraint + concurrency token (`uint Version` map เป็น xmin ของ Postgres ผ่าน `IsRowVersion()`) ตรวจพฤติกรรมให้แน่ใจหลังติดตั้งแพ็กเกจ Npgsql จริง
- **จับคู่ยอดโอน**: `PayableAmount` (ยอด + เศษสตางค์) unique เฉพาะ Order ที่ `Pending`
- **กันสลิปซ้ำ**: unique ที่ `SlipHash` และ `SlipTransRef` (เฉพาะที่ไม่ null)
- **หมดเวลาคืนโควตา**: `ExpiresAt` + partial index ให้ Hangfire หาออเดอร์ค้างได้เร็ว
- **ราคา snapshot**: เก็บราคาจริงตอนสมัครใน `Registration.PricePaid` และยอดใน `Order`
- **ที่อยู่**: FK ไป `Subdistrict` + ข้อความที่อยู่ snapshot ใน Order
- **master data ที่ถูกใช้ลบไม่ได้** (`Restrict`) ให้ปิดด้วย `IsActive`
- **Outbox**: ทุกข้อความเขียนลง `Notification` ก่อน แล้ว job ไปส่ง มี `DedupeKey` กันซ้ำ
- **Audit**: ผู้ใช้แอดมินที่ถูกอ้างใน log ลบไม่ได้ ให้ปิดด้วย `IsActive`

## ยังไม่ได้ใส่ / ต้องทำเพิ่ม

- ตาราง **ผู้จัด/องค์กร (Organizer/Tenant)** ถ้าจะขายให้หลายราย (ตอนนี้ Event ยังไม่ผูกผู้จัด)
- การออกเลขบิบ: ทำใน service ตอน Order เป็น `Paid` ไม่ใช่ใน DB
- service สร้าง Order + จองโควตา/เสื้อ/โค้ดใน transaction เดียว และ job คืนโควตาเมื่อหมดเวลา
- การเข้ารหัสฟิลด์อ่อนไหว (เช่น `IdCardOrPassport`, `MedicalNote`) หรือเลิกเก็บถ้าไม่จำเป็น (PDPA)
- migration แรก, index เพิ่มเติมตามผล load test
- ไฟล์ seed จังหวัด/อำเภอ/ตำบล (`provinces.json`, `districts.json`, `subdistricts.json`) ปรับ `JsonPropertyName` ใน `GeoDto` ให้ตรงไฟล์จริง และตรวจ license ข้อมูล
- ตั้ง `Hangfire` storage สำหรับ PostgreSQL
