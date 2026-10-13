#!/usr/bin/env bash
# สร้าง rich menu ของ LINE OA ผ่าน Messaging API (กำหนดพิกัดปุ่มเองได้ ไม่ติด template ของ OA Manager)
# ใช้: ./scripts/line-richmenu.sh <รูป.jpg|png> [layout.json] [--replace]
#   layout: scripts/line-richmenu/side.json (ค่าเริ่มต้น: ซ้ายสมัครวิ่ง ขวา 5 ปุ่ม) หรือ stacked.json (บนสมัครวิ่ง 2+3 ปุ่มล่าง)
#   --replace  ลบ rich menu อื่นที่สร้างผ่าน API ไว้ก่อนหน้า หลังตั้งตัวใหม่เป็น default แล้ว
# token: ตั้ง LINE_TOKEN (channel access token เดียวกับ Line__ChannelAccessToken) หรือพิมพ์ตอนรัน
set -euo pipefail
cd "$(dirname "$0")/.."

API=https://api.line.me/v2/bot
DATA_API=https://api-data.line.me/v2/bot
REPLACE=false
ARGS=()
for a in "$@"; do
  if [[ "$a" == "--replace" ]]; then REPLACE=true; else ARGS+=("$a"); fi
done
IMAGE="${ARGS[0]:-}"
JSON="${ARGS[1]:-scripts/line-richmenu/side.json}"

die() { echo "✗ $*" >&2; exit 1; }

[[ -n "$IMAGE" ]] || die "ระบุไฟล์รูป เช่น ./scripts/line-richmenu.sh ~/Desktop/richmenu.jpg"
[[ -f "$IMAGE" ]] || die "ไม่พบรูป: $IMAGE"
[[ -f "$JSON" ]] || die "ไม่พบไฟล์ JSON: $JSON"
command -v jq >/dev/null || die "ต้องมี jq (brew install jq)"
jq empty "$JSON" 2>/dev/null || die "$JSON ไม่ใช่ JSON ที่ถูกต้อง"

case "$(echo "${IMAGE##*.}" | tr '[:upper:]' '[:lower:]')" in
  jpg|jpeg) CONTENT_TYPE=image/jpeg ;;
  png) CONTENT_TYPE=image/png ;;
  *) die "รูปต้องเป็น .jpg หรือ .png" ;;
esac

# ตรวจรูปก่อนส่ง: ไม่เกิน 1MB และขนาดตรงกับ size ใน JSON
BYTES=$(wc -c <"$IMAGE" | tr -d ' ')
(( BYTES <= 1048576 )) || die "รูปใหญ่ $((BYTES / 1024))KB เกิน 1MB — ลดคุณภาพ/บีบอัดก่อน"
command -v sips >/dev/null || die "ต้องมี sips (มีใน macOS) ไว้อ่านขนาดรูป"
W=$(sips -g pixelWidth "$IMAGE" | awk '/pixelWidth/ {print $2}')
H=$(sips -g pixelHeight "$IMAGE" | awk '/pixelHeight/ {print $2}')
# กติกา LINE: กว้าง 800–2500, สูง ≥ 250, กว้าง/สูง ≥ 1.45 (รูปจัตุรัสใช้ไม่ได้)
(( W >= 800 && W <= 2500 && H >= 250 )) || die "รูป ${W}x${H} — LINE รับกว้าง 800–2500px สูง ≥ 250px"
(( W * 100 >= H * 145 )) || die "รูป ${W}x${H} สัดส่วนแคบเกิน — LINE ต้องกว้าง:สูง ≥ 1.45 (เช่น 2500x1686)"

