# HuebRun Web API

API ระบบรับสมัครงานวิ่ง (.NET 10 + PostgreSQL) — สัญญา API: [`docs/api-contract.md`](docs/api-contract.md), หน้าชำระเงินฝั่งหน้าบ้าน: [`docs/frontend-payment-handoff.md`](docs/frontend-payment-handoff.md)

## เริ่มรันบนเครื่อง dev

ต้องมี: .NET SDK 10 (`global.json`), PostgreSQL ที่เข้าถึงได้, Docker (เฉพาะตอนรันเทสต์)

```bash
dotnet tool restore                      # dotnet-ef
./scripts/setup-dev-secrets.sh           # ถามค่าลับทีละตัว เก็บใน user-secrets
dotnet ef database update -p src/HuebRun.Infrastructure -s src/HuebRun.Api
dotnet run --project src/HuebRun.Api --launch-profile http
```

API อยู่ที่ `http://localhost:5144/api/v1` · health: `/health` · OpenAPI: `/openapi/v1.json` · งานตัวอย่าง (seed): `hueb-run-2027`

## Config

ค่าที่ไม่ลับอยู่ใน `src/HuebRun.Api/appsettings.json` และ `appsettings.Development.json`
**ค่าลับไม่อยู่ใน repo** — dev เก็บใน [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) (สคริปต์ด้านบนตั้งให้), server ใช้ environment variable (`:` → `__` เช่น `ConnectionStrings__DefaultConnection`)

| Key | ลับ | ต้องมีเมื่อ | ตัวอย่าง / ค่าเริ่ม |
|---|---|---|---|
| `ConnectionStrings:DefaultConnection` | ✓ | เสมอ | `Host=…;Port=5432;Database=rundb;Username=postgres;Password=…` |
| `SlipOk:BranchId` | ✓ | `Payments:Mode` ไม่ใช่ `Fake` | จากหน้า SlipOK |
| `SlipOk:ApiKey` | ✓ | 〃 | จากหน้า SlipOK |
| `Minio:AccessKey` | ✓ | 〃 | |
| `Minio:SecretKey` | ✓ | 〃 | |
| `Minio:Endpoint`, `Minio:Bucket`, `Minio:UseSsl` | | 〃 | dev: `192.168.155.208:9000`, `huebrun-slips` (private), `false` · Railway: ดู deploy guide |
| `Admin:JwtSecret` | ✓ | ลงนาม token แอดมิน ≥ 32 ตัวอักษร สุ่มยาว ๆ (แอปไม่ขึ้นถ้าขาด) | dev มีค่าใน `appsettings.Development.json` · Railway: ตั้ง `Admin__JwtSecret` |
| `Admin:BootstrapEmail`, `Admin:BootstrapPassword` | | สร้าง SuperAdmin คนแรกตอนแอปเริ่ม (เมื่อยังไม่มี SuperAdmin เลย) รหัสผ่าน ≥ 10 ตัว | ใส่ครั้งเดียว login แล้วเปลี่ยนรหัส (`POST /admin/auth/change-password`) และลบตัวแปรทิ้ง |
| `RateLimits:OrdersPerWindow`, `RateLimits:SlipsPerWindow`, `RateLimits:WindowMinutes` | | จำกัดต่อ IP: สร้างออเดอร์ / อัปสลิป ต่อหน้าต่างเวลา (นาที) เกินแล้ว `429 TOO_MANY_REQUESTS` + `Retry-After` | ค่าเริ่ม `10`, `20`, `10` (เผื่อหลายคนใช้ Wi-Fi เดียวกัน) |
| `Minio:Region`, `Minio:ForcePathStyle` | | | MinIO: `us-east-1`, `true` (ค่าเริ่ม) · Railway Bucket: `auto`, `false` |
| `Payments:Mode` | | | dev: `Fake`; ไม่ตั้ง = ของจริง |
| `Cors:AllowedOrigins` | | | `http://localhost:3000` |

ขาดค่าที่ "ต้องมี" แอปจะไม่ยอมเริ่ม (บอกชื่อ key ที่ขาดใน log)

### โหมดจำลองการชำระเงิน (`Payments:Mode = Fake`)

เปิดไว้ใน `appsettings.Development.json` — ไม่เรียก SlipOK, เก็บรูปสลิปในเครื่อง, ชื่อไฟล์สลิปเลือกผลตรวจ (ตารางใน handoff หน้าบ้าน) จึงรันได้โดยไม่ต้องมี key ของ SlipOK/MinIO
**ใช้ได้เฉพาะ Development** — environment อื่นแอปไม่ยอมเริ่ม

ใช้ของจริง: ตั้ง key SlipOK/MinIO แล้วลบ `"Payments": { "Mode": "Fake" }` ออกจาก `appsettings.Development.json` และผูกบัญชี PromptPay ของงานใน SlipOK LIFF ให้ตรงกับ `Event.PromptPayId`

## Deploy

Railway: ดู [`docs/deploy-railway.md`](docs/deploy-railway.md) (`Dockerfile` + `railway.json` อยู่ที่ root; แอป migrate + seed เองตอนเริ่ม ปิดได้ด้วย `Database:MigrateOnStartup=false`)

## เทสต์

```bash
dotnet test                              # ต้องเปิด Docker (Testcontainers: Postgres + MinIO)
```

## โครงสร้าง

- `src/HuebRun.Domain` — entity และกติกาคำนวณล้วน (ราคา, QR PromptPay)
- `src/HuebRun.Infrastructure` — EF Core, migration, service ธุรกิจ (`Orders/`, `Payments/`)
- `src/HuebRun.Api` — controller, background job, จัดการ error กลาง
- `docs/superpowers/specs`, `docs/superpowers/plans` — design และแผนของแต่ละฟีเจอร์
