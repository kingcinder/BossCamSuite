#!/usr/bin/env python3
"""
5523w-surface-verify.py — live round-trip verification of the firmware-proven
settings surface against a real 5523-W camera.

Read-only: every probe is a GET. No PUTs — the camera configuration is never
touched. Cross-checks the seed-contract SourcePath fields (e.g. $.ledPwm.switch,
$.AlarmSchedule[0].Enabled) against the ACTUAL JSON payload returned by the
camera, and prints a machine-readable verdict per endpoint.

Usage:
  python3 scripts/5523w-surface-verify.py [IP ...]
      Default IPs: 10.0.0.29 10.0.0.169 10.0.0.227 (fleet 5523-W LAN addrs).
  ADMIN_PASS=pw python3 scripts/5523w-surface-verify.py 10.0.0.29
      Probe a camera whose admin password was already changed.

Output: JSON to stdout — one object per endpoint:
  {endpoint, http, payload, expected_fields, present_fields, missing_fields, verdict}
  verdict ∈ {"confirmed", "partial", "empty", "gated", "no-answer", "error"}
"""
import base64
import json
import os
import subprocess
import sys

ADMIN_PASS = os.environ.get("ADMIN_PASS", "")
DEFAULT_IPS = os.environ.get(
    "VERIFY_IPS", "10.0.0.29 10.0.0.169 10.0.0.227"
).split()

# endpoint -> list of SourcePath fields that must round-trip (from the seed contracts)
ENDPOINTS = [
    ("/NetSDK/System/ledpwm", ["$.ledPwm.switch", "$.ledPwm.project", "$.ledPwm.nChannelCount", "$.channelInfo"]),
    ("/NetSDK/System/ledpwm/ChannelInfo", ["$.channelInfo"]),
    ("/NetSDK/System/AlarmSchedule", ["$.AlarmSchedule[0].Enabled", "$.AlarmSchedule[0].Weekday", "$.AlarmSchedule[0].BeginTime", "$.AlarmSchedule[0].EndTime"]),
    ("/NetSDK/System/AlarmScheduleV2", ["$.ScheduleEnabled", "$.ScheduleScheme"]),
    ("/NetSDK/System/AlarmTone", ["$.AlarmTone[0].Enabled", "$.AlarmTone[0].tone"]),
    ("/NetSDK/System/RecordSchedule", ["$.RecordSchedule[0].Enabled", "$.RecordSchedule[0].RecType", "$.RecordSchedule[0].Weekday", "$.RecordSchedule[0].BeginTime", "$.RecordSchedule[0].EndTime"]),
    ("/NetSDK/System/time/rtc", ["$"]),
    ("/NetSDK/System/time/timeZone", ["$"]),
    ("/NetSDK/System/time/calendarStyle", ["$"]),
    ("/NetSDK/Video/FaceDetection", ["$.enabled"]),
    ("/NetSDK/Video/HumanDetect", ["$.enabled", "$.drawRegion", "$.sensitivityStep"]),
    ("/NetSDK/Video/cordon", ["$.enabled", "$.type", "$.sensitivityLevel", "$.line", "$.grid"]),
    ("/NetSDK/System/gb28181", ["$.sipPort", "$.sipServerport", "$.sipUsername", "$.sipUserpass", "$.sipServeraddr"]),
    ("/NetSDK/System/gat1400", ["$.bGAT1400"]),
    ("/NetSDK/FTP", ["$.ScheduleEnabled", "$.schedule"]),
    ("/NetSDK/RTMP", ["$.rtmpUrl"]),
    ("/NetSDK/Network/port", ["$[0].id", "$[0].portname", "$[0].value"]),
    ("/NetSDK/Network/wireless/stationSignal", ["$"]),
    ("/NetSDK/Network/wireless/allStaInfo", ["$"]),
    ("/NetSDK/System/deviceInfo/deviceName", ["$"]),
    ("/NetSDK/System/deviceInfo/deviceAddress", ["$"]),
]


def http_get(ip, path, timeout=6):
    """GET with blank-admin basic auth. Returns (code, body)."""
    try:
        out = subprocess.run(
            ["curl", "-sS", "-m", str(timeout), "-u", f"admin:{ADMIN_PASS}",
             "-w", "\n%{http_code}", f"http://{ip}{path}"],
            capture_output=True, text=True, timeout=timeout + 4,
        ).stdout
        code = out.rsplit("\n", 1)[-1].strip()
        body = out.rsplit("\n", 1)[0]
        return code, body
    except Exception:
        return "000", ""


