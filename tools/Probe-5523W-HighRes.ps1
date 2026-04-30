param(
    [string]$Ip = "10.0.0.227",
    [string]$Username = "admin",
    [AllowEmptyString()]
    [string]$Password = "",
    [int]$HttpPort = 80,
    [int]$RtspPort = 554,
    [int]$OnvifPort = 8888,
    [int]$CaptureSeconds = 45,
    [switch]$RequirePreviewOpen,
    [switch]$DryRun,
    [string]$OutDir
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactRoot = if ([string]::IsNullOrWhiteSpace($OutDir)) { Join-Path $repoRoot "artifacts/5523w-highres/$Ip/$stamp" } else { Join-Path $OutDir $stamp }
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

function Write-Step([string]$message) { Write-Host "[5523W] $message" }
function Save-Text([string]$name, [string]$content) { $content | Set-Content -LiteralPath (Join-Path $artifactRoot $name) }
function Save-Json([string]$name, $value) { $value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $artifactRoot $name) }
function BasicAuthHeader {
    $raw = "{0}:{1}" -f $Username, $Password
    "Basic " + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($raw))
}
function Invoke-CameraGet([string]$path, [string]$name) {
    $uri = "http://$Ip`:$HttpPort$path"
    try {
        $r = Invoke-WebRequest -Uri $uri -Headers @{ Authorization = BasicAuthHeader } -UseBasicParsing -TimeoutSec 8
        Save-Text $name $r.Content
        [pscustomobject]@{ uri=$uri; ok=$true; status=[int]$r.StatusCode; path=$path; body=$r.Content }
    } catch {
        $msg = $_.Exception.Message
        Save-Text $name $msg
        [pscustomobject]@{ uri=$uri; ok=$false; status=$_.Exception.Response.StatusCode.value__; path=$path; body=$msg }
    }
}
function Test-StreamCandidate([string]$url, [string]$name) {
    $ffprobe = Get-Command ffprobe -ErrorAction SilentlyContinue
    if (-not $ffprobe) { return [pscustomobject]@{ url=$url; ok=$false; reason="ffprobe missing" } }
    $out = Join-Path $artifactRoot "$name.ffprobe.out.txt"
    $err = Join-Path $artifactRoot "$name.ffprobe.err.txt"
    & $ffprobe.Source -v error -rtsp_transport tcp -select_streams v:0 -show_entries stream=codec_name,width,height,avg_frame_rate -of json $url > $out 2> $err
    $raw = Get-Content -Raw -LiteralPath $out -ErrorAction SilentlyContinue
    $json = if ($raw) { try { $raw | ConvertFrom-Json } catch { $null } } else { $null }
    $s = @($json.streams)[0]
    $errRaw = Get-Content -Raw -LiteralPath $err -ErrorAction SilentlyContinue
    [pscustomobject]@{ url=$url; ok=[bool]$s; codec=$s.codec_name; width=$s.width; height=$s.height; fps=$s.avg_frame_rate; out=$out; err=$err; error=$errRaw }
}
function Test-Snapshot {
    $file = Join-Path $artifactRoot "snapshot.jpg"
    try {
        Invoke-WebRequest -Uri "http://$Ip`:$HttpPort/snapshot.jpg" -Headers @{ Authorization = BasicAuthHeader } -OutFile $file -TimeoutSec 8 | Out-Null
        Add-Type -AssemblyName System.Drawing
        $img = [System.Drawing.Image]::FromFile($file)
        try {
            [pscustomobject]@{ ok=$true; width=$img.Width; height=$img.Height; path=$file; classification=$(if($img.Width -lt 1920 -and $img.Height -lt 1080) { "LOWRES_ONLY" } else { "HIGHRES_SNAPSHOT" }) }
        }
        finally {
            $img.Dispose()
        }
    } catch {
        Save-Text "snapshot-error.txt" $_.Exception.Message
        [pscustomobject]@{ ok=$false; width=$null; height=$null; path=$file; classification="FAILED" }
    }
}
function Get-CaptureTool {
    $tshark = Get-Command tshark -ErrorAction SilentlyContinue
    if ($tshark) { return [pscustomobject]@{ name="tshark"; path=$tshark.Source } }
    $pktmon = Get-Command pktmon -ErrorAction SilentlyContinue
    if ($pktmon) { return [pscustomobject]@{ name="pktmon"; path=$pktmon.Source } }
    $null
}
function Run-Capture {
    param([object]$Tool)
    if (-not $Tool) { Save-Text "capture-status.txt" "No tshark or pktmon found."; return $null }
    if ($DryRun) { Save-Text "capture-status.txt" "DryRun: would capture $Ip traffic with $($Tool.name)."; return $null }
    if ($RequirePreviewOpen) { Write-Step "Open IPCamSuite preview for $Ip now. Capturing for $CaptureSeconds seconds." }
    if ($Tool.name -eq "tshark") {
        $pcap = Join-Path $artifactRoot "capture.pcapng"
        $fields = Join-Path $artifactRoot "http_requests.tsv"
        & $Tool.path -a "duration:$CaptureSeconds" -f "host $Ip" -w $pcap | Out-Null
        & $Tool.path -r $pcap -Y "http.request or websocket or rtsp or tcp" -T fields -e frame.time -e ip.src -e ip.dst -e tcp.srcport -e tcp.dstport -e http.request.method -e http.host -e http.request.uri -e http.authorization -e frame.len > $fields 2>$null
        return [pscustomobject]@{ pcap=$pcap; fields=$fields; tool="tshark" }
    }
    $etl = Join-Path $artifactRoot "pktmon.etl"
    $txt = Join-Path $artifactRoot "pktmon.txt"
    pktmon filter remove | Out-Null
    pktmon filter add -i $Ip | Out-Null
    pktmon start --capture --pkt-size 0 --file-name $etl | Out-Null
    Start-Sleep -Seconds $CaptureSeconds
    pktmon stop | Out-Null
    pktmon format $etl -o $txt | Out-Null
    Save-Text "http_requests.tsv" "pktmon capture saved; tshark not available for HTTP field extraction."
    [pscustomobject]@{ pcap=$etl; fields=$txt; tool="pktmon" }
}
function Extract-Candidates {
    $set = [ordered]@{}
    foreach ($u in @(
        "rtsp://$Ip`:$RtspPort/ch0_0.264",
        "rtsp://$Ip`:$RtspPort/ch0_1.264",
        "rtsp://$Username`:$Password@$Ip`:$RtspPort/ch0_0.264",
        "rtsp://$Username`:$Password@$Ip`:$RtspPort/ch0_1.264"
    )) { $set[$u] = $true }
    $httpFile = Join-Path $artifactRoot "http_requests.tsv"
    if (Test-Path $httpFile) {
        foreach ($line in Get-Content $httpFile) {
            if ($line -match '(/[^ \t]+)') {
                $path = $matches[1]
                if ($path -match 'live|stream|video|bubble|mjpeg|h264|264') {
                    $set["http://$Ip`:$HttpPort$path"] = $true
                }
            }
        }
    }
    $set.Keys | Set-Content -LiteralPath (Join-Path $artifactRoot "candidate_urls.txt")
    $set.Keys
}

