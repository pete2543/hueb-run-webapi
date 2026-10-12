# Deploy บน Railway

repo มี `Dockerfile` + `railway.json` แล้ว: Railway build จาก Dockerfile, แอป migrate + seed เองตอนเริ่ม และ `/health` ไม่ผ่านจนกว่า migration จะลงครบ (Railway จึงไม่สลับไปเวอร์ชันที่ DB ไม่พร้อม)

## 1. สร้าง project (ครั้งเดียว)

ใน Railway dashboard → **New Project** → เลือก region **Singapore** (ใช้ region เดียวกันทุก service)

1. **Postgres:** New → Database → **PostgreSQL**
2. **Bucket (เก็บรูปสลิป):** New → **Bucket** → ตั้งชื่อเช่น `huebrun-slips` → ลอง region `sin` ก่อน ถ้าสร้างไม่ผ่านใช้ region อื่นที่ใกล้ที่สุด (เปลี่ยน region ทีหลังไม่ได้) — bucket เป็น private อยู่แล้ว **ห้ามเปิด public**
3. **API:** New → **GitHub Repo** → `pete2543/hueb-run-webapi` → Settings → Source: เลือก branch ที่จะ deploy (เช่น `main` หลัง merge) — Railway อ่าน `railway.json` เอง

## 2. ตั้ง Variables ของ service API

Service API → **Variables** → Raw Editor แล้ววาง (แก้ค่าที่อยู่ใน `<...>`):

```env
ConnectionStrings__DefaultConnection=Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}
SlipOk__BranchId=<Branch ID จาก SlipOK>
SlipOk__ApiKey=<API key จาก SlipOK>
Minio__Endpoint=${{<ชื่อ bucket>.ENDPOINT}}
Minio__Region=${{<ชื่อ bucket>.REGION}}
Minio__Bucket=${{<ชื่อ bucket>.BUCKET}}
Minio__AccessKey=${{<ชื่อ bucket>.ACCESS_KEY_ID}}
Minio__SecretKey=${{<ชื่อ bucket>.SECRET_ACCESS_KEY}}
Minio__ForcePathStyle=false
Cors__AllowedOrigins__0=<URL หน้าบ้าน เช่น https://huebrun.example.com>
```

- `${{Postgres.…}}` คือ reference variable ของ Railway — ใช้ตามนี้ได้เลยถ้า service Postgres ชื่อ `Postgres` (ต่อผ่าน private network ไม่ออกเน็ต) · ห้ามใช้ `DATABASE_URL` ตรง ๆ เพราะ Npgsql ไม่รับรูปแบบ `postgresql://`
- `<ชื่อ bucket>` = ชื่อ service ของ bucket บน canvas (เช่น `lightweight-tin`) — Railway แทนค่าเอง ไม่ต้อง copy key มาวาง · `BUCKET` คือชื่อจริงที่มีรหัสต่อท้าย (เช่น `lightweight-tin-x1eceftla`)
- `Minio__ForcePathStyle=false` จำเป็น: bucket ของ Railway (Tigris) ที่สร้างใหม่รับแค่ URL แบบ `https://<bucket>.t3.storageapi.dev/...` (ชื่อ `Minio__*` ใช้กับ object storage แบบ S3 ทุกเจ้า ไม่ใช่เฉพาะ MinIO)
- หน้าบ้านหลายโดเมนเพิ่ม `Cors__AllowedOrigins__1=...`
- **ห้ามตั้ง** `Payments__Mode=Fake` — production แอปจะไม่ยอมเริ่ม (โหมดจำลองใช้ได้แค่ Development)
- ไม่ต้องตั้ง `PORT` / `ASPNETCORE_ENVIRONMENT` (Railway ส่ง `PORT` มาเอง, image ตั้ง Production ไว้แล้ว)

## 3. Deploy

กด **Deploy** (หรือ push เข้า branch ที่เลือก) → ดู log:

1. Build image
2. แอปขึ้น → migrate ฐานข้อมูลที่ `ConnectionStrings__DefaultConnection` ชี้อยู่ (database `railway`) แล้ว seed ข้อมูลกลาง (คำนำหน้า สัญชาติ จังหวัด ฯลฯ)
3. health check `/health` ต้องได้ `Healthy`

ไม่ต้องรัน `docs/sql/schema.sql` เอง (ไฟล์นั้นมีไว้ดูโครงสร้าง/ให้ DBA) · ปิด migrate อัตโนมัติได้ด้วย `Database__MigrateOnStartup=false` แล้วรัน `/app/migrate.sh` เอง

ถ้าแอปไม่ขึ้นเพราะ `DataAnnotation validation failed for 'SlipOkOptions'/'MinioOptions'` = ขาด variable ตามที่ log บอกชื่อ

Settings → Networking → **Generate Domain** เพื่อได้ URL สาธารณะ (เช่น `https://xxx.up.railway.app`) → หน้าบ้านใช้ `https://xxx.up.railway.app/api/v1`

ควรรัน **1 replica** ไปก่อน (job หมดเวลา/ตรวจสลิปซ้ำรันหลายตัวได้ปลอดภัย แต่ไม่จำเป็น)

## 4. สร้างงานวิ่ง (ครั้งแรก)

production ไม่มี seed งานตัวอย่าง และยังไม่มีหน้าแอดมิน → สร้างด้วย SQL:

1. แก้ค่าใน [`docs/sql/create-event.sql`](sql/create-event.sql) — ชื่องาน, slug, วันเวลา (UTC), ระยะ/ราคา/โควตา, ไซซ์เสื้อ, ค่าส่ง และ **`PromptPayId` + ชื่อบัญชีจริง**
2. Postgres → **Data** → Query (หรือ `psql`/DataGrip ด้วย connection string จากแท็บ Connect) → วางทั้งไฟล์แล้วรันครั้งเดียว — **ต้องอยู่ใน database `railway`** (เช็กด้วย `SELECT current_database();`) ไม่ใช่ `postgres`
3. เช็ก `GET https://xxx.up.railway.app/api/v1/events/<slug>`

**ผูกบัญชีเดียวกันใน SlipOK LIFF** ไม่งั้นทุกสลิปจะได้ `WRONG_RECEIVER`

## 5. ทดสอบหลัง deploy

1. สมัครจากหน้าบ้าน → หน้าชำระเงินเห็น QR + ยอดมีเศษสตางค์
2. โอนจริงตามยอด (ยอดเล็ก ๆ ด้วยโค้ดส่วนลดทดสอบ) → อัปสลิป → `Paid` + มีเลขบิบ
3. Bucket → ดูว่ามีไฟล์ `slips/<eventId>/<orderId>/<paymentId>.jpg`

ถ้าอัปสลิปได้ 500 ที่ขั้นเก็บรูป ดู log: `403`/`SignatureDoesNotMatch` = ค่า `Minio__*` ไม่ตรงกับ bucket หรือลืม `Minio__ForcePathStyle=false` / `Minio__Region=auto`

## อื่น ๆ

- **Backup:** ตรวจว่าแผนที่ใช้มี backup ของ Postgres หรือไม่ (Postgres → Backups) ถ้าไม่มีให้ตั้ง `pg_dump` ตามเวลา
- **PDPA:** ข้อมูลนักวิ่งและสลิปอยู่บน cloud ต่างประเทศ (Singapore) — ระบุในนโยบายความเป็นส่วนตัว (ดู `docs/legal-checklist.md`)
- **ทดสอบ image ในเครื่อง:** `docker build -t huebrun-api .` แล้ว `docker run -p 8080:8080 -e ConnectionStrings__DefaultConnection=... (variables ตามข้อ 2) huebrun-api`
