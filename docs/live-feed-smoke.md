# Live Feed Smoke

Run from PowerShell 7:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/smoke-live-camera-feed.ps1 -CameraHost 10.0.0.29 -Username admin -Password ""
```

The script uses `ffprobe` to verify the main and sub RTSP streams, optionally calls BossCam.Service endpoint truth refresh when the service is reachable, then checks `/api/devices/{id}/sources` for persisted playable RTSP sources.

Artifacts are saved under `artifacts/live-feed-smoke/<timestamp>/`. `summary.json` records codec, dimensions, service reachability, persisted source checks, and whether mechanical PTZ remained disabled.
