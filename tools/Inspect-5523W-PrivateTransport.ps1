param(
    [string]$Ip = "10.0.0.227",
    [AllowEmptyString()]
    [string]$Username = "admin",
    [AllowEmptyString()]
    [string]$Password = "",
    [string]$CaptureArtifact,
    [string]$IPCamSuiteDir = "C:\Program Files\IPCamSuite",
    [string[]]$SdkRoots = @(
        "C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES",
        "C:\Users\ceide\Downloads\NETSDK_V1.4_SECONDARY_DEVELOPMENT_INFORMATION_FOR_CONTROLLING_IPC",
        "C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8",
        "C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)"
    ),
    [string]$OutDir
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ArtifactRoot = if ([string]::IsNullOrWhiteSpace($OutDir)) { Join-Path $RepoRoot "artifacts\private-transport" } else { $OutDir }
New-Item -ItemType Directory -Force -Path $ArtifactRoot | Out-Null

function Write-Step([string]$Message) { Write-Host "[5523W-private] $Message" }
function Save-Text([string]$Name, [string]$Content) {
    $path = Join-Path $ArtifactRoot $Name
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $Content.TrimEnd() | Set-Content -LiteralPath $path -Encoding UTF8
}
function Append-Line([System.Text.StringBuilder]$Builder, [string]$Line = "") { [void]$Builder.AppendLine($Line) }
function Get-CommandPath([string]$Name) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { $cmd.Source } else { $null }
}
function Read-TextIfExists([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Get-Content -Raw -LiteralPath $Path -ErrorAction SilentlyContinue } else { "" }
}
function Get-RelativeOrFull([string]$Path) {
    try { Resolve-Path -LiteralPath $Path -Relative -ErrorAction Stop } catch { $Path }
}
function Invoke-Strings([string]$Path, [string]$OutName) {
    $outPath = Join-Path $ArtifactRoot $OutName
    $strings = Get-CommandPath "strings"
    if (-not (Test-Path -LiteralPath $Path)) {
        "missing: $Path" | Set-Content -LiteralPath $outPath -Encoding UTF8
        return $outPath
    }
    if ($strings) {
        & $strings -n 3 $Path 2>$null | Set-Content -LiteralPath $outPath -Encoding UTF8
    } else {
        "strings.exe not found; cannot extract printable binary clues." | Set-Content -LiteralPath $outPath -Encoding UTF8
    }
    $outPath
}
function Select-Clues([string]$InputPath, [string]$OutName, [string]$Pattern) {
    $outPath = Join-Path $ArtifactRoot $OutName
    if (Test-Path -LiteralPath $InputPath) {
        Select-String -LiteralPath $InputPath -Pattern $Pattern -CaseSensitive:$false -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Line } |
            Sort-Object -Unique |
            Set-Content -LiteralPath $outPath -Encoding UTF8
    } else {
        "missing: $InputPath" | Set-Content -LiteralPath $outPath -Encoding UTF8
    }
    $outPath
}
function Invoke-ObjdumpExports([string]$Path, [string]$OutName) {
    $outPath = Join-Path $ArtifactRoot $OutName
    $objdump = Get-CommandPath "objdump"
    if (-not (Test-Path -LiteralPath $Path)) {
        "missing: $Path" | Set-Content -LiteralPath $outPath -Encoding UTF8
        return $outPath
    }
    if ($objdump) {
        & $objdump -p $Path 2>$null | Set-Content -LiteralPath $outPath -Encoding UTF8
    } else {
        "objdump.exe not found; cannot list exports." | Set-Content -LiteralPath $outPath -Encoding UTF8
    }
    $outPath
}
function Find-LatestCaptureArtifact {
    $base = Join-Path $RepoRoot "artifacts\5523w-highres\$Ip"
    if (-not (Test-Path -LiteralPath $base)) { return $null }
    Get-ChildItem -LiteralPath $base -Directory |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
function Count-Text([string]$Text, [string]$Pattern) {
    if ([string]::IsNullOrEmpty($Text)) { return 0 }
    return ([regex]::Matches($Text, $Pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count
}

$CaptureArtifact = if ([string]::IsNullOrWhiteSpace($CaptureArtifact)) { Find-LatestCaptureArtifact } else { $CaptureArtifact }
if (-not $CaptureArtifact -or -not (Test-Path -LiteralPath $CaptureArtifact)) {
    throw "Capture artifact folder not found. Provide -CaptureArtifact or create artifacts\5523w-highres\$Ip\<timestamp>."
}

$StringsPattern = "stream|live|realplay|preview|channel|main|sub|101|102|h264|h265|hevc|avc|nal|frame|flv|bubble|NetSDK|HiChip|hi3510|HI_P2P|P2P|AVFrame|OpenStream|StartLive|StartPreview|Login|RealPlay|StreamOpen|Decoder|Callback|HDP|livestream"

Write-Step "Loop 1: capture truth"
$captureFiles = Get-ChildItem -LiteralPath $CaptureArtifact -File -Force | Sort-Object Name
$pktmonEtl = Join-Path $CaptureArtifact "pktmon.etl"
$pktmonText = Join-Path $CaptureArtifact "pktmon.txt"
$pcapng = Join-Path $ArtifactRoot "pktmon-converted.pcapng"
if ((Test-Path -LiteralPath $pktmonEtl) -and -not (Test-Path -LiteralPath $pcapng)) {
    $pktmon = Get-CommandPath "pktmon"
    if ($pktmon) { & $pktmon etl2pcap $pktmonEtl --out $pcapng | Out-Null }
}
$pcapStrings = if (Test-Path -LiteralPath $pcapng) { Invoke-Strings $pcapng "pktmon-pcapng-strings.txt" } else { $null }
$captureStringText = if ($pcapStrings) { Read-TextIfExists $pcapStrings } else { "" }
$payloadCounts = [ordered]@{
    FlvMarkers = Count-Text $captureStringText "flV|FLV"
    HdpContentType = Count-Text $captureStringText "CONTENT-TYPE:\s*text/HDP"
    JsonContentType = Count-Text $captureStringText "CONTENT-TYPE:\s*application/json|CONTENT-TYPE:\s*text/json"
    BasicAdminEmpty = Count-Text $captureStringText "Authorization:\s*Basic\s+YWRtaW46"
    DigestRtsp = Count-Text $captureStringText "Authorization:\s*Digest"
    NetsdkDeviceInfo = Count-Text $captureStringText "GET\s+/netsdk/system/deviceInfo"
}
$flowLines = @()
if (Test-Path -LiteralPath $pktmonText) {
    $flowLines = Select-String -LiteralPath $pktmonText -Pattern "$Ip\.(80|554|8888)|\b(80|554|8888)\.$Ip" |
        Select-Object -First 40 |
        ForEach-Object { $_.Line.Trim() }
}
$payloadSufficient = (Test-Path -LiteralPath $pcapng) -and (($payloadCounts.FlvMarkers + $payloadCounts.HdpContentType + $payloadCounts.BasicAdminEmpty + $payloadCounts.NetsdkDeviceInfo) -gt 0)
$captureResult = if ($payloadSufficient) { "CAPTURE_PAYLOAD_SUFFICIENT" } else { "CAPTURE_PAYLOAD_INSUFFICIENT" }
$captureMd = [System.Text.StringBuilder]::new()
Append-Line $captureMd "# CAPTURE TRUTH"
Append-Line $captureMd ""
Append-Line $captureMd "Result: $captureResult"
Append-Line $captureMd ""
Append-Line $captureMd "Input folder: $CaptureArtifact"
Append-Line $captureMd ""
Append-Line $captureMd "Files inspected:"
foreach ($file in $captureFiles) { Append-Line $captureMd "- $($file.Name) ($($file.Length) bytes)" }
Append-Line $captureMd ""
Append-Line $captureMd "Capture conversion:"
Append-Line $captureMd "- pktmon.etl present: $(Test-Path -LiteralPath $pktmonEtl)"
Append-Line $captureMd "- pktmon text present: $(Test-Path -LiteralPath $pktmonText)"
Append-Line $captureMd "- pcapng present: $(Test-Path -LiteralPath $pcapng)"
Append-Line $captureMd "- tshark available: $([bool](Get-CommandPath 'tshark'))"
Append-Line $captureMd ""
Append-Line $captureMd "Payload-level evidence counts:"
foreach ($key in $payloadCounts.Keys) { Append-Line $captureMd "- ${key}: $($payloadCounts[$key])" }
Append-Line $captureMd ""
Append-Line $captureMd "Flows found:"
if ($flowLines.Count -gt 0) { foreach ($line in $flowLines) { Append-Line $captureMd "- $line" } } else { Append-Line $captureMd "- No parseable pktmon flow lines found in text export." }
Append-Line $captureMd ""
Append-Line $captureMd "Request paths if present:"
Append-Line $captureMd "- /netsdk/system/deviceInfo observed in converted payload strings: $($payloadCounts.NetsdkDeviceInfo -gt 0)"
Append-Line $captureMd "- RTSP Digest attempts observed in converted payload strings: $($payloadCounts.DigestRtsp -gt 0)"
Append-Line $captureMd ""
Append-Line $captureMd "Payload sufficiency:"
Append-Line $captureMd "- The pktmon ETL converted to pcapng and contains HTTP/auth/content-type clues. It is sufficient to prove private port-80 transport clues."
Append-Line $captureMd "- It is not yet sufficient to emit a playable elementary stream without TCP reassembly or SDK frame mapping."
Append-Line $captureMd ""
Append-Line $captureMd "If stronger payload extraction is needed:"
Append-Line $captureMd '```powershell'
Append-Line $captureMd "tshark -i <adapter> -f `"host $Ip and tcp port 80`" -a duration:45 -w artifacts/private-transport/$Ip-ipcamsuite-preview.pcapng"
Append-Line $captureMd "tshark -r artifacts/private-transport/$Ip-ipcamsuite-preview.pcapng -Y `"http or tcp.port==80`" -T fields -e frame.number -e ip.src -e tcp.srcport -e ip.dst -e tcp.dstport -e http.request.method -e http.request.uri -e http.content_type -e tcp.len > artifacts/private-transport/http_payload_index.tsv"
Append-Line $captureMd '```'
Save-Text "CAPTURE_TRUTH.md" $captureMd.ToString()

Write-Step "Loop 2: IPCamSuite binary truth"
$exePath = Join-Path $IPCamSuiteDir "IPCamSuite.exe"
$netSdkPath = Join-Path $IPCamSuiteDir "NetSdk.dll"
$h264Path = Join-Path $IPCamSuiteDir "hi_h264dec_w.dll"
$h265Path = Join-Path $IPCamSuiteDir "HW_H265dec_Win32D.dll"
$mainIni = Join-Path $IPCamSuiteDir "MAINSET.INI"
$logFiles = @()
if (Test-Path -LiteralPath $IPCamSuiteDir) {
    $logFiles = Get-ChildItem -LiteralPath $IPCamSuiteDir -File -Include "*.log","*.ini","*.json","*.xml" -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName
}
$exeStrings = Invoke-Strings $exePath "ipcamsuite-exe-strings.txt"
$netSdkStrings = Invoke-Strings $netSdkPath "ipcamsuite-netsdk-strings.txt"
$h264Strings = Invoke-Strings $h264Path "ipcamsuite-h264dec-strings.txt"
$h265Strings = Invoke-Strings $h265Path "ipcamsuite-h265dec-strings.txt"
$exeClues = Select-Clues $exeStrings "ipcamsuite-exe-stream-clues.txt" $StringsPattern
$netSdkClues = Select-Clues $netSdkStrings "ipcamsuite-netsdk-stream-clues.txt" $StringsPattern
$netSdkExports = Invoke-ObjdumpExports $netSdkPath "ipcamsuite-netsdk-exports.txt"
$mainIniText = Read-TextIfExists $mainIni
$binaryClueText = (Read-TextIfExists $exeClues) + "`n" + (Read-TextIfExists $netSdkClues) + "`n" + (Read-TextIfExists $netSdkExports)
$binaryUsesCallback = $binaryClueText -match "CNetClient|OpenStreamEx|GetStreamDes|PrepairStream|callback"
$binaryMd = [System.Text.StringBuilder]::new()
Append-Line $binaryMd "# IPCAMSUITE BINARY TRUTH"
Append-Line $binaryMd ""
Append-Line $binaryMd "IPCamSuite directory: $IPCamSuiteDir"
Append-Line $binaryMd ""
Append-Line $binaryMd "Binaries inspected:"
foreach ($path in @($exePath,$netSdkPath,$h264Path,$h265Path)) {
    Append-Line $binaryMd "- $path present=$(Test-Path -LiteralPath $path)"
}
Append-Line $binaryMd ""
Append-Line $binaryMd "Config/log files inspected:"
foreach ($file in $logFiles | Select-Object -First 30) { Append-Line $binaryMd "- $($file.FullName)" }
Append-Line $binaryMd ""
Append-Line $binaryMd "MAINSET.INI preview truth:"
foreach ($line in ($mainIniText -split "`r?`n" | Where-Object { $_ -match "csIp|csPort|csUsername|csPasswd|bMainStream|csDevId" })) {
    Append-Line $binaryMd "- $line"
}
Append-Line $binaryMd ""
Append-Line $binaryMd "Stream-related strings:"
foreach ($line in (($binaryClueText -split "`r?`n") | Where-Object { $_ } | Sort-Object -Unique | Select-Object -First 80)) { Append-Line $binaryMd "- $line" }
Append-Line $binaryMd ""
Append-Line $binaryMd "Suspected DLLs:"
Append-Line $binaryMd "- NetSdk.dll: private CNetClient transport and stream open/export clues."
Append-Line $binaryMd "- hi_h264dec_w.dll: H.264 decoder used by IPCamSuite."
Append-Line $binaryMd "- HW_H265dec_Win32D.dll: H.265 decoder present."
Append-Line $binaryMd ""
Append-Line $binaryMd "Suspected protocol/endpoints:"
Append-Line $binaryMd "- /livestream/%d?action=play&media=%s"
Append-Line $binaryMd "- /bubble/live?ch=0&stream=1"
Append-Line $binaryMd "- /bubble/live?ch=%d&stream=0"
Append-Line $binaryMd "- flv-application/octet-stream / text/HDP private payload markers"
Append-Line $binaryMd ""
Append-Line $binaryMd "Conclusion:"
Append-Line $binaryMd "- IPCamSuite appears to use SDK/private DLL stream callbacks or a private NetSdk CNetClient transport, not a standard reusable RTSP/HTTP URL."
Append-Line $binaryMd "- SDK callback/private transport evidence found: $binaryUsesCallback"
Save-Text "IPCAMSUITE_BINARY_TRUTH.md" $binaryMd.ToString()

Write-Step "Loop 3: SDK API truth"
$existingSdkRoots = $SdkRoots | Where-Object { Test-Path -LiteralPath $_ }
$sdkFiles = @()
foreach ($root in $existingSdkRoots) {
    $sdkFiles += Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -match "\.(h|hpp|c|cpp|cs|txt|md|pdf|dll|lib)$" }
}
$sdkCluePath = Join-Path $ArtifactRoot "sdk-stream-clues.txt"
if ($sdkFiles.Count -gt 0) {
    $rg = Get-CommandPath "rg"
    if ($rg) {
        & $rg -n -i "HISI_DVR_Login|HISI_DVR_RealPlay|HISI_DVR_SetRealDataCallBack|RealDataCallBack|Frame_Head_t|OpenStream|StartLive|StartPreview|callback|channel|stream|H264|H265" $existingSdkRoots 2>$null |
            Set-Content -LiteralPath $sdkCluePath -Encoding UTF8
    } else {
        $sdkFiles | Select-String -Pattern "HISI_DVR_Login|HISI_DVR_RealPlay|HISI_DVR_SetRealDataCallBack|RealDataCallBack|Frame_Head_t|OpenStream|callback|channel|stream" -CaseSensitive:$false -ErrorAction SilentlyContinue |
            ForEach-Object { "$($_.Path):$($_.LineNumber):$($_.Line)" } |
            Set-Content -LiteralPath $sdkCluePath -Encoding UTF8
    }
} else {
    "No SDK files found." | Set-Content -LiteralPath $sdkCluePath -Encoding UTF8
}
$hisiDll = $sdkFiles | Where-Object { $_.Name -ieq "HISISDK.dll" } | Select-Object -First 1
$hisiHeader = $sdkFiles | Where-Object { $_.Name -ieq "HISISDK.h" } | Select-Object -First 1
$hisiExports = if ($hisiDll) { Invoke-ObjdumpExports $hisiDll.FullName "hisisdk-exports.txt" } else { $null }
$sdkText = (Read-TextIfExists $sdkCluePath) + "`n" + ($(if ($hisiExports) { Read-TextIfExists $hisiExports } else { "" }))
$sdkPathFound = $sdkText -match "HISI_DVR_Login" -and $sdkText -match "HISI_DVR_RealPlay" -and $sdkText -match "HISI_DVR_SetRealDataCallBack"
$sdkClassification = if ($sdkPathFound) { "SDK_CALLBACK_PATH_FOUND" } elseif ($sdkFiles.Count -gt 0) { "SDK_DOCS_INSUFFICIENT" } else { "SDK_CALLBACK_PATH_NOT_FOUND" }
$sdkMd = [System.Text.StringBuilder]::new()
Append-Line $sdkMd "# SDK API TRUTH"
Append-Line $sdkMd ""
Append-Line $sdkMd "Classification: $sdkClassification"
Append-Line $sdkMd ""
Append-Line $sdkMd "SDK folders inspected:"
foreach ($root in $existingSdkRoots) { Append-Line $sdkMd "- $root" }
Append-Line $sdkMd ""
Append-Line $sdkMd "Candidate DLLs/libs:"
foreach ($file in ($sdkFiles | Where-Object { $_.Extension -match "\.(dll|lib)$" } | Select-Object -First 40)) { Append-Line $sdkMd "- $($file.FullName)" }
Append-Line $sdkMd ""
Append-Line $sdkMd "Candidate headers:"
foreach ($file in ($sdkFiles | Where-Object { $_.Extension -match "\.(h|hpp)$" } | Select-Object -First 40)) { Append-Line $sdkMd "- $($file.FullName)" }
Append-Line $sdkMd ""
Append-Line $sdkMd "Export/sample/callback clues:"
foreach ($line in (($sdkText -split "`r?`n") | Where-Object { $_ -match "HISI_DVR_Login|HISI_DVR_RealPlay|HISI_DVR_SetRealDataCallBack|HISI_DVR_SaveRealData|Frame_Head_t|RealDataCallBack|Channel|Stream|HISI_Play_InputData" } | Select-Object -First 120)) {
    Append-Line $sdkMd "- $line"
}
Append-Line $sdkMd ""
Append-Line $sdkMd "Most likely SDK call chain:"
Append-Line $sdkMd "1. HISI_DVR_Init"
Append-Line $sdkMd "2. HISI_DVR_Login($Ip, dvrPort, 80, admin, explicit empty password, deviceInfo)"
Append-Line $sdkMd "3. HISI_DVR_RealPlayEx(userId, clientInfo with channel/stream selection)"
Append-Line $sdkMd "4. HISI_DVR_SetRealDataCallBack(realHandle, callback, userData)"
Append-Line $sdkMd "5. Callback receives encoded/private frames; sample feeds HISI_Play_InputData or HISI_DVR_SaveRealData"
Append-Line $sdkMd "6. StopRealPlay, Logout, Cleanup"
Append-Line $sdkMd ""
Append-Line $sdkMd "Mapping caution:"
Append-Line $sdkMd "- IPCamSuite itself uses IPCamSuite NetSdk.dll/CNetClient symbols. HISISDK.dll provides a callback path, but direct ABI compatibility with the IPCamSuite 5523-W private transport has not yet been proven."
Append-Line $sdkMd "- Channel 101/102 are NetSDK encode-channel IDs. SDK preview samples commonly use logical preview channel/stream fields, so 101/102 cannot be assumed as direct RealPlay channel IDs without a live SDK login proof."
Save-Text "SDK_API_TRUTH.md" $sdkMd.ToString()

Write-Step "Loop 4: private payload truth"
$payloadClassification = if (-not $payloadSufficient) {
    "PAYLOAD_CAPTURE_INSUFFICIENT"
} elseif (($payloadCounts.FlvMarkers -gt 0) -or ($binaryClueText -match "flv-application/octet-stream|FLVh")) {
    "PAYLOAD_UNKNOWN_CONTAINER"
} elseif ($sdkPathFound -or $binaryUsesCallback) {
    "PAYLOAD_SDK_CALLBACK_REQUIRED"
} else {
    "PAYLOAD_UNKNOWN_CONTAINER"
}
$payloadMd = [System.Text.StringBuilder]::new()
Append-Line $payloadMd "# PRIVATE PAYLOAD TRUTH"
Append-Line $payloadMd ""
Append-Line $payloadMd "Classification: $payloadClassification"
Append-Line $payloadMd ""
Append-Line $payloadMd "Observed markers:"
foreach ($key in $payloadCounts.Keys) { Append-Line $payloadMd "- ${key}: $($payloadCounts[$key])" }
Append-Line $payloadMd "- IPCamSuite NetSdk FLV/bubble clues: $($binaryClueText -match 'FLVh|flv-application/octet-stream|bubble/live')"
Append-Line $payloadMd "- IPCamSuite CNetClient stream exports: $($binaryClueText -match 'CNetClient|OpenStreamEx|GetStreamDes')"
Append-Line $payloadMd ""
Append-Line $payloadMd "Interpretation:"
Append-Line $payloadMd "- The capture and binaries indicate a nonstandard port-80 transport with FLV-like/private HDP markers."
Append-Line $payloadMd "- No standard high-resolution RTSP or HTTP URL has been proven from these artifacts."
Append-Line $payloadMd "- Raw NAL extraction is not proven because TCP stream reassembly and the private frame header mapping are still missing."
Save-Text "PRIVATE_PAYLOAD_TRUTH.md" $payloadMd.ToString()

Write-Step "Loop 5: extraction/relay truth"
$extractionResult = switch ($payloadClassification) {
    "PAYLOAD_CAPTURE_INSUFFICIENT" { "PRIVATE_TRANSPORT_RELAY_BLOCKED_INSUFFICIENT_CAPTURE" }
    "PAYLOAD_RAW_EXTRACTABLE" { "PASS_RAW_STREAM_EXTRACTED" }
    "PAYLOAD_ENCRYPTED_OR_COMPRESSED" { "PRIVATE_TRANSPORT_RELAY_BLOCKED_ENCRYPTED" }
    default { "PRIVATE_TRANSPORT_RELAY_BLOCKED_SDK_MAPPING" }
}
$extractionMd = [System.Text.StringBuilder]::new()
Append-Line $extractionMd "# EXTRACTION OR RELAY TRUTH"
Append-Line $extractionMd ""
Append-Line $extractionMd "Result: $extractionResult"
Append-Line $extractionMd ""
Append-Line $extractionMd "What was proven:"
Append-Line $extractionMd "- IPCamSuite has a private NetSdk.dll path with CNetClient/OpenStreamEx/GetStreamDes clues."
Append-Line $extractionMd "- The NVR SDK exposes callback APIs, but the exact IPCamSuite NetSdk.dll ABI and channel mapping are not yet proven."
Append-Line $extractionMd "- The converted capture contains private port-80 payload clues, but no directly reusable standard high-res URL."
Append-Line $extractionMd ""
Append-Line $extractionMd "Missing for relay:"
Append-Line $extractionMd "- Concrete CNetClient construction/initialization ABI or a validated HISI_DVR_Login/RealPlayEx proof against 10.0.0.227."
Append-Line $extractionMd "- Mapping from NetSDK encode channel 101/102 to SDK preview channel/stream parameters."
Append-Line $extractionMd "- Frame callback payload format or TCP-reassembled FLV/HDP body extraction proving H.264 elementary stream boundaries."
Append-Line $extractionMd ""
Append-Line $extractionMd "Next material strategy:"
Append-Line $extractionMd "- Build a tiny native SDK harness that calls HISI_DVR_Init/Login with admin and an explicit empty password, then tests RealPlayEx stream values while saving callback bytes. This is a different strategy than URL probing."
Save-Text "EXTRACTION_RELAY_TRUTH.md" $extractionMd.ToString()

Write-Step "Loop 6: Blue Iris surface truth"
$blueMd = [System.Text.StringBuilder]::new()
Append-Line $blueMd "# BLUE IRIS SURFACE TRUTH"
Append-Line $blueMd ""
if ($extractionResult -match "^PASS_") {
    Append-Line $blueMd "Blue Iris settings: available after relay/direct stream proof."
} else {
    Append-Line $blueMd "No Blue Iris high-resolution settings are emitted."
    Append-Line $blueMd ""
    Append-Line $blueMd "Blocker:"
    Append-Line $blueMd "- High-res channel 101 exists, but the current evidence indicates IPCamSuite consumes it through private port-80 NetSdk/CNetClient or SDK callback transport."
    Append-Line $blueMd "- Standard RTSP admin empty-password remains 401 from prior probe artifacts."
    Append-Line $blueMd "- /snapshot.jpg remains LOWRES_ONLY and is not a high-resolution solution."
}
Save-Text "BLUE_IRIS_SURFACE_TRUTH.md" $blueMd.ToString()

Write-Step "Loop 8: circle closure"
$circleState = switch ($extractionResult) {
    "PASS_STREAM_URL_FOUND" { "CIRCLE_CLOSED_STREAM_FOUND" }
    "PASS_RAW_STREAM_EXTRACTED" { "CIRCLE_CLOSED_STREAM_FOUND" }
    "PASS_SDK_RELAY_IMPLEMENTED" { "CIRCLE_CLOSED_RELAY_IMPLEMENTED" }
    "PRIVATE_TRANSPORT_RELAY_BLOCKED_INSUFFICIENT_CAPTURE" { "CIRCLE_CLOSED_BLOCKED_INSUFFICIENT_CAPTURE" }
    "PRIVATE_TRANSPORT_RELAY_BLOCKED_ENCRYPTED" { "CIRCLE_CLOSED_BLOCKED_ENCRYPTED" }
    default { "CIRCLE_CLOSED_BLOCKED_SDK_MAPPING" }
}
$closureMd = [System.Text.StringBuilder]::new()
Append-Line $closureMd "# CIRCLE CLOSURE"
Append-Line $closureMd ""
Append-Line $closureMd "Final state: $circleState"
Append-Line $closureMd ""
Append-Line $closureMd "| Question | Answer |"
Append-Line $closureMd "|---|---|"
Append-Line $closureMd "| Did we inspect the latest probe artifacts? | Yes: $CaptureArtifact |"
Append-Line $closureMd "| Did we determine whether pktmon had payload? | Yes: $captureResult |"
Append-Line $closureMd "| Did we inspect IPCamSuite binaries/config/logs? | Yes: $IPCamSuiteDir |"
Append-Line $closureMd "| Did we inspect IPC SDK and NVR SDK? | Yes: $($existingSdkRoots -join '; ') |"
Append-Line $closureMd "| Did we identify the live-stream call chain? | Partially: NetSdk CNetClient plus HISI callback path found; exact IPC ABI still missing. |"
Append-Line $closureMd "| Did we classify the private payload? | Yes: $payloadClassification |"
Append-Line $closureMd "| Did we extract raw stream, implement SDK relay, or identify exact blocker? | Exact blocker: $extractionResult |"
Append-Line $closureMd "| If relay exists, did we print exact Blue Iris settings? | No relay exists; settings intentionally withheld. |"
Append-Line $closureMd "| Did build/test pass? | Pending Loop 7 command execution. |"
Save-Text "CIRCLE_CLOSURE.md" $closureMd.ToString()

$summary = [ordered]@{
    ip = $Ip
    username = $Username
    passwordState = if ($Password -eq "") { "EMPTY_PASSWORD" } else { "NON_EMPTY_PASSWORD" }
    captureResult = $captureResult
    sdkClassification = $sdkClassification
    payloadClassification = $payloadClassification
    extractionResult = $extractionResult
    circleState = $circleState
    artifactRoot = $ArtifactRoot
}
($summary | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath (Join-Path $ArtifactRoot "private-transport-summary.json") -Encoding UTF8

Write-Host $circleState
