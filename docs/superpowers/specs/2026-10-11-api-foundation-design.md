# HuebRun API — Foundation + Public Read Endpoints (Design)

วันที่: 2026-10-11 · สถานะ: รอรีวิว

## 1. เป้าหมาย

วางโครง ASP.NET Core Web API ให้พร้อมต่อยอด และเปิด endpoint ฝั่งอ่านของ public ให้หน้าบ้าน (Next.js) เรียกข้อมูลจริงแทน mock ได้บางส่วน

อ้างอิง: `docs/plan.md`, `docs/api-contract.md`, `docs/db-schema/`

### ความสำเร็จวัดจาก

- `dotnet build` และ `dotnet test` ผ่าน
- migration แรกสร้างทุกตารางใน schema ร่าง (รวม Identity) บน PostgreSQL `rundb` ได้
- เรียก endpoint ในหัวข้อ 4 ได้ผลตามรูปแบบที่กำหนด พร้อม seed ข้อมูลตัวอย่างใน Development
- `/openapi/v1.json` ใช้ generate type ฝั่ง TypeScript ได้

### นอกขอบเขต (ทำใน spec ถัดไป)

สร้าง Order/จองโควตา, payment/สลิป, ออกเลขบิบ, Hangfire, auth/admin endpoint, LINE, เช็กอิน

## 2. โครงสร้าง solution

```
HuebRun.slnx
src/
  HuebRun.Domain/          entity + enum (ไม่มี dependency ภายนอก ยกเว้น Identity stores สำหรับ AdminUser)
  HuebRun.Infrastructure/  AppDbContext, Migrations/, SeedData, Seed/geo/*.json
  HuebRun.Api/             Controllers/, Contracts/ (DTO), Errors/, Program.cs
tests/
  HuebRun.Tests/           xUnit, WebApplicationFactory, Testcontainers.PostgreSql
```

ทิศทาง dependency: `Api → Infrastructure → Domain`, `Tests → Api`

