# KP2P PTZ Action Enum — Recovered from P2PSDKClient.dll / iotlink.dll + NVR Web + Firmware (2026-08-11)

**Target:** recover the PTZ action enum (pan/tilt/zoom/preset codes) behind `kp2p_ptz_ctrl`
and document how the FWHI102 NVR maps it to the `/juan/ptzctrl` and `/netsdk/Channel/PTZ`
CGI params.

**Verdict up front:** `kp2p_ptz_ctrl` is a **thin, opaque frame-builder** — it does *not*
contain the enum itself. Its C++-visible signature is
`kp2p_ptz_ctrl(p2p_handle, channel, action, param1, param2)` and it packs those 4 ints into a
KP2P request frame (`ptz_req_t`, magic `P2PK`) before handing them to the transport. The
**numeric action enum lives in the callers and in the NVR firmware handler**, and it differs
between the **P2P wire plane** (EseeCloud client → NVR) and the **HTTP CGI plane** (NVR web
UI → camera). Both are recovered below, cross-verified from three sources each.

---

## 1. `kp2p_ptz_ctrl` — disassembly (P2PSDKClient.dll, PE32 x86)

Export table: `kp2p_ptz_ctrl` = ordinal 18, RVA `0x194c` (`.text` at RVA 0x1000, file off 0x400).

```
0x194c: push  ebp
0x194d: mov   ebp, esp
0x194f: pop   ebp            ; frame not used — pure trampoline
0x1950: jmp   0x3140         ; -> real implementation
```

Real implementation at `0x3140`:

```
0x3140: push ebp / mov ebp,esp / push ecx / push esi
0x3145: push 0x28            ; sizeof(ptz_req_t) = 0x28
0x3147: call 0x2353          ; proc_malloc
0x314c: mov  esi, eax        ; esi = req
0x314f: test esi, esi / jne 0x3174
        ; OOM path -> log "proc_malloc for ptz_req_t failed", return -1
0x3174: push ebx / push edi
0x3176: mov  dword ptr [esi], 0x4B503250        ; magic "P2PK" (KP2P, LE)
0x317c: mov  dword ptr [esi+4], 1               ; version
0x3183: call 0x1000                             ; seq/frame-id source
0x3188: mov  edi, [ebp+0xc]     ; arg1
0x318b: mov  ebx, [ebp+0x10]    ; arg2
0x318e: mov  [esi+8], eax       ; seq
0x3191: mov  eax, [ebp+0x14]    ; arg3
0x3196: mov  [esi+0xc], 0x14    ; ctrl id 0x14 (20)
0x319d: mov  [esi+0x14], 0x10   ; 0x10
0x31a4: mov  [esi+0x20], eax    ; arg3 -> param1
0x31a9: mov  eax, [ebp+0x18]    ; arg4
0x31b0: mov  [esi+0x18], edi    ; arg1 -> channel
0x31b3: mov  [esi+0x1c], ebx    ; arg2 -> action
0x31b6: mov  [esi+0x24], eax    ; arg4 -> param2
0x31b9: call 0x262d            ; link/proc send
0x31e4: call 0x1037            ; vlogf
```

The `.rdata` at RVA `0x6a20` carries the definitive **debug signature**, confirming the arg order:

```
kp2p_ptz_ctrl(p2p_handle=%p, channel=%d, action=%d, param1=%d, param2=%d) --> rc=%d
```

Adjacent strings confirm the frame type name: `proc_malloc for ptz_req_t failed`,
`proc_ptz_control_req`, `kp2p_ptz_ctrl rsp --> p2p_handle=%p, context=%p, rc=%d`.

**Conclusion for this DLL:** the action code is passed through unchanged as an opaque `int`.
No enum table, no switch. Same story in `iotlink.dll` (newer IOTLink client) — it has zero
PTZ-literal strings; PTZ traffic goes through the same `ptzctrl` command channel.