Write-Step "Target $Ip only. Artifacts: $artifactRoot"
if ($DryRun) { Write-Step "DryRun active: no live capture or stream probes." }

$deviceInfo = Invoke-CameraGet "/NetSDK/System/deviceInfo" "deviceInfo.json"
$channels = Invoke-CameraGet "/NetSDK/Video/encode/channels" "encode-channels.json"
$props101 = Invoke-CameraGet "/netsdk/video/encode/channel/101/properties" "channel-101-properties.json"
$keyFrame = Invoke-CameraGet "/netsdk/video/encode/channel/101/requestKeyFrame" "request-keyframe.txt"
$snapshot = Test-Snapshot

$channel101Pass = $false
try {
    $channelJson = $channels.body | ConvertFrom-Json
    $ch101 = @($channelJson | Where-Object { $_.id -eq 101 })[0]
    $channel101Pass = ($ch101.resolution -eq "2560x1920" -and $ch101.codecType -match "H\.264")
} catch {}

$capture = Run-Capture -Tool (Get-CaptureTool)
$candidates = Extract-Candidates
$results = @()
$idx = 0
foreach ($candidate in $candidates) {
    if ($DryRun) { continue }
    $idx++
    $safe = "candidate-$idx"
    $results += Test-StreamCandidate $candidate $safe
}
Save-Json "ffprobe-results.json" $results

