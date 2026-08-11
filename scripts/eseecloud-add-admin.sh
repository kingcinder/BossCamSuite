#!/usr/bin/env bash
# ── eseecloud-add-admin.sh — create/reset admin creds once the gate opens ──
#
# Once the camera's cloud check-in succeeds ($.Auth.ticket set), the
# /user/*.xml endpoints stop returning "check in falied". This script tries
# the plausible request shapes for adding a user (add_user.xml) and for the
# documented password reset (user_reset) used by BossCam, then verifies the
# new credential against /NetSDK/System/deviceInfo.
#
# Usage:
#   ./scripts/eseecloud-add-admin.sh <camera-ip> [new-user] [new-pass]
#   NEW_USER=admin2 NEW_PASS='BossCam2026!' ./scripts/eseecloud-add-admin.sh 10.0.0.29

set -u

IP="${1:?usage: $0 <camera-ip> [new-user] [new-pass]}"
NEW_USER="${NEW_USER:-${2:-operator}}"
NEW_PASS="${NEW_PASS:-${3:-BossCam2026!}}"

echo "═══ add/reset admin on $IP (user=$NEW_USER) ═══"

gate=$(curl -s -m 5 "http://$IP/user/user_list.xml" | tr -d '\r')
if echo "$gate" | grep -q "check in falied"; then
  echo "!! Gate still closed: $gate"
  exit 1
fi
echo "  gate open: $gate"

# 1) add_user.xml — try query-string, form body, and XML body shapes
echo "── add_user.xml shapes ──"
echo "  [1] query string:"
curl -s -m 5 -X POST \
  "http://$IP/user/add_user.xml?userName=$NEW_USER&password=$NEW_PASS&level=2"; echo
echo "  [2] form body:"
curl -s -m 5 -X POST "http://$IP/user/add_user.xml" \
  -d "userName=$NEW_USER&password=$NEW_PASS&level=2"; echo
echo "  [3] XML body:"
curl -s -m 5 -X POST "http://$IP/user/add_user.xml" \
  -H 'Content-Type: application/xml' \
  -d "<?xml version=\"1.0\" encoding=\"UTF-8\"?><User><userName>$NEW_USER</userName><password>$NEW_PASS</password><level>2</level></User>"; echo

# 2) user_reset — the endpoint BossCam's PasswordReset maintenance uses
echo "── user_reset shapes ──"
echo "  [4] query string (reset admin):"
curl -s -m 5 -X POST \
  "http://$IP/user/user_reset?userName=admin&password=$NEW_PASS"; echo
echo "  [5] form body (reset admin):"
curl -s -m 5 -X POST "http://$IP/user/user_reset" \
  -d "userName=admin&password=$NEW_PASS"; echo
echo "  [6] XML body (reset admin):"
curl -s -m 5 -X POST "http://$IP/user/user_reset" \
  -H 'Content-Type: application/xml' \
  -d "<?xml version=\"1.0\" encoding=\"UTF-8\"?><User><userName>admin</userName><password>$NEW_PASS</password></User>"; echo

# 3) verify any newly-set credentials against /NetSDK
echo "── verification ──"
for cred in "admin:$NEW_PASS" "$NEW_USER:$NEW_PASS" "admin:"; do
  code=$(curl -s -o /dev/null -w '%{http_code}' -m 5 -u "$cred" \
    "http://$IP/NetSDK/System/deviceInfo")
  echo "  /NetSDK/System/deviceInfo with $cred -> HTTP $code"
done
