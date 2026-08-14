#!/usr/bin/env python3
"""Append firmware-revealed 5523-W NetSDK routes (new vs catalog) to endpoint_catalog.json.

Discovery source: FWHI102 NVR firmware cross-reference (2026-08-13). Route strings and
$.schema descriptors mined verbatim from anyka_ipc (IPCAKV3C 3.6.60 / IPG5322_W 3.6.58)
and the NVR app.out. Provenance tag: "firmware-string-2026-08-13".

Endpoint convention: concrete path forms as found in the firmware string table
(trailing slash for parameterized leaves, e.g. /NetSDK/PTZ/channel/) — same style as
the pre-existing 140 catalog entries. Idempotent: entries already present (under
normalization) are skipped.
"""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CATALOG = ROOT / "assets/protocols/endpoint_catalog.json"

PROVENANCE = ("Mined verbatim from anyka_ipc route strings (IPCAKV3C_20211221_3_6_60_572010, "
              "IPG5322_W_20211210_3_6_58_572011) cross-referenced with the FWHI102 NVR app.out "
              "BCAM/N1 control plane (2026-08-13). Not yet live-verified on 5523-W; see "
              "docs/reports/2026-08-13-nvr-firmware-bcam-surface-revelation.md")

# (endpoint, tag, methods, key fields from $.schema descriptors)
NEW_ENTRIES = [
    # Bare parent routes — included so a fresh run reproduces the exact catalog; the
    # diff treats them as present once any child is cataloged.
    ("/NetSDK/Image", "Image", ["GET"], "Image control root"),
    ("/NetSDK/Network/interface/", "Network", ["GET", "PUT"],
     "$.Interface.{dhcp,macAddress,ipAddress,netmask,gateway} (lan/pppoe/wireless variants)"),
    ("/NetSDK/ProductionResult", "System", ["GET"], "Production result root (CGI ProductionResult)"),
    ("/NetSDK/System/operation", "System", ["GET"], "Operation root"),
    ("/NetSDK/System/time", "System", ["GET"], "Time root"),
    ("/NetSDK/Audio/encode/channel/", "Audio", ["GET", "PUT"],
     "$.audioEncodeChannel[%d].{id,codecType}"),
    ("/NetSDK/Audio/input/channel/", "Audio", ["GET", "PUT"],
     "$.audioId; sp* audio IO surface (audioEncodeChannel, AudioInCodec/AudioOutCodec)"),
    ("/NetSDK/Factory", "System", ["GET", "POST"], "Factory reset / factory-test entry (CGI OnFactory)"),
    ("/NetSDK/Image/denoise3d", "Image", ["GET", "PUT"], "$.denoise3d.{enabled,denoise3dStrength}"),
    ("/NetSDK/Image/irCutFilter", "Image", ["GET", "PUT"],
     "$.irCutFilter.{irCutControlMode,irCutMode,softwareIrOption.{dayToNightLevel,nightToDayLevel}}"),
    ("/NetSDK/Image/manualSharpness", "Image", ["GET", "PUT"], "$.manualSharpness.{enabled,sharpnessLevel}"),
    ("/NetSDK/Image/videoMode", "Image", ["GET", "PUT"],
     "$.videoMode.{cellMode,fixMode,tableMode,wallMode,FixParam[%d].{id,AngleX,AngleY,AngleZ,CenterCoordinateX,CenterCoordinateY,Radius}}"),
    ("/NetSDK/Image/wdr", "Image", ["GET", "PUT"], "$.WDR.{enabled,WDRStrength}"),
    ("/NetSDK/Network/DNS", "Network", ["GET", "PUT"], "$.Primary_DNS/$.Secondary_DNS (dialog strings); $.Dns"),
    ("/NetSDK/Network/ESee", "Network", ["GET", "PUT"], "$.eseeID, $.Esee, $.Stat.Network.P2P*"),
    ("/NetSDK/PTZ/channel/", "PTZ", ["GET", "PUT", "POST"],
     "$.angles.{anglesX,anglesY,anglesZ}; spPtz{Horizontal,Vertical}*; RESTful_NetSDKPTZChannelIDSetup_OnGET/PUT"),
    ("/NetSDK/Production/SetEncryptionChip", "System", ["POST"], "Production-only: set encryption chip"),
    ("/NetSDK/ProductionResult/FinishedOldTest", "System", ["POST"], "Production: finish old test"),
    ("/NetSDK/ProductionResult/FinishedTest", "System", ["POST"], "Production: finish test"),
    ("/NetSDK/ProductionResult/ImageTest", "System", ["POST"], "Production: image test"),
    ("/NetSDK/ProductionResult/SFPTest", "System", ["POST"], "Production: SFP test"),
    ("/NetSDK/ProductionResult/WIFITest", "System", ["POST"], "Production: WiFi test"),
    ("/NetSDK/RangeUpload/Firmware", "System", ["POST"], "Range/fleet firmware upload (OTA)"),
    ("/NetSDK/RangeUpload/UploadPT", "System", ["POST"], "Range upload: PT (PTZ?) data"),
    ("/NetSDK/System/mdAlarm/motionWarningTone", "System", ["GET", "PUT"],
     "$.MotionWarningTone / $.AlarmSetting.MotionDetection.MotionWarningTone"),
    ("/NetSDK/System/module/bluetooth", "System", ["GET", "PUT"], "N1Device_Bluetooth; bluetooth module state"),
    ("/NetSDK/System/module/gsensor", "System", ["GET", "PUT"], "N1Device_EvtVRCamGetGensorCalibration / SetGensorEnabled"),
    ("/NetSDK/System/module/gsensor/calibration", "System", ["GET", "PUT"], "$.calibration.{enabled}; gsensor calibration"),
    ("/NetSDK/System/operation/default", "System", ["GET", "PUT"], "Factory-default operation"),
    ("/NetSDK/System/operation/reboot", "System", ["POST"], "Reboot device"),
    ("/NetSDK/System/operation/remoteUpgrade", "System", ["POST"], "Remote firmware upgrade"),
    ("/NetSDK/System/time/ntp", "System", ["GET", "PUT"], "$.ntp.{ntpEnabled,ntpServerDomain}"),
    ("/NetSDK/Video/encode/channel/", "Video", ["GET", "PUT"],
     "$.videoEncodeChannel[%d].{id,resolution}; channel 101/102"),
    ("/NetSDK/Video/encode/channel/101/snapshot", "Video", ["GET"], "Main-stream snapshot (JPEG)"),
    ("/NetSDK/Video/encode/channel/102/snapshot", "Video", ["GET"], "Sub-stream snapshot (JPEG)"),
    ("/NetSDK/Video/input/channel/", "Video", ["GET", "PUT"],
     "Image-plane root per channel (properties, privacyMasks, brightnessLevel, ...)"),
    ("/NetSDK/Video/motionDetection/channel/", "Video", ["GET", "PUT"],
     "Motion detection config per channel (sensitivity grid, regions, schedules)"),
]


