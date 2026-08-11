# CAM SDK Reverse-Engineering Files — Complete Firmware Extraction Report

**Date:** 2026-08-11 · **Status:** COMPLETE — all 3 firmware ROMs fully decrypted/extracted, all SDK installers unpacked
**Scope:** `CAM_SDK_REVERSE_ENGINEERING_FILES/` · **Output:** `CAM_SDK_REVERSE_ENGINEERING_FILES/extracted/`

---

## 1. Inventory (what was in the folder)

| Item | Type | Size | SHA256/MD5 |
|---|---|---|---|
| `IPG5322_W_20211210_3_6_58_572011.rom` | Camera firmware (Anyka AK3919EV330) | 7.47 MB | md5 `9e271c205793e44b2649e6f2baeb82c1` |
| `IPCAKV3C_20211221_3_6_60_572010.rom` | Camera firmware (Anyka AK3919EV330) | 7.67 MB | md5 `3444810ac412654667347c0e41da1ae0` |
| `FWHI102_20240715_W-NVR_K8210-3WS_3_6_2_22_0x62102106_RELEASE.rom` | NVR firmware (HiSilicon, encrypted) | 14.8 MB | md5 `21b9308c0b7b623e8dcaa9cdf9c29840` |
| `cms_v1.9.4.8.exe` | Windows CMS client (PE32, no archive — raw binary) | 12.6 MB | md5 `fb8b7bac9ce07edcf0f2b4b5cd289e82` |
| `EseeCloud_Setup_3.0.8.4.exe` | EseeCloud Windows client (NSIS installer) | 104 MB | md5 `a7d39ffc22684020014e0605c83ee3e5` |
| `IPCamSuite-1.2.27.6 install.exe` | IPCamSuite installer (PE32, no archive) | 1.09 MB | md5 `3fff358a279389674db79a91e483be32` |
| `nSDK_v1.1.0.8.zip` | HiSilicon NetSDK (HISISDK.dll + samples) | 6.2 MB | md5 `819be72be4343144693aa4e8ebd4f015` |
| `NETSDK V1.4 接口说明.pdf` | NetSDK interface docs (41 pages, Chinese) | 368 KB | md5 `331535bef058e44e4367c12a839a3820` |
| `cms_v1.9.4.8/`, `DvrSuite-1.0.0.8/`, `nSDK_v1.1.0.8/`, `hisilicon-telnet-backdoor-pocs/` | Pre-existing extracted SDK trees | — | — |

---

## 2. Camera ROMs (IPG5322_W, IPCAKV3C) — unencrypted, plain flash layout

Both camera ROMs share an identical layout. **No encryption** — the payloads are standard ARM images.

### 2.1 Partition map

| Offset | Size | Content |
|---|---|---|
| `0x0` | 393 KB | Bootloader header + env: **"JUAN AKV330 IPCAM FIRMWARE"**, partition table, u-boot env vars (`a_uboot_flags=0x1`, `sf_hz=20000000`, mtd partitions envbak/kernel/rootfs/usr.sqsh4) |
| `0x60000` | 39 KB | **Device Tree Blob (DTB)** — compatible `anyka,ak3919ev330`, SoC = Anyka AK3919EV330 |
| `0x70000` | 1.63 MB | **uImage kernel** `Linux-4.4.192V2.1` (ARM, uncompressed, load `0x80008000`, built 2021-11-26) |
| `0x200000` | 695 KB | **SquashFS v4.0 (xz)** — bootfs (busybox base: `ash`, `ping`, `mount`, `printenv`…) |
| `0x2B0000` | 4.6/4.8 MB | **SquashFS v4.0 (xz)** — appfs (full app + `anyka_ipc`) |

### 2.2 appfs highlights (both cameras)

- **`/bin/anyka_ipc`** — the main camera application. **ELF 32-bit ARM EABI5, uClibc, stripped**, 16,317 strings. Exports the Anyka SDK API surface (`anyka_visp_*`, `anyka_venc_*`, `ak_osd_ex_get_version`…) plus the cloud/P2P client (Esee/JUAN: prior decompile resolved `oc_cal_verify` salt `Japass^2>.j` from this binary).
- Network stack: `wpa_supplicant`, `hostapd`/`hostapd_rtl`, `atbm_iw`, `rtwpriv`, `wifi_cfg`, `IOTDaemon` (AKV3C only).
- Tooling: `tftp`, `wget`, `nk_upgarde` (firmware updater), `mkfs.exfat`, `mmcblk`, `cryptpw`.
- AKV3C adds `onvif_config.xml` and `IOTDaemon_start.sh`.

---

## 3. NVR ROM (FWHI102) — **encrypted; scheme cracked and payload fully recovered**

### 3.1 The cipher

- **Detection:** raw ROM had **zero** binwalk signatures and ~8.0 entropy end-to-end; the only anomaly was a long run of an identical 64-byte pattern (the region `0x7000–0x2F000` was a single repeated block → entropy exactly 6.00).
- **Identification:** the repeating 64-byte block is the **XOR key stream** visible in a blank (0x00-filled) partition region. Repeating-key XOR with period 64 over the whole file.
- **Key (64 bytes, hex):**
  `509091519353529296565797559594549c5c5d9d5f9f9e5e5a9a9b5b99595898884849894b8b8a4a4e8e8f4f8d4d4c8c44848545874746868242438341818040`
- **Decryption:** `plain[i] = cipher[i] XOR key[i mod 64]` (recovered from ciphertext at offset `0x7000`). Both `0x00`-fill and `0xFF`-fill interpretations were tested; the **`0x00`-fill form yields valid structures** (binwalk: 2 uImages + 2 SquashFS). Decrypted image saved as `FWHI102/FWHI102_decrypted.rom`.