# พิกัดใน JSON เขียนตาม size ใน JSON; ถ้ารูปจริงขนาดต่างแต่สัดส่วนเดียวกัน ย่อ/ขยายพิกัดให้อัตโนมัติ
WANT_W=$(jq -r '.size.width' "$JSON")
WANT_H=$(jq -r '.size.height' "$JSON")
if [[ "$W" != "$WANT_W" || "$H" != "$WANT_H" ]]; then
  (( (W * WANT_H - H * WANT_W) * 100 < W * WANT_H && (H * WANT_W - W * WANT_H) * 100 < W * WANT_H )) \
    || die "รูป ${W}x${H} สัดส่วนไม่ตรงกับ ${WANT_W}x${WANT_H} ใน $JSON — ใช้ layout ผิดไฟล์ หรือ export รูปผิดขนาด"
  SCALED=$(mktemp)
  trap 'rm -f "$SCALED"' EXIT
  jq --argjson w "$W" --argjson h "$H" '
    (.size.width) as $ow | (.size.height) as $oh
    | .size = {width: $w, height: $h}
    | .areas[].bounds |= (
        ((.x * $w / $ow) | round) as $x0 | (((.x + .width) * $w / $ow) | round) as $x1
        | ((.y * $h / $oh) | round) as $y0 | (((.y + .height) * $h / $oh) | round) as $y1
        | {x: $x0, y: $y0, width: ($x1 - $x0), height: ($y1 - $y0)})' "$JSON" >"$SCALED"
  JSON=$SCALED
  echo "✓ ปรับพิกัดจาก ${WANT_W}x${WANT_H} เป็น ${W}x${H}"
fi
echo "✓ รูป ${W}x${H}, $((BYTES / 1024))KB"

if [[ -z "${LINE_TOKEN:-}" ]]; then
  read -rsp "Channel access token: " LINE_TOKEN
  echo
fi
[[ -n "$LINE_TOKEN" ]] || die "ไม่มี token"
AUTH="Authorization: Bearer $LINE_TOKEN"

call() {   # call <method> <url> [curl args...] → พิมพ์ body; HTTP ไม่ใช่ 2xx = return 1
  local method=$1 url=$2; shift 2
  local out code
  out=$(curl -sS -X "$method" "$url" -H "$AUTH" -w $'\n%{http_code}' "$@") || return 1
  code=${out##*$'\n'}
  out=${out%$'\n'*}
  if [[ "$code" != 2* ]]; then
    echo "✗ $method $url → HTTP $code: $out" >&2
    return 1
  fi
  echo "$out"
}

echo "== ตรวจ JSON กับ LINE =="
call POST "$API/richmenu/validate" -H "Content-Type: application/json" --data-binary @"$JSON" >/dev/null
echo "✓ ผ่าน"

echo "== สร้าง rich menu =="
ID=$(call POST "$API/richmenu" -H "Content-Type: application/json" --data-binary @"$JSON" | jq -r '.richMenuId')
[[ -n "$ID" && "$ID" != null ]] || die "ไม่ได้ richMenuId กลับมา"
echo "✓ $ID"

echo "== อัปโหลดรูป =="
if ! call POST "$DATA_API/richmenu/$ID/content" -H "Content-Type: $CONTENT_TYPE" --data-binary @"$IMAGE" >/dev/null; then
  call DELETE "$API/richmenu/$ID" >/dev/null || true   # ไม่ทิ้งเมนูไม่มีรูปค้างไว้
  exit 1
fi
echo "✓ อัปโหลดแล้ว"

echo "== ตั้งเป็นเมนูหลักของทุกคน =="
call POST "$API/user/all/richmenu/$ID" -H "Content-Length: 0" >/dev/null   # LINE ตอบ 411 ถ้าไม่มี Content-Length
echo "✓ ตั้ง default แล้ว"

if $REPLACE; then
  echo "== ลบเมนูเก่า =="
  for old in $(call GET "$API/richmenu/list" | jq -r '.richmenus[].richMenuId'); do
    [[ "$old" == "$ID" ]] && continue
    call DELETE "$API/richmenu/$old" >/dev/null
    echo "  ✓ ลบ $old"
  done
fi

echo
echo "เสร็จ — เปิดแชต OA ในมือถือดู (ถ้ายังไม่เปลี่ยน ปิดแชตแล้วเข้าใหม่ อาจใช้เวลาสักครู่)"
