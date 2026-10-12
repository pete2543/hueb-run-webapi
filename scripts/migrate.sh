#!/bin/sh
# รัน migration ก่อน deploy (Railway preDeployCommand) — ใช้ connection string เดียวกับแอป
set -eu
: "${ConnectionStrings__DefaultConnection:?ตั้ง ConnectionStrings__DefaultConnection ก่อน}"
cd "$(dirname "$0")"
exec ./efbundle --connection "$ConnectionStrings__DefaultConnection"