def normalize(route: str) -> str:
    """Canonical form — must stay in sync with nvr-firmware-surface-diff.py."""
    r = re.sub(r"/\{[^}]*\}", "/{}", route)   # {0}, {n}, {} → {}
    r = re.sub(r"/\d+", "/{}", r)             # /1, /101 → /{}
    return r.rstrip("/")


def main() -> None:
    catalog = json.loads(CATALOG.read_text())
    existing_norm = {normalize(e["endpoint"]) for e in catalog}

    added = 0
    for endpoint, tag, methods, fields in NEW_ENTRIES:
        if normalize(endpoint) in existing_norm:
            print(f"skip (exists): {endpoint}")
            continue
        leaf = endpoint.rstrip("/").rsplit("/", 1)[-1]
        entry = {
            "title": f"{tag} {leaf} — firmware-revealed (NVR cross-ref 2026-08-13)",
            "tag": tag,
            "endpoint": endpoint,
            "methods": methods,
            "details": {m: {"description": f"{m} {endpoint}", "query": "None.", "content": "None.",
                            "success_return": "See $.schema descriptors in assets/protocols/firmware-surface/5523w_schema_descriptors.txt",
                            "notes": fields} for m in methods},
            "inference_flags": {"tag_inferred": True, "operation_ids_inferred": True,
                                "request_response_schema_links_partly_inferred": True},
            "provenance": PROVENANCE,
        }
        catalog.append(entry)
        existing_norm.add(normalize(endpoint))
        added += 1

    CATALOG.write_text(json.dumps(catalog, indent=2, ensure_ascii=False) + "\n")
    print(f"added {added} entries; catalog now {len(catalog)} entries")


if __name__ == "__main__":
    main()
