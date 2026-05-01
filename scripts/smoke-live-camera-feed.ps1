param(
    [string]$CameraHost = "10.0.0.29",
    [string]$Username = "admin",
    [string]$Password = "",
    [string]$ServiceBaseUrl = "http://127.0.0.1:5317",
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$LaunchPlayer
)

$ErrorActionPreference = "Stop"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $RepoRoot "artifacts/live-feed-smoke/$stamp"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Save-Json($name, $value) {
    $value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $outDir $name)
}

$ffprobe = Get-Command ffprobe -ErrorAction SilentlyContinue
if (-not $ffprobe) { throw "ffprobe not found on PATH." }

$main = "rtsp://$Username`:$Password@$CameraHost`:554/ch0_0.264"
$sub = "rtsp://$Username`:$Password@$CameraHost`:554/ch0_1.264"

function Probe($uri, $name) {
    $jsonPath = Join-Path $outDir "$name.ffprobe.json"
    & $ffprobe.Source -v quiet -print_format json -show_streams $uri | Set-Content -LiteralPath $jsonPath
    $json = Get-Content -Raw -LiteralPath $jsonPath | ConvertFrom-Json
    $video = @($json.streams | Where-Object { $_.codec_type -eq "video" })[0]
    [pscustomobject]@{ uri=$uri; codec=$video.codec_name; width=$video.width; height=$video.height; fps=$video.avg_frame_rate }
}

$mainProbe = Probe $main "main"
$subProbe = Probe $sub "sub"

$serviceReachable = $false
try {
    Invoke-RestMethod "$ServiceBaseUrl/api/health" -TimeoutSec 3 | Out-File (Join-Path $outDir "service-health.json")
    $serviceReachable = $true
} catch {}

$device = $null
$refresh = $null
$truth = $null
$sources = @()
if ($serviceReachable) {
    $rawDevices = @((Invoke-WebRequest "$ServiceBaseUrl/api/devices" -UseBasicParsing).Content | ConvertFrom-Json)
    $devices = New-Object System.Collections.Generic.List[object]
    foreach ($item in $rawDevices) {
        if ($item -is [array]) {
            foreach ($child in $item) { $devices.Add($child) | Out-Null }
        } else {
            $devices.Add($item) | Out-Null
        }
    }
    $device = $null
    foreach ($candidate in $devices) {
        if ($candidate.ipAddress -eq $CameraHost) {
            $device = $candidate
            break
        }
    }
    if (-not $device) {
        $body = @{
            name = "Live 5523-W $CameraHost"
            ipAddress = $CameraHost
            port = 80
            loginName = $Username
            password = $Password
            hardwareModel = "5523-W"
            deviceType = "IPC"
            metadata = @{ onvifPort = "8888" }
        }
        $device = Invoke-RestMethod "$ServiceBaseUrl/api/devices" -Method Post -ContentType "application/json" -Body ($body | ConvertTo-Json -Depth 10)
    }
    $deviceId = [string]$device.id
    $refreshUrl = "$ServiceBaseUrl/api/devices/$deviceId/endpoint-truth/refresh"
    $refreshUrl | Set-Content -LiteralPath (Join-Path $outDir "refresh-url.txt")
    $refresh = Invoke-RestMethod $refreshUrl -Method Post
    $truth = Invoke-RestMethod "$ServiceBaseUrl/api/devices/$deviceId/endpoint-truth"
    $sources = @(Invoke-RestMethod "$ServiceBaseUrl/api/devices/$deviceId/sources")
    Save-Json "bossCam-refresh.json" $refresh
    Save-Json "bossCam-truth.json" $truth
    Save-Json "bossCam-sources.json" $sources
}

if ($LaunchPlayer) {
    $ffplay = Get-Command ffplay -ErrorAction SilentlyContinue
    $vlc = Get-Command vlc -ErrorAction SilentlyContinue
    if ($ffplay) { Start-Process $ffplay.Source -ArgumentList @("-rtsp_transport","tcp",$main) -WindowStyle Hidden }
    elseif ($vlc) { Start-Process $vlc.Source -ArgumentList @($main) -WindowStyle Hidden }
}

$summary = [pscustomobject]@{
    ffprobeFound = $true
    mainCodec = $mainProbe.codec
    mainWidth = $mainProbe.width
    mainHeight = $mainProbe.height
    subCodec = $subProbe.codec
    subWidth = $subProbe.width
    subHeight = $subProbe.height
    serviceReachable = $serviceReachable
    bossCamRefreshAttempted = [bool]$refresh
    persistedMainSource = [bool](@($sources) | Where-Object { $_.url -eq $main -and $_.metadata.probedCodec -eq "h264" })
    persistedSubSource = [bool](@($sources) | Where-Object { $_.url -eq $sub -and $_.metadata.probedCodec -eq "hevc" })
    mechanicalPtzDisabled = if ($truth) { -not [bool]$truth.profile.ptz.movementControlsEnabled } else { $null }
    artifacts = $outDir
}
Save-Json "summary.json" $summary
$summary | ConvertTo-Json -Depth 10