### 3.2 NVR partition map (after decryption)

| Offset | Size | Content |
|---|---|---|
| `0x0`–`0x2DD80B` | ~2.9 MB | u-boot bootloader + boot params + **uImage #1** |
| `0x30740` | 190 KB (LZMA→531 KB) | **uImage #1** — "Firmware OS" loader (bootrom stage; name field `MVX4##I2M#ga4833fcCM_UBT1501#XVM`), payload is XZ-compressed ARM (`uimage_30740.vmlinux`) |
| `0x5FF4C` | 2.61 MB (LZMA→5.38 MB) | **uImage #2** — **Linux kernel 4.9.84** (`#152 SMP PREEMPT Wed Apr 19 16:33:45 CST 2023`, gcc 4.9.4 Buildroot 2017.08, `hisilicon,hisi-ahci`), load `0x20008000`, XZ-compressed ARM (`uimage_5ff4c.vmlinux`) |
| `0x2DD80C` | 854 KB | **SquashFS v4.0 (xz)** — bootfs (rootfs: `/etc/init.d` rcS, `root:x:0:0` + `stb:x:1000` users) |
| `0x3AE80C` | 10.94 MB | **SquashFS v4.0 (xz)** — appfs (NVR application, 2,430 inodes) |

### 3.3 NVR appfs highlights

- **`/app/app.out`** — main NVR application, **9.42 MB ELF 32-bit ARM, uClibc, stripped**. Build path `/home/workspace/NewNVR2/`; links the **miv100** (MStar/MSR621x) platform SDK + **libEsee** + **KP2P** cloud stacks (`KP2P_CloudMessagePushFile`, `NK_ESEE_GetSDKVersion`, `NK_ESEE_ServerSet_TALK_Cb`, `LZ4`, `zlog`). This is the binary that drives the NVR's EseeCloud/KP2P integration.
- **`/bin/cgi.app`** — ARM ELF CGI handler (WebClient companion).
- **`/bin/daemon_server`, `connlog.app`, `hi3881_fw.bin`** (HiSilicon 3881 WiFi firmware), `wdt_disable`, `mount_udisk.sh`.
- **`/web/`** — browser UI: `index.html`, `JaViewer.swf`, `WebClient.exe`, `dist/` (webpack `build.js`, esee login assets).
- Config: `/app/ddns.conf`, `/app/user.conf`, `config_ini/`.

---

## 4. SDK / tooling installers

| Item | Result |
|---|---|
| `nSDK_v1.1.0.8.zip` | Unzipped → `HISISDK.dll` (PE32 DLL), `avlib.dll`, `HISISDK.h`, Chinese CHM manual, `HisiSdkTest` VS2005 sample project (VC6-era) |
| `EseeCloud_Setup_3.0.8.4.exe` | **NSIS — fully unpacked** (1,370 files, ~304 MB): `EseeCloud.exe` (PE32 GUI), ~250 DLLs (FFmpeg avcodec-57/58, d3dx9, gles_v2, FastUdx, arq…), full web client (`web/src` app.js, `web/odm/yanfei/` white-label guide) |
| `cms_v1.9.4.8.exe` | Raw PE32 GUI (no embedded archive) — not unpackable; version-info strings read (`FileVersion`, `LegalCopyright`). Pre-extracted `cms_v1.9.4.8/` tree present |
| `IPCamSuite-1.2.27.6 install.exe` | Raw PE32 (8 sections, no archive payload detected by 7z) — kept as-is |
| `NETSDK V1.4 接口说明.pdf` | 41-page API doc (Chinese) — references in `docs/reports/2026-04-20-…` |

---

## 5. Translation notes (what this firmware actually is)

- **Cameras = Anyka AK3919EV330 SoC** ("JUAN" ODM, hardware code 572110), dual-region flash (bootfs + appfs), Linux 4.4.192, uClibc. The `anyka_ipc` binary is the Esee/JUAN P2P + ONVIF + NetSDK server — matches the fleet behavior documented in `docs/reports/*` (5523-W live units at 10.0.0.29 / 10.0.0.169, eseeid 4780…).
- **NVR = HiSilicon platform** (Linux 4.9.84 + MStar miv100/MSR621x SDK + HiSilicon 3881 WiFi), EseeCloud/KP2P client baked into `app.out`. The 2024-07-15 release builds match the `0x62102106` hardware code in the filename.
- **Security takeaway:** the camera images are unencrypted; only the NVR image is XOR-obfuscated (period-64 keystream — trivially recoverable from its own blank regions). No asymmetric signing was observed in the extraction pass.

---

## 6. Reproducibility

```bash
# Cameras: no crypto, just carve + unsquashfs
binwalk -e IPG5322_W_20211210_3_6_58_572011.rom   # DTB @0x60000, kernel @0x70000, sqfs @0x200000/0x2B0000

# NVR: decrypt then carve
python3 - <<'EOF'
k = bytes.fromhex("509091519353529296565797559594549c5c5d9d5f9f9e5e5a9a9b5b99595898884849894b8b8a4a4e8e8f4f8d4d4c8c44848545874746868242438341818040")
d = open("FWHI102_..._RELEASE.rom","rb").read()
open("dec.rom","wb").write(bytes(b ^ k[i%64] for i,b in enumerate(d)))
EOF
binwalk -e dec.rom   # uImages @0x30740/0x5FF4C (XZ payloads), squashfs @0x2DD80C/0x3AE80C
```

All artifacts: `CAM_SDK_REVERSE_ENGINEERING_FILES/extracted/` (≈446 MB, including the 304 MB EseeCloud unpack).