$high = @($results | Where-Object { $_.ok -and ([int]$_.width -ge 1920 -or [int]$_.height -ge 1080) })[0]
$low = @($results | Where-Object { $_.ok -and $_.width -eq 704 -and $_.height -eq 480 })[0]
$private = $false
$sig = New-Object System.Collections.Generic.List[string]
if ($capture -and (Test-Path $capture.fields)) {
    $text = Get-Content -Raw -LiteralPath $capture.fields
    if ($text -match '80' -and $text -notmatch 'http.request') { $private = $true }
    if ($text -match 'multipart|mjpeg') { $sig.Add("MJPEG/multipart clue") | Out-Null }
    if ($text -match 'websocket') { $sig.Add("WebSocket clue") | Out-Null }
    if ($text -match 'rtsp') { $sig.Add("RTSP clue") | Out-Null }
}
$sig | Set-Content -LiteralPath (Join-Path $artifactRoot "http_payload_signatures.txt")

$gate = "FAIL_INSUFFICIENT_CAPTURE"
$reason = "No relevant IPCamSuite capture or high-res standard stream was proven."
if ($high -and $high.url -like "rtsp://*") { $gate = "PASS_HIGHRES_RTSP"; $reason = "High-res RTSP opened by ffprobe." }
elseif ($high -and $high.url -like "http://*") { $gate = "PASS_HIGHRES_HTTP"; $reason = "High-res HTTP stream opened by ffprobe." }
elseif ($private) { $gate = "PASS_CAPTURED_PRIVATE_TRANSPORT"; $reason = "Capture indicates nonstandard/private port 80 transport." }
elseif ($low) { $gate = "PASS_LOWRES_ONLY"; $reason = "Only low-res standard-consumable stream was proven." }
elseif ($snapshot.ok -and $snapshot.classification -eq "LOWRES_ONLY" -and ($results | Where-Object { $_.url -like "rtsp://$Username`:*@*" -and -not $_.ok -and ($_.error -match "401|Unauthorized") })) { $gate = "FAIL_RTSP_EMPTY_PASSWORD_AUTH_NEGOTIATION"; $reason = "High-res channel exists in NetSDK but is not exposed through tested standard HTTP/RTSP transport; IPCamSuite likely uses proprietary LanPrivateHttp/SDK framing or RTSP auth state not writable by ONVIF SetUser." }
elseif ($results | Where-Object { $_.url -like "rtsp://*" -and -not $_.ok }) { $gate = "FAIL_AUTH"; $reason = "RTSP paths exist but tested URLs did not open." }

$summary = [pscustomobject]@{
    ip=$Ip
    username=$Username
    passwordState=$(if($Password.Length -eq 0) { "EMPTY_PASSWORD" } else { "NON_EMPTY_PASSWORD_SUPPLIED" })
    channel101HighResPass=$channel101Pass
    snapshotPass=$snapshot.ok
    snapshotWidth=$snapshot.width
    snapshotHeight=$snapshot.height
    snapshotClassification=$snapshot.classification
    captureTool=$capture.tool
    gate=$gate
    reason=$reason
    highResUrl=$high.url
    lowResUrl=$low.url
    artifactRoot=$artifactRoot
}
Save-Json "summary.json" $summary

Write-Host ""
Write-Host "Known facts: channel101HighResPass=$channel101Pass captureTool=$($capture.tool)"
Write-Host "Test run: candidates=$($candidates.Count) highResUrl=$($high.url)"
Write-Host "PASS/FAIL: $gate"
Write-Host "Reason: $reason"
if ($gate -eq "PASS_HIGHRES_RTSP") {
    Write-Host "Blue Iris: Make=Generic/ONVIF, Model=RTSP H.264/H.265/MJPEG/MPEG4, Path=$($high.url), RTSP port=$RtspPort"
}
elseif ($gate -eq "PASS_HIGHRES_HTTP") {
    Write-Host "Blue Iris: Make=Generic, Model=HTTP H264/MJPEG, Path=$($high.url), HTTP port=$HttpPort"
}
Write-Host "Next single action: review $artifactRoot and rerun with IPCamSuite preview open if gate is FAIL_INSUFFICIENT_CAPTURE."
