#!/usr/bin/env bash
# ── eseecloud-gate-probe.sh — watch the /user/*.xml gate during the MITM ──
#
# The camera refuses local user management ("check in falied") until a cloud
# check-in succeeds and $.Auth.ticket is set. This probe polls
# /user/user_list.xml every few seconds and logs the gate state, so we can
# see exactly when (and with which fake /message/message reply candidate)
# the gate flips open.
#
# Usage:
#   ./scripts/eseecloud-gate-probe.sh [duration_seconds] [camera1 camera2 ...]
#
# Env:  CAMS=...  (space-separated camera IPs, overrides positional args)

set -u

DURATION="${1:-480}"
CAMS="${CAMS:-${2:-10.0.0.29 10.0.0.169}}"
INTERVAL=2

echo "═══ gate probe — ${DURATION}s, cameras: ${CAMS} ═══"
END=$((SECONDS + DURATION))
while [ "$SECONDS" -lt "$END" ]; do
  for ip in $CAMS; do
    body=$(curl -s -m 4 "http://${ip}/user/user_list.xml" | tr -d '\r')
    ts=$(date -u '+%H:%M:%S')
    if echo "$body" | grep -q "check in falied"; then
      echo "[$ts] $ip GATED: $body"
    elif [ -z "$body" ]; then
      echo "[$ts] $ip NO-RESPONSE"
    else
      echo "[$ts] $ip ★★★ GATE OPEN ★★★: $body"
    fi
  done
  sleep "$INTERVAL"
done
echo "═══ probe complete ═══"