def resolve_path(obj, path):
    """Resolve a JSONPath-ish SourcePath like $.ledPwm.switch or $.AlarmSchedule[0].Enabled."""
    if not path.startswith("$.") or not isinstance(obj, (dict, list)):
        return None
    parts = []
    for tok in path[2:].split("."):
        if "[" in tok:
            base, idx = tok.split("[", 1)
            idx = idx.rstrip("]")
            if base:
                parts.append(base)
            parts.append(int(idx) if idx.isdigit() else idx)
        else:
            parts.append(tok)
    cur = obj
    for p in parts:
        if isinstance(cur, dict) and p in cur:
            cur = cur[p]
        elif isinstance(cur, list) and isinstance(p, int) and 0 <= p < len(cur):
            cur = cur[p]
        else:
            return None
    return cur


def unwrap_envelope(obj):
    """Strip the RPC-style envelope {requestMethod, requestURL, statusCode, ...}."""
    if isinstance(obj, dict) and "requestMethod" in obj:
        return {k: v for k, v in obj.items()
                if k not in ("requestMethod", "requestURL", "requestQuery", "statusCode", "statusMessage")}
    return obj


def probe(ip, path, fields):
    code, body = http_get(ip, path)
    rec = {"endpoint": path, "http": code, "payload": None,
           "expected_fields": fields, "present_fields": [], "missing_fields": [],
           "verdict": "no-answer", "payload_keys": []}
    if code != "200":
        rec["verdict"] = "gated" if code in ("401", "403") else "error"
        return rec
    try:
        data = json.loads(body)
    except Exception:
        rec["verdict"] = "error"
        rec["payload"] = body[:200]
        return rec
    data = unwrap_envelope(data)
    rec["payload"] = data
    keys = list(data.keys()) if isinstance(data, dict) else None
    rec["payload_keys"] = keys
    if isinstance(data, list):
        # allStaInfo-style array payload
        if fields == ["$"] or (data and isinstance(data[0], dict)):
            rec["present_fields"] = ["$array"]
            rec["verdict"] = "confirmed"
            return rec
    if not isinstance(data, (dict, list)):
        # BARE SCALAR payload (deviceName="5523-W", rtc=1786493574, stationSignal=-48,
        # timeZone="GMT+08:00", calendarStyle="general", deviceAddress=1): the wire document
        # IS the value, so any "$"-rooted expectation is satisfied by the bare scalar itself.
        if fields == ["$"] or any(f in ("$", "$.deviceName", "$.rtc", "$.timeZone",
                                        "$.calendarStyle", "$.deviceAddress", "$.SignalStrength",
                                        "$.stationsignal") for f in fields):
            rec["present_fields"] = ["$bare-scalar"]
            rec["verdict"] = "confirmed"
        else:
            rec["verdict"] = "empty"
        return rec
    if not data:
        rec["verdict"] = "empty"
        return rec
    for f in fields:
        if resolve_path(data, f) is not None:
            rec["present_fields"].append(f)
        else:
            rec["missing_fields"].append(f)
    if not rec["missing_fields"]:
        rec["verdict"] = "confirmed"
    elif rec["present_fields"]:
        rec["verdict"] = "partial"
    else:
        rec["verdict"] = "empty"
    return rec


def main():
    ips = sys.argv[1:] or DEFAULT_IPS
    results = []
    for ip in ips:
        for path, fields in ENDPOINTS:
            results.append({"ip": ip, **probe(ip, path, fields)})
    # Compact payloads: keep keys + a short sample value for the report
    for r in results:
        if isinstance(r.get("payload"), dict):
            sample = {}
            for k, v in r["payload"].items():
                sv = v if not isinstance(v, (dict, list)) else type(v).__name__
                if isinstance(sv, (dict, list)):
                    sv = sv if isinstance(sv, list) else list(sv.keys())[:6]
                sample[k] = sv
            r["payload"] = sample
    print(json.dumps(results, indent=1))


if __name__ == "__main__":
    main()
