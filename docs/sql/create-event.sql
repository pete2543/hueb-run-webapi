-- สร้างงานวิ่งใน DB production (ยังไม่มีหน้าแอดมิน)
-- แก้ 2 บรรทัดใน "ตั้งค่า" แล้ววางทั้งไฟล์ใน Railway → Postgres → Data → Query แล้วรัน
-- ผิดรูปแบบหรือมีงาน slug นี้อยู่แล้ว → หยุดพร้อม error ไม่มีอะไรถูกบันทึก
-- เวลาทั้งหมดเป็น UTC (เวลาไทย = UTC+7)

DO $$
DECLARE
  -- ===== ตั้งค่า (แก้แค่ 2 บรรทัดนี้) =====
  promptpay_id   text := 'ใส่เลข PromptPay';         -- เบอร์ 10 หลัก หรือเลขบัตร/ภาษี 13 หลัก ต้องตรงกับบัญชีใน SlipOK LIFF
  account_name   text := 'ใส่ชื่อบัญชีผู้รับเงิน';    -- แสดงให้นักวิ่งเห็นก่อนโอน
  -- ======================================

  event_slug     text := 'hueb-run-2027';
  early_bird_end timestamptz := '2026-11-30 00:00:00+00';   -- 07:00 เวลาไทย 30 พ.ย. 2026
  pp             text := regexp_replace(promptpay_id, '[^0-9]', '', 'g');
  ev_id          int;
BEGIN
  IF NOT (pp ~ '^0[0-9]{9}$' OR pp ~ '^[0-9]{13}$') THEN
    RAISE EXCEPTION 'PromptPay ไม่ถูกต้อง: "%" (ต้องเป็นเบอร์ 10 หลักขึ้นต้นด้วย 0 หรือเลข 13 หลัก)', promptpay_id;
  END IF;
  IF account_name = '' OR account_name LIKE 'ใส่%' THEN
    RAISE EXCEPTION 'ยังไม่ได้ใส่ชื่อบัญชีผู้รับเงิน';
  END IF;
  IF EXISTS (SELECT 1 FROM "Events" WHERE "Slug" = event_slug) THEN
    RAISE EXCEPTION 'มีงาน slug "%" อยู่แล้ว', event_slug;
  END IF;

  INSERT INTO "Events" ("Name", "Slug", "Year", "EventDate", "RegistrationOpensAt", "RegistrationClosesAt",
                        "PromptPayId", "PromptPayAccountName", "ReservationMinutes", "IsPublished")
  VALUES ('Hueb Run 2027', event_slug, 2027,
          '2027-02-13 22:00:00+00',   -- ปล่อยตัว 05:00 เวลาไทย 14 ก.พ. 2027
          '2026-10-01 00:00:00+00',   -- เปิดรับ 07:00 เวลาไทย 1 ต.ค. 2026
          '2027-01-31 00:00:00+00',   -- ปิดรับ 07:00 เวลาไทย 31 ม.ค. 2027
          pp, account_name,
          30,                         -- นาทีที่ให้จ่ายเงินหลังสมัคร
          true)
  RETURNING "Id" INTO ev_id;

  INSERT INTO "EventDistances" ("EventId", "Name", "DistanceKm", "BibPrefix", "Quota", "ReservedCount", "LastBibNumber",
                                "Price", "EarlyBirdPrice", "EarlyBirdEndsAt")
  VALUES (ev_id, 'Mini 3K',           3,  'A', 300, 0, 0, 450, 390, early_bird_end),
         (ev_id, 'Fun Run 5K',        5,  'B', 400, 0, 0, 550, 490, early_bird_end),
         (ev_id, 'Mini Marathon 10K', 10, 'C', 300, 0, 0, 650, 590, early_bird_end);

  INSERT INTO "EventShirtSizes" ("EventId", "Size", "Detail", "Stock", "ReservedCount")
  VALUES (ev_id, 'XS',  'รอบอก 34 นิ้ว', 80,  0),
         (ev_id, 'S',   'รอบอก 36 นิ้ว', 180, 0),
         (ev_id, 'M',   'รอบอก 38 นิ้ว', 250, 0),
         (ev_id, 'L',   'รอบอก 40 นิ้ว', 250, 0),
         (ev_id, 'XL',  'รอบอก 42 นิ้ว', 180, 0),
         (ev_id, '3XL', 'รอบอก 46 นิ้ว', 80,  0);

  INSERT INTO "ShippingOptions" ("EventId", "Type", "Name", "Fee", "IsActive")
  VALUES (ev_id, 'Pickup', 'รับเองหน้างาน',     0,  true),
         (ev_id, 'Post',   'ส่งไปรษณีย์ (EMS)', 60, true);

  RAISE NOTICE 'สร้างงาน "%" แล้ว (Id = %)', event_slug, ev_id;
END $$;
