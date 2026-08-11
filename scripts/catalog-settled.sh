#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# catalog-settled.sh — machine-readable index of live-settled verdicts in the
# NetSDK endpoint catalog.
#
# Reads assets/protocols/endpoint_catalog.json and lists every endpoint entry
# carrying a `settled_verdict*` field (endpoint, canonical key(s), date) so
# SDK-derived tooling can assert it reads the settled spellings instead of
# re-litigating them. Fields are globbed (settled_verdict, settled_verdict_<topic>)
# so an entry with multiple settled topics (e.g. 5.7.6 wireless mode key + the
# wirelessStationDhcp hint) renders one row per verdict.
#
# Usage:
#   ./scripts/catalog-settled.sh                 # table (default)
#   ./scripts/catalog-settled.sh --json          # same data as JSON array
#   ./scripts/catalog-settled.sh --check         # assert: exit 0 iff every verdict
#                                                #   is dated (keys optional — some
#                                                #   verdicts settle facts, not keys)
#   CATALOG=/path/to/endpoint_catalog.json ./scripts/catalog-settled.sh
#
# Exit code: 0 on success; 2 if the catalog is missing, unparseable, or has no
# settled verdicts; 1 in --check mode if any verdict lacks a date.
# ─────────────────────────────────────────────────────────────────────────────
set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CATALOG="${CATALOG:-$SCRIPT_DIR/../assets/protocols/endpoint_catalog.json}"

if [ ! -f "$CATALOG" ]; then
  echo "catalog-settled: catalog not found: $CATALOG" >&2
  exit 2
fi

MODE="table"
case "${1:-}" in
  --json)  MODE="json" ;;
  --check) MODE="check" ;;
  "")
    ;;
  *)
    echo "catalog-settled: unknown arg '$1' (expected --json, --check, or nothing)" >&2
    exit 2
    ;;
esac

# Embedded python does the real work: catalog parse, settled_verdict* globbing,
# canonical-key extraction across all three shapes, and rendering.
python3 - "$CATALOG" "$MODE" <<'PYEOF'
import json
import os
import sys

path, mode = sys.argv[1], sys.argv[2]

try:
    with open(path, encoding="utf-8") as fh:
        catalog = json.load(fh)
except (OSError, json.JSONDecodeError) as exc:
    print(f"catalog-settled: cannot read catalog {path}: {exc}", file=sys.stderr)
    sys.exit(2)

if not isinstance(catalog, list):
    print(f"catalog-settled: {path} is not a JSON array (expected endpoint catalog)", file=sys.stderr)
    sys.exit(2)


def canonical_key(v):
    """Extract the settled spelling from any of the three verdict shapes."""
    if isinstance(v.get("canonical_keys"), list):
        return ", ".join(str(k) for k in v["canonical_keys"])
    for k in ("canonical_rest_key", "key"):
        if v.get(k):
            return str(v[k])
    return None  # verdict with no canonical-key field (e.g. 5.7.2 slot note)


rows = []
for entry in catalog:
    if not isinstance(entry, dict):
        continue
    endpoint = str(entry.get("endpoint") or entry.get("title") or "?")
    for field, verdict in entry.items():
        if not field.startswith("settled_verdict") or not isinstance(verdict, dict):
            continue
        rows.append({
            "endpoint": endpoint,
            "field": field,
            "canonical_key": canonical_key(verdict),
            "date": str(verdict.get("date") or "?"),
            "status": str(verdict.get("status") or "?"),
        })

if not rows:
    print(f"catalog-settled: no settled_verdict* fields in {path}", file=sys.stderr)
    sys.exit(2)

rows.sort(key=lambda r: (r["endpoint"], r["field"]))

if mode == "json":
    print(json.dumps(rows, indent=2))
    sys.exit(0)

if mode == "check":
    # The assertion is: every settled verdict is DATED (traceable). Canonical keys are
    # NOT required — some verdicts (e.g. the 5.7.2 interface-slot note) settle a fact
    # rather than a key spelling and legitimately carry no key field.
    bad = [r for r in rows if r["date"] == "?"]
    if bad:
        for r in bad:
            print(f"  ✗ {r['endpoint']} [{r['field']}] date={r['date']}", file=sys.stderr)
        print(f"catalog-settled: {len(bad)}/{len(rows)} verdict(s) missing date", file=sys.stderr)
        sys.exit(1)
    print(f"catalog-settled: OK — {len(rows)} settled verdict(s), all dated")
    sys.exit(0)

# ── table mode ──────────────────────────────────────────────────────────────
ep_w = max([len(r["endpoint"]) for r in rows] + [len("ENDPOINT")])
key_w = max([len(r["canonical_key"] or "—") for r in rows] + [len("CANONICAL KEY")])
date_w = len("DATE")
hdr = f"{'ENDPOINT':<{ep_w}}  {'CANONICAL KEY':<{key_w}}  {'DATE':<{date_w}}  STATUS"
print(hdr)
print("-" * len(hdr))
for r in rows:
    key = r["canonical_key"] or "—"
    status = r["status"]
    # One-line status: truncate the middle so the table stays scan-able.
    max_s = 70
    if len(status) > max_s:
        status = status[: max_s - 3] + "..."
    print(f"{r['endpoint']:<{ep_w}}  {key:<{key_w}}  {r['date']:<{date_w}}  {status}")
print()
print(f"{len(rows)} settled verdict(s) across {len({r['endpoint'] for r in rows})} endpoint(s)")
print("Catalog: " + os.path.abspath(path))
PYEOF
