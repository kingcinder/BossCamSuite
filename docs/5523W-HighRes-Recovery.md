# 5523-W High-Resolution Recovery

Target only the failing 5523-W camera, normally `10.0.0.227`.

Run:

```powershell
pwsh -ExecutionPolicy Bypass -File .\tools\Probe-5523W-HighRes.ps1 -Ip 10.0.0.227 -RequirePreviewOpen
```

Open IPCamSuite preview for `10.0.0.227` before the capture window starts. The script saves artifacts under:

```text
artifacts/5523w-highres/<ip>/<timestamp>/
```

PASS means one of the required gates was proven:

- `PASS_HIGHRES_RTSP`: ffprobe opened a high-resolution RTSP stream. Use the printed Blue Iris RTSP settings.
- `PASS_HIGHRES_HTTP`: ffprobe opened a high-resolution HTTP stream. Use the printed Blue Iris HTTP settings.
- `PASS_CAPTURED_PRIVATE_TRANSPORT`: IPCamSuite appears to use private/binary transport; generic Blue Iris RTSP/HTTP cannot be claimed from current evidence.
- `PASS_LOWRES_ONLY`: only the low-resolution snapshot/standard stream is consumable.
- `FAIL_AUTH`: RTSP exists but tested credentialed URLs still fail authorization.
- `FAIL_INSUFFICIENT_CAPTURE`: IPCamSuite preview was not captured; open preview and rerun.

This report intentionally does not treat `/snapshot.jpg` as a high-resolution solution.
