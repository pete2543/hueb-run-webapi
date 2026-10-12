-- สร้างงานวิ่งใน DB production (ยังไม่มีหน้าแอดมิน) — แก้ค่าในส่วน "ตั้งค่า" แล้วรันทั้งไฟล์ครั้งเดียว
-- เวลาเป็น UTC (เวลาไทย = UTC+7) · PromptPayId ต้องตรงกับบัญชีที่ผูกใน SlipOK LIFF
BEGIN;

WITH ev AS (
  INSERT INTO "Events" ("Name", "Slug", "Year", "EventDate", "RegistrationOpensAt", "RegistrationClosesAt",
                        "PromptPayId", "PromptPayAccountName", "ReservationMinutes", "IsPublished")
  VALUES ('Hueb Run 2027', 'hueb-run-2027', 2027,
          '2027-02-13 22:00:00+00',          -- วันงาน (05:00 เวลาไทย 14 ก.พ.)
          '2026-10-01 00:00:00+00',          -- เปิดรับสมัคร
          '2027-01-31 00:00:00+00',          -- ปิดรับสมัคร
          '0812345678',                      -- เลข PromptPay (เบอร์ 10 หลัก / เลขบัตร 13 หลัก)
          'ชื่อบัญชีผู้รับเงิน',
          30, true)
  RETURNING "Id"
), distances AS (
  INSERT INTO "EventDistances" ("EventId", "Name", "DistanceKm", "BibPrefix", "Quota", "ReservedCount", "LastBibNumber",
                                "Price", "EarlyBirdPrice", "EarlyBirdEndsAt")
  SELECT ev."Id", d.* FROM ev, (VALUES
    ('Mini 3K',           3::numeric,  'A', 300, 0, 0, 450::numeric, 390::numeric, '2026-11-30 00:00:00+00'::timestamptz),
    ('Fun Run 5K',        5,           'B', 400, 0, 0, 550,          490,          '2026-11-30 00:00:00+00'),
    ('Mini Marathon 10K', 10,          'C', 300, 0, 0, 650,          590,          '2026-11-30 00:00:00+00')
  ) AS d
), shirts AS (
  INSERT INTO "EventShirtSizes" ("EventId", "Size", "Detail", "Stock", "ReservedCount")
  SELECT ev."Id", s.* FROM ev, (VALUES
    ('XS', 'รอบอก 34 นิ้ว', 80, 0), ('S', 'รอบอก 36 นิ้ว', 180, 0), ('M', 'รอบอก 38 นิ้ว', 250, 0),
    ('L', 'รอบอก 40 นิ้ว', 250, 0), ('XL', 'รอบอก 42 นิ้ว', 180, 0), ('3XL', 'รอบอก 46 นิ้ว', 80, 0)
  ) AS s
)
INSERT INTO "ShippingOptions" ("EventId", "Type", "Name", "Fee", "IsActive")
SELECT ev."Id", o.* FROM ev, (VALUES
  ('Pickup', 'รับเองหน้างาน', 0::numeric, true),
  ('Post',   'ส่งไปรษณีย์ (EMS)', 60, true)
) AS o;

COMMIT;
