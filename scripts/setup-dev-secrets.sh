#!/usr/bin/env bash
# ตั้งค่าลับสำหรับรัน API บนเครื่อง dev (เก็บใน dotnet user-secrets นอก repo ไม่ติด git)
# ใช้: ./scripts/setup-dev-secrets.sh   — กด Enter เพื่อใช้ค่าในวงเล็บ / เว้นว่างเพื่อข้ามค่านั้น
set -euo pipefail
cd "$(dirname "$0")/.."
PROJECT=src/HuebRun.Api

ask() {   # ask <prompt> <default> → echo value
  local value
  read -rp "$1 [$2]: " value
  echo "${value:-$2}"
}

ask_secret() {   # ask_secret <prompt> → echo value (ไม่แสดงบนจอ)
  local value
  read -rsp "$1: " value
  echo >&2
  echo "$value"
}

set_secret() {   # set_secret <key> <value>; ค่าว่าง = ข้าม
  if [[ -n "$2" ]]; then
    dotnet user-secrets set "$1" "$2" --project "$PROJECT" >/dev/null
    echo "  ✓ $1"
  else
    echo "  - $1 (ข้าม)"
  fi
}

echo "== PostgreSQL =="
host=$(ask "Host" "192.168.155.203")
port=$(ask "Port" "5432")
db=$(ask "Database" "rundb")
user=$(ask "Username" "postgres")
password=$(ask_secret "Password")
set_secret "ConnectionStrings:DefaultConnection" "Host=$host;Port=$port;Database=$db;Username=$user;Password=$password"

echo
echo "== SlipOK / MinIO (เว้นว่างได้ถ้าใช้โหมดจำลอง Payments:Mode=Fake) =="
set_secret "SlipOk:BranchId" "$(ask "SlipOK Branch ID" "")"
set_secret "SlipOk:ApiKey" "$(ask_secret "SlipOK API key")"
set_secret "Minio:AccessKey" "$(ask_secret "MinIO access key")"
set_secret "Minio:SecretKey" "$(ask_secret "MinIO secret key")"

echo
echo "เสร็จแล้ว ต่อไป: dotnet ef database update -p src/HuebRun.Infrastructure -s src/HuebRun.Api"
