# ตั้งค่า LINE OA กับ API

API ใช้ LINE 3 อย่าง: **ส่งข้อความ** (Messaging API), **รับเหตุการณ์เพิ่ม/บล็อกเพื่อน** (webhook) และ **ผูก LINE ของนักวิ่งกับออเดอร์** (LINE Login / LIFF) ทั้งหมดไม่ตั้งก็ได้ — ไม่ตั้ง = ระบบทำงานปกติ แค่ข้อความค้างอยู่ในคิว (`Notifications` สถานะ `Pending`) ไม่ส่ง

## ค่าที่ต้องตั้ง (Railway Variables / user-secrets)

| ตัวแปร | มาจาก | ใช้ทำอะไร |
|---|---|---|
| `Line__ChannelAccessToken` | LINE Developers → Messaging API channel → Messaging API → Channel access token (long-lived) | ส่งข้อความ (push) — **ตั้งตัวนี้ worker ถึงเริ่มส่ง** |
| `Line__ChannelSecret` | Messaging API channel → Basic settings → Channel secret | ตรวจลายเซ็น webhook |
| `Line__LoginChannelId` | **LINE Login channel** (คนละ channel กับ Messaging API) → Basic settings → Channel ID | ตรวจ ID token ตอนผูก LINE กับออเดอร์ |
| `Line__TicketUrlBase` | URL หน้าตั๋วของหน้าบ้าน เช่น `https://hueb.run/orders` (ไม่บังคับ) | ใส่ลิงก์ `{base}/{orderId}` ในข้อความ `order_paid` |

อย่าใส่ค่าเหล่านี้ใน `appsettings*.json` หรือ commit ลง repo

## ขั้นตอน

1. **Messaging API channel** (ของ LINE OA): เปิด Messaging API, ออก Channel access token (long-lived), เอา Channel secret
2. **Webhook**: Messaging API → Webhook settings → Webhook URL = `https://<โดเมน API>/api/v1/line/webhook` → เปิด **Use webhook** → กด **Verify** (ต้องได้ Success) → ปิด Auto-reply / Greeting ของ OA ถ้าไม่ต้องการตอบอัตโนมัติซ้อน
3. **LINE Login channel** (สำหรับผูกบัญชี): สร้าง channel ประเภท LINE Login แล้ว**ผูกกับ OA เดียวกัน** (Basic settings → Linked LINE Official Account) และเปิดตัวเลือกให้ขอเป็นเพื่อนตอน login (bot prompt = *aggressive* หรือ *normal*) เพื่อให้นักวิ่งกดเพิ่มเพื่อนระหว่างผูกบัญชี
4. **LIFF** (ฝั่งหน้าบ้าน): สร้าง LIFF app ใน LINE Login channel ให้หน้าบ้านเรียก `liff.init` → `liff.login()` → `liff.getIDToken()` แล้ว `POST /api/v1/orders/{orderId}/line` ด้วย `{ "idToken": "..." }` (scope ต้องมี `openid` และ `profile`)
5. ตั้ง Variables ข้างบนบน Railway แล้ว redeploy

## การทำงาน

- นักวิ่งผูก LINE แล้ว **และ** เป็นเพื่อนกับ OA (webhook `follow`) ถึงจะได้ข้อความ — ผูกอย่างเดียวไม่พอ ระบบข้ามคนที่ยังไม่ได้ add / บล็อกแล้วเงียบ ๆ
- ข้อความอัตโนมัติ: `order_paid` (จ่ายสำเร็จ), `payment_rejected` (สลิปไม่ผ่าน/เจ้าหน้าที่ปฏิเสธ พร้อมเหตุผล), `shipping_sent` (ใส่เลขพัสดุ) — แก้ข้อความได้ที่ตาราง `NotificationTemplates` (placeholder: `{{Name}} {{OrderNo}} {{Total}} {{EventName}} {{ExpiresAt}} {{TicketUrl}} {{Reason}} {{TrackingNo}}`; ปิดข้อความด้วย `IsActive=false`)
- ยังไม่ส่ง: `order_created`, `order_expiring`, `order_expired`, `event_reminder` (มีเทมเพลตแล้ว รอเพิ่มตัวจับเวลา) และแจ้งเตือนตอนคืนเงิน
- ส่งผ่านคิว (outbox): worker ทุก 15 วินาที ลองใหม่ 5 ครั้ง (1, 5, 15, 60, 180 นาที) ข้อความที่ LINE ปฏิเสธถาวร (400/403 เช่น ผู้ใช้บล็อก) ไม่ลองซ้ำ — ดูที่ `Notifications.Status/LastError`
- โควตาข้อความ: push message นับเป็นโควตารายเดือนของแพ็กเกจ OA (แพ็กเกจฟรีจำกัดจำนวน) ประกาศถึงหลายร้อยคนอาจชนโควตา — ตรวจแพ็กเกจก่อนส่งประกาศ
