#!/usr/bin/env python3
"""Compare firmware-mined NetSDK routes against the endpoint catalog.

Normalization is shared with nvr-firmware-catalog-append.py: both fold numeric path
segments and {0}-style placeholders to a common token and strip trailing slashes, so
a rerun after appending reports the appended entries as present, not NEW.

Usage:
    python3 scripts/nvr-firmware-surface-diff.py
    python3 scripts/nvr-firmware-surface-diff.py /path/to/camera_routes.txt /path/to/catalog.json
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ROUTES_FILE = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "assets/protocols/firmware-surface/5523w_netsdk_routes.txt"
CATALOG_FILE = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "assets/protocols/endpoint_catalog.json"


def normalize(route: str) -> str:
    """Canonical form: numeric segments and {0}-style placeholders fold to '{}', trailing slash stripped."""
    r = re.sub(r"/\{[^}]*\}", "/{}", route)   # {0}, {n}, {} → {}
    r = re.sub(r"/\d+", "/{}", r)             # /1, /101 → /{}
    return r.rstrip("/")


def main() -> None:
    routes = {line.strip() for line in ROUTES_FILE.read_text().splitlines() if line.strip()}
    catalog = json.loads(CATALOG_FILE.read_text())
    catalog_routes = {e.get("endpoint", "") for e in catalog}

    norm_routes = {normalize(r) for r in routes}
    norm_catalog = {normalize(c) for c in catalog_routes}

    missing = sorted(r for r in routes if normalize(r) not in norm_catalog)
    present = sorted(r for r in routes if normalize(r) in norm_catalog)

    print(f"=== Camera firmware routes: {len(routes)} ===")
    print(f"=== Already in catalog ({len(present)}): ===")
    for r in present:
        print(f"  [in-catalog] {r}")
    print(f"\n=== NEW / NOT in catalog ({len(missing)}): ===")
    for r in missing:
        print(f"  [NEW] {r}")


if __name__ == "__main__":
    main()