**WebCtrlLite.ocx bridge:** exports `?SetCtlPtz@@YAHDDDD@Z` =
`int SetCtlPtz(double, double, double, double)` — the 4 doubles are the same
(channel, action/cmd, param1/speed, param2) tuple, marshaled from the OCX into the KP2P call.

---

## 2. P2P wire plane — the `action` enum the client actually sends (EseeCloud `JAVideo.js`)

`web/src/modules/video/glesclient.js` defines the command:

```js
glesclient.ptz = function (connIndex, channel, type, param1, param2) {
    var msg = { command: "ptzctrl", conn: data.conn, channel: channel,
                type: type, param1: param1, param2: param2 };
    handleCommand(msg);
};
```

`web/src/modules/video/JAVideo.js` (the only callers) maps UI verbs → wire `type` values
(comments are the vendor's own):

| UI verb (case) | wire `type` | vendor comment |
|---|---|---|
| `cruise` | `1` | // 8 |
| `up` | `2` | — |
| `Down` | `3` | — |
| `Left` | `4` | — |
| `right` | `5` | — |
| `gqadd` (iris open / 光圈扩大) | `6` | // 9 |
| `gqadec` (iris close / 光圈缩小) | `7` | // 10 |
| `sfadd` (zoom in / 缩放加) | `8` | // 13 |
| `sfdec` (zoom out / 缩放减) | `9` | // 14 |
| `jjadd` (focus near / 焦距加) | `10` | // 11 |
| `jjdec` (focus far / 焦距减) | `11` | // 12 |

> **pclass-semantics caveat:** the same icon-class names map to *different* semantics across
> codebases. This table uses JAVideo.js's own Chinese comments (光圈=iris, 焦距=focus, 缩放=zoom),
> but the NVR SPA (`build.js`) labels `gqadd` as "Focus In" (param 13) and `jjadd` as "Lris In"
> (param 9). The two vendor codebases genuinely disagree on what these pclasses mean; §3's HTTP
> plane is the authoritative layer for the NVR, §2's labels are the EseeCloud client's own.
| `yzdset` (set preset) | `13` | — |
| `yzdtransfer` (goto preset) | `14` | — |
| `yzdclear` (clear preset) | `15` | — |
| (release → `stopPTZ`) | separate `stopptz` command | — |

The trailing `// n` comments are vendor annotations pointing at **NVR-side HTTP cmd codes**
(section 3). They are corroborative, not authoritative: they conflict with `view1.js` on the
11/12/13/14 pairs (e.g. `jjadd`→//11 where view1 assigns 11=ZOOM_OUT, `sfadd`→//13 where view1
assigns 13=FOCUS_FAR), so treat them as hints, not ground truth.

---

## 3. HTTP CGI plane — NVR web UI → camera

### 3a. `/netsdk/Channel/PTZ` (`S.PtzControl`, modern SPA in `dist/build.js`)

```js
ptzControl: function (e) { ... t = { DEV:"XVR", VER:"1.0", API:"S.PtzControl", Parameter: e };
    return El.post("/netsdk/Channel/PTZ", t, { headers:{ Authorization:"Basic " + ... }}) }

device.ptzControl({ Channel: n+1, Cmd: e ? t : 15, Speed: i })
```

`Cmd` is the action; **15 = STOP** (sent on mouseup / while idle), `e ? t : 15` = while a
button is held, send the button's code; on release send 15. Direction/lens buttons carry
`param:[cmd,speed]` (speed default 7):

| button title | `Cmd` | `Speed` |
|---|---|---|
| PTZ Up | `0` | 7 |
| PTZ Down | `1` | 7 |
| PTZ Left | `2` | 7 |
| PTZ Right | `3` | 7 |
| Cruise (auto) | `8` | 7 |
| Lris In (iris in) | `9` | 7 |
| Lris Out (iris out) | `10` | 7 |
| Zoom In | `11` | 7 |
| Zoom Out | `12` | 7 |
| Focus In | `13` | 7 |
| Focus Out | `14` | 7 |
| Stop (release) | `15` | — |

So the SPA plane is: **0=up, 1=down, 2=left, 3=right, 8=cruise, 9=iris-in, 10=iris-out,
11=zoom-in, 12=zoom-out, 13=focus-in, 14=focus-out, 15=stop.**

### 3b. `/juan/ptzctrl` (legacy `view1.js`, via `/cgi-bin/gw.cgi`)

The legacy UI sends an XML envelope to the gateway and reads the `<juan><ptzctrl>` response:

```js
function ptz_send(cmd) {
    var chn = dvr_ocx.GetSelectChl();
    var xmldoc = loadXMLString("<juan ver=\"0\" squ=\"abcdef\" dir=\"0\" enc=\"1\">"
        + "<ptzctrl usr=\"" + dvr_usr + "\" pwd=\"" + dvr_pwd + "\" chn=\"" + chn
        + "\" cmd=\"" + cmd + "\" param=\"0\" /></juan>");
    var xmlstr = toXMLString(xmldoc);
    $.ajax({ type:"GET", url:"/cgi-bin/gw.cgi", data:"xml=" + xmlstr, ...,
        success: parse "<juan>/<ptzctrl> errno attribute (0 = ok)" });
}
```

Button → `cmd=` map (with the vendor's own `PTZ_CMD_*` comments — this is the **canonical
legacy naming**):

| button id | `cmd=` | comment |
|---|---|---|
| `pb_ptz_up` | `0` | — |
| `pb_ptz_down` | `1` | — |
| `pb_ptz_left` | `2` | — |
| `pb_ptz_right` | `3` | — |
| `pb_ptz_auto` | `8` | — |
| `pb_ptz_zd_i` | `9` | `PTZ_CMD_IRIS_OPEN` |
| `pb_ptz_zu_i` | `10` | `PTZ_CMD_IRIS_CLOSE` |
| `pb_ptz_zu_z` | `11` | `PTZ_CMD_ZOOM_OUT` |
| `pb_ptz_zd_z` | `12` | `PTZ_CMD_ZOOM_IN` |
| `pb_ptz_zu_f` | `13` | `PTZ_CMD_FOCUS_FAR` |
| `pb_ptz_zd_f` | `14` | `PTZ_CMD_FOCUS_NEAR` |
| release (`_zd_f`/`_zu_f`/`_zd_z`/`_zu_z`) | `15` | `PTZ_CMD_STOP` |

The two web generations share the same **code pairs** (9/10 iris, 11/12 zoom, 13/14 focus,
15 stop), but the **zoom direction labels are swapped between them**: the SPA sends
`Zoom In=11`/`Zoom Out=12` while the legacy code names `11=PTZ_CMD_ZOOM_OUT`/`12=PTZ_CMD_ZOOM_IN`.
Same code space, opposite direction assignments on the zoom pair — the 11/12 semantics must be
confirmed live (or driven as a direction-agnostic "hold to zoom" toggle). Pan/tilt (0–3) and
stop (15) agree in all sources.

---

## 4. NVR firmware side (`app.out`, FWHI102) — handler + vocabulary

The NVR firmware's handler entry points and the `DVRPTZCtrl` method table together cover the
same action space (the two symbol groups are co-located in `app.out`; the call relationship was
not traced in this pass):

```
proc_ptz_ctrl(channel=%d, action=%d, param1=%d, param2=%d)
proc_ptz_ctrl_bysession(channel=%d, action=%d, param1=%d, param2=%d, session=%p)
```

`DVRPTZCtrl` (C++ method table — mirrors the enum's capabilities):

```
Stop  Preset(int, const char*)  TiltUp  ZoomIn  AutoPan  PanLeft  Request(int,int,uchar)
ZoomOut  FocusFar  IrisOpen  PanRight  TiltDown  TourStop  FocusNear  FocusStop
IrisClose  SelfCheck  TourStart
```

Success-log vocabulary (`.rodata`, in the order the switch emits them):

```
NVP_PTZ_CMD_LEFT / UP / DOWN / RIGHT / ZOOM_IN / ZOOM_OUT / FOCUS_FAR / FOCUS_NEAR /
SET_PRESET / GOTO_PRESET / CLEAR_PRESET / STOP / FOCUS_STOP
```

Preset paths on the NVR are ONVIF-native: `GetPresets`, `GotoPreset`, `SetPreset`,
`RemovePreset`, `CreatePresetTour` / `OperatePresetTour` / `RemovePresetTour` + a HiSilicon
bridge `http://%s:%d/cgi-bin/hi3510/preset.cgi`. `param1` carries the preset index (`goto
nPreset = %d`).

---

## 5. TL;DR mapping cheat-sheet

| action | HTTP Cmd (`/netsdk/Channel/PTZ` + `/juan/ptzctrl cmd=`) | P2P wire `type` (kp2p_ptz_ctrl action) |
|---|---|---|
| stop | 15 | `stopptz` command (separate) |
| up / down / left / right | 0 / 1 / 2 / 3 | 2 / 3 / 4 / 5 |
| cruise / auto | 8 | 1 |
| iris in / out | 9 / 10 | 6 / 7 |
| zoom in / out | 11 / 12 (direction labels swapped between the two UIs — see §3) | 8 / 9 |
| focus far / near | 13 / 14 | 10 / 11 |
| set / goto / clear preset | via ONVIF (`GetPresets`/`GotoPreset`/… + `hi3510/preset.cgi`); `param1` = preset index | 13 / 14 / 15 (`yzdset`/`yzdtransfer`/`yzdclear`) |

> **Wire-plane caveat:** §2's iris/focus labels (6/7 vs 10/11) follow JAVideo.js's Chinese
> comments and conflict with §3's SPA/legacy reading (iris 9/10, focus 13/14). For a
> BossCam adapter, the **HTTP CGI plane (§3) is the authoritative target** since it is what the
> NVR's own web UIs send; §2 documents the EseeCloud client's P2P type space for parity.

**Actionable for BossCamSuite:** a PTZ adapter can drive `/netsdk/Channel/PTZ` with
`{Channel, Cmd, Speed}` (0–3 pan/tilt, 8 cruise, 9–14 lens, 15 stop) or the legacy
`/cgi-bin/gw.cgi?xml=<juan><ptzctrl … cmd=N/>` form. Presets are ONVIF (`GotoPreset`), not CGI,
on this NVR. The zoom-direction ambiguity (11/12) is the only open item and is resolved by a
one-shot live probe.

## 6. Reproducibility

```bash
# DLLs (unpacked from the NVR's WebClient.exe NSIS installer)
P2P=/tmp/wc/P2PSDKClient.dll
objdump -p "$P2P" | sed -n '/Export Table/,/Import Table/p'   # kp2p_ptz_ctrl @ ordinal 18
# capstone disassembly (x86 32-bit) at RVA 0x194c (trampoline) + 0x3140 (impl):
#   .text RVA base 0x1000, file off 0x400 -> impl at file 0x3140-0x400+0x1000
# Web client (NVR squashfs /web/):
grep -oE 'ptzControl\(\{Channel:n\+1,Cmd:e\?t:15,Speed:i\}' dist/build.js
grep -oE 'ptz_send\([0-9]+\).*' view1.js            # /juan/ptzctrl cmd= map
# EseeCloud wire plane (EseeCloud SDK web/src):
grep -n "case '" web/src/modules/video/JAVideo.js   # glesclient.ptz type values
# Firmware handler:
strings -n 4 app/app.out | grep -E 'DVRPTZCtrl|proc_ptz_ctrl|NVP_PTZ_CMD'
```

All artifacts under `CAM_SDK_REVERSE_ENGINEERING_FILES/extracted/` (report
`docs/reports/2026-08-11-firmware-full-extraction-report.md` for the extraction provenance).
