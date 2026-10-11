# ดีไซน์ UI (สำเนาจาก Design canvas)

ต้นฉบับ: https://claude.ai/artifact/X4MjVjZGTMWmEBCJ2LZNm6 (Fun Run Registration Demo)
ดึงมาเมื่อ 2026-10-09 ถ้าแก้ดีไซน์ในแคนวาส ให้ดึงไฟล์มาใหม่ ไฟล์ในโฟลเดอร์นี้ไม่ได้ซิงก์อัตโนมัติ

ไฟล์ `.dc.html` เป็นซอร์สของแต่ละหน้าจอในแคนวาส ใช้เป็นต้นแบบ layout, ข้อความ และสี ตอนทำหน้าจริงด้วย Next.js
เปิดตรง ๆ ในเบราว์เซอร์จะแสดงผลไม่ครบ เพราะต้องใช้ runtime ของแคนวาส (`support.js`) ส่วน `canvas.json` เก็บลำดับและตำแหน่งของแต่ละหน้าจอ

## Runner flow (มือถือ 390px)

| # | ไฟล์ | หน้าจอ |
|---|---|---|
| 1 | [Main.dc.html](Main.dc.html) | หน้าแรก (3D hero) |
| 2 | [Distances.dc.html](Distances.dc.html) | เลือกระยะ |
| 3 | [Runners.dc.html](Runners.dc.html) | ข้อมูลนักวิ่ง |
| 4 | [Summary.dc.html](Summary.dc.html) | สรุป ส่วนลด จัดส่ง |
| 5 | [Payment.dc.html](Payment.dc.html) | ชำระเงิน PromptPay |
| 6 | [Success.dc.html](Success.dc.html) | สำเร็จ + QR เช็กอิน |
| 7 | [LineChat.dc.html](LineChat.dc.html) | แจ้งเตือนทาง LINE OA |

## Admin (เดสก์ท็อป 1280px)

| # | ไฟล์ | หน้าจอ |
|---|---|---|
| 8 | [Dashboard.dc.html](Dashboard.dc.html) | แดชบอร์ด |
| 9 | [Orders.dc.html](Orders.dc.html) | ใบสมัคร |
| 10 | [Admin.dc.html](Admin.dc.html) | ตรวจสลิป |
| 11 | [AdminRunners.dc.html](AdminRunners.dc.html) | นักวิ่ง & เลขบิบ |
| 12 | [Quota.dc.html](Quota.dc.html) | ระยะ & โควตา |
| 13 | [Promo.dc.html](Promo.dc.html) | โปรโมชัน |
| 14 | [Checkin.dc.html](Checkin.dc.html) | เช็กอิน / รับของ |
| 15 | [Broadcast.dc.html](Broadcast.dc.html) | ประกาศ LINE |
| 16 | [Refunds.dc.html](Refunds.dc.html) | คืนเงิน |
| 17 | [Users.dc.html](Users.dc.html) | ผู้ใช้ & สิทธิ์ |
| 18 | [Audit.dc.html](Audit.dc.html) | Audit log |

Design tokens (สี, ฟอนต์) สรุปไว้ใน [../ui-flow.md](../ui-flow.md)
