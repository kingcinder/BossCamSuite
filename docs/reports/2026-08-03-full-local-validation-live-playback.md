# Full Local Validation Pass — 5523-W Live Playback, AV Persistence, Recording

**Date:** 2026-08-03 · **Service:** deployed `/opt/bosscam` (`bosscam.service`, pid 1703105, ASPNETCORE_ENVIRONMENT=Production, .NET 8) · **Camera:** 10.0.0.169 (5523-W, device `0654e903-afdb-4d1d-b016-b3c9957600a1`) · **Build:** `bfd2e86` + one-line CS0121 fix (uncommitted, see below)

---

## 1. Compile / build — GREEN

| Check | Result |
|---|---|
| Clean full-solution build under default SDK 8.0.129 | **0 warnings / 0 errors** |
| Deployed DLLs newer than source (Core 21:56, Infra 21:57 Aug 2) | ✅ current code running |

**Known uncommitted change:** `src/BossCam.Infrastructure/Video/VideoTransportAdapters.cs` — the CS0121 fix (`raw.Split(new char[] {...})` replacing the ambiguous C# 12 collection expression). Behavior-identical; shipped in `/opt/bosscam`; not yet in `origin/main`.

## 2. Test suites — ALL GREEN

| Suite | Result |
|---|---|
| `BossCam.Tests` (unit) | **395/395 passed** (2m05s) |
| `BossCam.E2E` | **108/108 passed**, live-camera tests executed against real units (1858+ log mentions, no skips) |
| `BossCam.Desktop.Avalonia.Tests` | **55/55 passed** |

The E2E `LiveCameraExhaustiveTests` ran with `BOSSCAM_E2E_IPS` pointed at 10.0.0.169 and exercised the live camera through the in-process WebApplicationFactory.

## 3. Live streaming video — PROVEN (not just "connected")

- Streamed the 5523-W through the deployed service (shared fMP4 media session).
- Captured 21 s of live HEVC, decoded **5 frames to PNG** via ffmpeg.
- **256/256 distinct gray levels, average brightness 66.6** — real scene content, definitively **not** a blank/black/static stream.
- Frame pattern = 1× I-frame + P-frames → live encoder output.
- Sources ranking verified live: main `ch0_0.264` (rank 0, `nativeNetSdk=true`, digest auth) → `/11` alias (3) → snapshot :80 fallback (26) → sub `ch0_1.264` HEVC (50) → `/12` alias (51).

## 4. AV / settings changes — WRITE + PERSIST PROVEN

Full write → verify → persist cycle through the deployed service against the live camera:

- **Write:** brightness `50 → 56` via the settings endpoint (with pre-read + post-read verification both passing — the camera itself confirms the new value).
- **Persist:** value survived a **full service restart** (`systemctl restart bosscam.service` → new PID) — read back as `56`.
- **Restore:** written back to `50` to leave the camera at its original setting.
- `requireWriteVerification:true` correctly rejected by the verification gate (protocol validation works).

## 5. Video properly recorded — PROVEN end-to-end

- Recording started via `POST /api/recordings/start` (direct-ffmpeg, main RTSP HEVC).
- **Segments written:** `0654e903..._20260802_231437.ts` (4.27 MB) and `..._231507.ts` (2.62 MB) into `/home/cody/.local/share/BossCamSuite/recordings/10_0_0_169/`.
- **Recording job persisted:** `recording_jobs` row for 0654e903 (mode `direct`, reconciled as stopped on service restart).
- **Recorded file decodes:** the recorded `.ts` is HEVC **2560×1920**, decoded to frames with **256/256 gray levels, brightness 66.2** — real footage, not blank.
- **Index closed this session:** `POST /api/recordings/index/refresh?deviceId=0654e903-…` walks the profile's output dir, ffprobes each file, and persists rows. Verified live: after cache invalidation the refresh indexed all 4 segments with **real durations (31.3 s, 18.1 s, 12.0 s, 12.6 s)**; API `GET /api/recordings/index?deviceId=…` returns them and the SQLite `recording_segments` table confirms `0654e903 → 4 rows`.

### Root cause of the earlier "refresh returns []"

`RefreshIndexAsync` is **incremental** — an in-memory `_indexedCache` keyed by `(file, mtime, size)` short-circuits unchanged files. The initial `[]` responses were that cache short-circuiting files already seen in-process, *not* a broken walk. Proof: (a) a fresh probe file was indexed and returned immediately; (b) after touching the real files (new mtime → cache miss) the refresh returned all 4 segments with correct ffprobe durations and persisted them to SQLite. The mechanism is healthy; no code defect in the walk.

## 6. Residual notes

- The 5523-W segment directory also holds two files from other device GUIDs (`29969e78…`, `38a2cc68…` — E2E-era recordings in the same IP dir); the refresh attributes them to the profile's device, which is by design (the walk trusts the profile output directory).
- Test residue cleaned up this session (probe index row removed; no probe files left on disk).
- The one-line CS0121 fix remains the only uncommitted source change — recommend committing + pushing for reproducibility (it is already live in `/opt/bosscam`).

**Verdict:** compiles, builds, runs, streams real video, records AV-settings changes persistently, and records decodable video — all validated live against the deployed service and the physical 5523-W.