- .NET 10, EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore`
- Controllers (`[ApiController]`) จัดกลุ่มตาม resource: `EventsController`, `LookupsController`, `GeoController`
- OpenAPI ใช้ `Microsoft.AspNetCore.OpenApi` (built-in) map `/openapi/v1.json` เฉพาะ Development

## 3. ย้าย schema ร่างเข้าโค้ด

- คัดลอก `docs/db-schema/*.cs` เข้า Domain/Infrastructure เปลี่ยน namespace `RunEvent.*` → `HuebRun.*`
- แก้ให้ compile ผ่านโดยรักษาความหมายเดิม (mapping, index, check constraint, xmin concurrency token) ถ้าต้องเปลี่ยนเชิงความหมาย ให้บันทึกไว้ใน commit message
- ไฟล์ใน `docs/db-schema/` คงไว้เป็นต้นฉบับอ้างอิง ไม่ลบในรอบนี้
- สร้าง migration แรก `InitialCreate`

## 4. Endpoint (base `/api/v1`)

ทุก endpoint เป็น GET ไม่ต้อง login ตอบ JSON camelCase เวลาเป็น ISO 8601 UTC เงินเป็น number 2 ตำแหน่ง

| Endpoint | Response | กติกา |
|---|---|---|
| `/events/{slug}` | `{ id, slug, name, year, eventDate, registrationOpensAt, registrationClosesAt, registrationOpen }` | เฉพาะ `IsPublished = true` ไม่งั้น 404 · ไม่ส่ง PromptPay · `registrationOpen = opensAt <= now < closesAt` |
| `/events/{slug}/distances` | `[{ id, name, distanceKm, price, regularPrice, isEarlyBird, earlyBirdEndsAt, quota, remaining }]` | `isEarlyBird = EarlyBirdPrice != null && EarlyBirdEndsAt != null && now < EarlyBirdEndsAt` · `price` = early bird ถ้าใช่ ไม่งั้น `Price` · `remaining = max(0, Quota − ReservedCount)` · เรียงตาม `DistanceKm` |
| `/events/{slug}/shirt-sizes` | `[{ id, size, detail, remaining }]` | `remaining = max(0, Stock − ReservedCount)` · เรียงตาม `Id` |
| `/events/{slug}/shipping-options` | `[{ id, type, name, fee }]` | เฉพาะ `IsActive` · `type` เป็น string (`Pickup`/`Post`) |
| `/lookups` | `{ titles, nationalities, bloodTypes, emergencyRelations }` แต่ละตัวเป็น `[{ id, nameTh, nameEn }]` | เฉพาะ `IsActive` เรียง `SortOrder` แล้ว `Id` |
| `/geo/provinces` | `[{ id, nameTh, nameEn }]` | เรียง `NameTh` |
| `/geo/provinces/{id}/districts` | `[{ id, nameTh, nameEn }]` | province ไม่พบ → 404 `PROVINCE_NOT_FOUND` |
| `/geo/districts/{id}/subdistricts` | `[{ id, nameTh, nameEn, zipCode }]` | district ไม่พบ → 404 `DISTRICT_NOT_FOUND` |
| `/geo/zip/{zipCode}` | `[{ subdistrict:{id,nameTh}, district:{id,nameTh}, province:{id,nameTh}, zipCode }]` | `zipCode` ต้องเป็นตัวเลข 5 หลัก ไม่งั้น 400 `VALIDATION_ERROR` · ไม่พบคืน `[]` (200) |
| `/health` (นอก `/api/v1`) | `200 Healthy` / `503` | ใช้ health check ของ EF Core ตรวจ DB |

endpoint ที่ใต้ `/events/{slug}` ทั้งหมดใช้กติกาเดียวกัน: slug ไม่พบหรือยังไม่ publish → 404 `EVENT_NOT_FOUND`

การคำนวณราคา/คงเหลือแยกเป็นฟังก์ชัน pure ใน Domain (เช่น `EventDistance.GetEffectivePrice(DateTime now)`) เพื่อใช้ซ้ำตอนสร้าง Order ในรอบหน้า

## 5. Error handling

- รูปแบบเดียว: `{ "code": "EVENT_NOT_FOUND", "message": "...", "details": {...} | null }`
- `AppException(statusCode, code, message, details)` โยนจาก controller/service แล้ว `IExceptionHandler` แปลงเป็น response
- validation ของ model binding (`[ApiController]`) ถูกแปลงเป็น `400 VALIDATION_ERROR` โดย `details` = รายการฟิลด์ → ข้อความ
- exception อื่น → `500 INTERNAL_ERROR` ข้อความกลาง ไม่ส่ง stack trace, log เต็มที่ฝั่งเซิร์ฟเวอร์

## 6. การตั้งค่า

- `ConnectionStrings:DefaultConnection` เก็บด้วย `dotnet user-secrets` ในโปรเจกต์ Api; `appsettings.json` มีค่าว่าง ห้าม commit รหัสผ่าน
- `Cors:AllowedOrigins` (array) ค่าเริ่มต้น dev `http://localhost:3000`
- `TimeProvider.System` ลงทะเบียนใน DI; test แทนด้วย `FakeTimeProvider`
- DB: PostgreSQL `192.168.155.203:5432` ฐาน `rundb` (สร้างโดย `dotnet ef database update`)

## 7. Migration และ Seed

- migration รันด้วยมือ `dotnet ef database update` ไม่ auto-migrate ตอนแอปเริ่ม
- `SeedData.RunAsync` เรียกตอนแอปเริ่ม (หลังตรวจว่ามีตารางแล้ว) ทุกส่วน idempotent (seed เมื่อว่างเท่านั้น):
  - lookups, roles, notification templates (ตามร่างเดิม)
  - geo จาก `kongvut/thai-province-data` (MIT) วางที่ `src/HuebRun.Infrastructure/Seed/geo/` ปรับ `GeoDto` ตามฟิลด์จริง (`amphure_id` ฯลฯ) และใส่ไฟล์ LICENSE/ที่มาไว้ข้างกัน
  - **Development เท่านั้น**: Event ตัวอย่าง slug `hueb-run-2027` (published, เปิดรับอยู่) ระยะ 3/5/10K มี early bird, ไซซ์เสื้อ XS–3XL, ShippingOption รับเอง (0) และไปรษณีย์ (60)

## 8. การทดสอบ

TDD: เขียน test ก่อน implement แต่ละ endpoint

- **Unit**: ราคา early bird (ก่อน/ตรง/หลังเวลาตัด, ไม่มี early bird), `remaining` ไม่ติดลบ, `registrationOpen` ที่ขอบเวลา
- **Integration** (`WebApplicationFactory` + Testcontainers PostgreSQL, apply migration จริง, ใช้ `FakeTimeProvider`):
  - แต่ละ endpoint คืนรูปแบบถูกต้อง
  - event ไม่ publish / slug ไม่มี → 404 `EVENT_NOT_FOUND`
  - lookup และ shipping ที่ inactive ไม่ถูกส่ง
  - geo: districts ของ province ที่ไม่มี → 404, zip ไม่ใช่ 5 หลัก → 400, zip ไม่พบ → `[]`
  - error ไม่คาดคิด → 500 รูปแบบกลาง
  - response ของ event ไม่มีฟิลด์ PromptPay
- ข้อกำหนด: ต้องเปิด Docker ไว้ตอนรัน test

## 9. ความเสี่ยง / หมายเหตุ

- schema ร่างไม่เคย compile อาจต้องแก้เล็กน้อยตอนย้าย
- xmin ใน Npgsql 10: ตรวจว่า `IsRowVersion()` บน `uint` map เป็น `xmin` ถูกต้อง (ใช้จริงในรอบสร้าง Order)
- ข้อมูล geo ของ kongvut มี id ของตนเอง ใช้เป็น PK ตรง ๆ ตามที่ schema ออกแบบไว้ (ไม่ auto-generate)
