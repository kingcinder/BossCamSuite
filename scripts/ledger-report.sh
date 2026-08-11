#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# ledger-report.sh — per-camera audit table from the campaign JSONL ledger.
#
# Reads local-camera-recovery/ledger/*.jsonl (one line = one run event, one
# file per camera, serial normalized to the canonical JAZ7C34… form) and
# renders a per-camera table: serial, last ts, latest IP, verdict, sta_dhcp.
#
# The ledger is appended by scripts/5523w-wifi-reprovision.sh and
# scripts/5523w-interface4-keyprobe.sh on every re-provision/keyprobe run, so
# this is the at-a-glance audit trail for the whole recovery campaign — the
# companion to docs/reports/2026-08-10-rest-keyprobe-verdict.md §3.
#
# Usage:
#   ./scripts/ledger-report.sh              # table (default)
#   ./scripts/ledger-report.sh --json       # same data as JSON array
#   LEDGER_DIR=/path/to/ledger ./scripts/ledger-report.sh
#
# Exit code: 0 on success; 2 if the ledger dir is missing or empty.
# ─────────────────────────────────────────────────────────────────────────────
set -u

LEDGER_DIR="${LEDGER_DIR:-local-camera-recovery/ledger}"

if [ ! -d "$LEDGER_DIR" ] || ! compgen -G "$LEDGER_DIR/*.jsonl" > /dev/null 2>&1; then
  echo "ledger-report: no ledger files in $LEDGER_DIR (directory missing or empty)" >&2
  exit 2
fi

MODE="table"
if [ "${1:-}" = "--json" ]; then
  MODE="json"
elif [ $# -gt 0 ]; then
  echo "ledger-report: unknown arg '$1' (expected --json or nothing)" >&2
  exit 2
fi

# Embedded python does the real work: JSONL parsing, per-camera grouping,
# and rendering. Output goes to stdout verbatim.
python3 - "$LEDGER_DIR" "$MODE" <<'PYEOF'
import json
import os
import sys

ledger_dir, mode = sys.argv[1], sys.argv[2]

# camera -> list of (ts, entry); entries keep raw JSON for --json mode.
cameras = {}
for name in sorted(os.listdir(ledger_dir)):
    if not name.endswith(".jsonl"):
        continue
    serial = name[:-len(".jsonl")]
    path = os.path.join(ledger_dir, name)
    entries = []
    with open(path, "r", encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                entries.append(json.loads(line))
            except json.JSONDecodeError as exc:
                # No ts/ip keys on purpose: last() skips entries without them,
                # so a broken line can't shadow the real last ts/ip columns —
                # the parse-error verdict + RUNS count keep the corruption visible.
                entries.append({"__broken__": line,
                                "keyprobe_verdict": f"JSONL parse error: {exc}",
                                "sta_dhcp": None})
    if entries:
        cameras[serial] = entries

if not cameras:
    print(f"ledger-report: no parseable ledger files in {ledger_dir}", file=sys.stderr)
    sys.exit(2)


def sort_key(e):
    # Broken entries sort LAST so last() surfaces their parse-error verdict,
    # while their keyless ts/ip let last() skip them for the real columns.
    return (1 if "__broken__" in e else 0, str(e.get("ts", "")))


def last(entries, key, default="?"):
    for e in reversed(entries):
        if key in e and e[key] is not None:
            return e[key]
    return default


def flag(e):
    v = e.get("sta_dhcp")
    if v is None:
        return "—"
    return str(v).lower()


def last_valid_flag(entries):
    # Footer must reflect the last VALID run — a trailing broken line would
    # otherwise shadow a real sta_dhcp with "—".
    for e in reversed(entries):
        if "__broken__" not in e:
            return flag(e)
    return "—"


if mode == "json":
    out = []
    for serial in sorted(cameras):
        entries = sorted(cameras[serial], key=sort_key)
        out.append({
            "serial": serial,
            "last_ts": str(last(entries, "ts")),
            "latest_ip": str(last(entries, "ip")),
            "verdict": str(last(entries, "keyprobe_verdict")),
            # Preserve the raw null (sta_dhcp never recorded) vs a real value.
            "sta_dhcp": last(entries, "sta_dhcp", default=None),
            "runs": len(entries),
            "broken_lines": sum(1 for e in entries if "__broken__" in e),
        })
    print(json.dumps(out, indent=2))
    sys.exit(0)

# ── table mode ──────────────────────────────────────────────────────────────
serial_w = max([len(s) for s in cameras] + [len("SERIAL")])
ts_w = max([len(str(last(c, "ts"))) for c in cameras.values()] + [len("LAST TS")])
ip_w = max([len(str(last(c, "ip"))) for c in cameras.values()] + [len("LATEST IP")])
runs_w = max([len(str(len(c))) for c in cameras.values()] + [len("RUNS")])

hdr = f"{'SERIAL':<{serial_w}}  {'LAST TS':<{ts_w}}  {'LATEST IP':<{ip_w}}  {'RUNS':<{runs_w}}  VERDICT"
print(hdr)
print("-" * len(hdr))
for serial in sorted(cameras):
    entries = sorted(cameras[serial], key=sort_key)
    verdict = str(last(entries, "keyprobe_verdict"))
    # One-line verdict: truncate the middle so the table stays scan-able.
    max_v = 78
    if len(verdict) > max_v:
        verdict = verdict[: max_v - 3] + "..."
    print(f"{serial:<{serial_w}}  {last(entries, 'ts'):<{ts_w}}  {last(entries, 'ip'):<{ip_w}}  {len(entries):<{runs_w}}  {verdict}")

print()
print("sta_dhcp (last run per camera): " + ", ".join(
    f"{s}={last_valid_flag(sorted(cameras[s], key=sort_key))}" for s in sorted(cameras)
))
print("Ledger dir: " + os.path.abspath(ledger_dir))
PYEOF
