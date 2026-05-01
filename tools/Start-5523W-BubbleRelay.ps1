param(
    [string]$Ip = "10.0.0.227",
    [string]$Username = "admin",
    [AllowEmptyString()]
    [string]$Password = "",
    [string[]]$KnownIps = @("10.0.0.4", "10.0.0.29", "10.0.0.227"),
    [int]$HttpPort = 80,
    [int]$RtspPort = 8554,
    [int]$ApiPort = 1984,
    [string]$LegacyAliasIp = "10.0.0.227",
    [switch]$StopExisting,
    [switch]$NoDownload
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot "artifacts\private-transport\go2rtc"
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

$go2rtc = Join-Path $artifactRoot "go2rtc.exe"
$config = Join-Path $artifactRoot "go2rtc.yaml"
$pidFile = Join-Path $artifactRoot "go2rtc.pid"
$stdout = Join-Path $artifactRoot "go2rtc.stdout.log"
$stderr = Join-Path $artifactRoot "go2rtc.stderr.log"

function Get-StreamPrefix([string]$Value) {
    "cam_" + ($Value -replace "[^0-9A-Za-z]+", "_")
}

function Stop-ExistingRelay {
    if (Test-Path -LiteralPath $pidFile) {
        $pidText = Get-Content -Raw -LiteralPath $pidFile
        $processId = 0
        if ([int]::TryParse($pidText.Trim(), [ref]$processId)) {
            Stop-Process -Id $processId -ErrorAction SilentlyContinue
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
                    break
                }

                Start-Sleep -Milliseconds 250
            }
        }
    }

    foreach ($process in Get-Process go2rtc -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $go2rtc }) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    }

    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $apiListener = Get-NetTCPConnection -LocalPort $ApiPort -State Listen -ErrorAction SilentlyContinue
        $rtspListener = Get-NetTCPConnection -LocalPort $RtspPort -State Listen -ErrorAction SilentlyContinue
        if (-not $apiListener -and -not $rtspListener) {
            break
        }

        Start-Sleep -Milliseconds 250
    }
}

if ($StopExisting) {
    Stop-ExistingRelay
}

if (-not (Test-Path -LiteralPath $go2rtc)) {
    if ($NoDownload) {
        throw "go2rtc.exe missing at $go2rtc and -NoDownload was set."
    }

    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/AlexxIT/go2rtc/releases/latest" -Headers @{ "User-Agent" = "BossCamSuite" }
    $asset = $release.assets | Where-Object { $_.name -eq "go2rtc_win64.zip" } | Select-Object -First 1
    if (-not $asset) {
        throw "go2rtc_win64.zip was not found in latest go2rtc release."
    }

    $zip = Join-Path $artifactRoot "go2rtc_win64.zip"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers @{ "User-Agent" = "BossCamSuite" }
    Expand-Archive -LiteralPath $zip -DestinationPath $artifactRoot -Force
}

$main = "bubble://$Username`:$Password@$Ip`:$HttpPort/bubble/live?ch=0&stream=0"
$sub = "bubble://$Username`:$Password@$Ip`:$HttpPort/bubble/live?ch=0&stream=1"
$prefix = Get-StreamPrefix $Ip
$allIps = @($KnownIps + $Ip + $LegacyAliasIp) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
$streamLines = foreach ($candidateIp in $allIps) {
    $candidatePrefix = Get-StreamPrefix $candidateIp
    "  ${candidatePrefix}_main: bubble://$Username`:$Password@$candidateIp`:$HttpPort/bubble/live?ch=0&stream=0"
    "  ${candidatePrefix}_sub: bubble://$Username`:$Password@$candidateIp`:$HttpPort/bubble/live?ch=0&stream=1"
}
$streamLines += "  5523w_main: bubble://$Username`:$Password@$LegacyAliasIp`:$HttpPort/bubble/live?ch=0&stream=0"
$streamLines += "  5523w_sub: bubble://$Username`:$Password@$LegacyAliasIp`:$HttpPort/bubble/live?ch=0&stream=1"

@"
api:
  listen: 127.0.0.1:$ApiPort
rtsp:
  listen: 127.0.0.1:$RtspPort
streams:
$($streamLines -join "`n")
log:
  level: debug
"@.TrimEnd() | Set-Content -LiteralPath $config -Encoding UTF8

$existing = Get-Process go2rtc -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $go2rtc }
if ($existing) {
    Stop-ExistingRelay
}

if (-not (Get-Process go2rtc -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $go2rtc })) {
    $process = Start-Process -FilePath $go2rtc -ArgumentList @("-config", $config) -WorkingDirectory $artifactRoot -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    $process.Id | Set-Content -LiteralPath $pidFile -Encoding ASCII
}

$listening = $false
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    Start-Sleep -Milliseconds 500
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync("127.0.0.1", $ApiPort)
        if ($connect.Wait(1000) -and $client.Connected) {
            $rtspClient = [Net.Sockets.TcpClient]::new()
            try {
                $rtspConnect = $rtspClient.ConnectAsync("127.0.0.1", $RtspPort)
                if ($rtspConnect.Wait(1000) -and $rtspClient.Connected) {
                    $listening = $true
                    break
                }
            }
            finally {
                $rtspClient.Dispose()
            }
        }
    }
    finally {
        $client.Dispose()
    }
}

if (-not $listening) {
    $logTail = Get-Content -LiteralPath $stdout -Tail 80 -ErrorAction SilentlyContinue
    $errTail = Get-Content -LiteralPath $stderr -Tail 80 -ErrorAction SilentlyContinue
    throw "go2rtc did not become ready on API port $ApiPort.`nSTDOUT:`n$logTail`nSTDERR:`n$errTail"
}

$mainOut = Join-Path $artifactRoot "$prefix-main.ffprobe.json"
$mainErr = Join-Path $artifactRoot "$prefix-main.ffprobe.err.txt"
$subOut = Join-Path $artifactRoot "$prefix-sub.ffprobe.json"
$subErr = Join-Path $artifactRoot "$prefix-sub.ffprobe.err.txt"

for ($attempt = 0; $attempt -lt 3; $attempt++) {
    & ffprobe -v error -rtsp_transport tcp -timeout 10000000 -analyzeduration 10000000 -probesize 10000000 -show_entries stream=codec_name,width,height,avg_frame_rate -of json "rtsp://127.0.0.1:$RtspPort/${prefix}_main" > $mainOut 2> $mainErr
    $mainExit = $LASTEXITCODE
    $mainRaw = Get-Content -Raw -LiteralPath $mainOut -ErrorAction SilentlyContinue
    if ($mainExit -eq 0 -and $mainRaw -match "codec_name") { break }
    Start-Sleep -Seconds 2
}

for ($attempt = 0; $attempt -lt 3; $attempt++) {
    & ffprobe -v error -rtsp_transport tcp -timeout 10000000 -analyzeduration 10000000 -probesize 10000000 -show_entries stream=codec_name,width,height,avg_frame_rate -of json "rtsp://127.0.0.1:$RtspPort/${prefix}_sub" > $subOut 2> $subErr
    $subExit = $LASTEXITCODE
    $subRaw = Get-Content -Raw -LiteralPath $subOut -ErrorAction SilentlyContinue
    if ($subExit -eq 0 -and $subRaw -match "codec_name") { break }
    Start-Sleep -Seconds 2
}

$mainJson = Get-Content -Raw -LiteralPath $mainOut | ConvertFrom-Json
$subJson = Get-Content -Raw -LiteralPath $subOut | ConvertFrom-Json
$mainStream = @($mainJson.streams | Where-Object { $_.codec_name -match "h264|h265|hevc" })[0]
$subStream = @($subJson.streams | Where-Object { $_.codec_name -match "h264|h265|hevc" })[0]

$pass = $mainExit -eq 0 -and $mainStream.width -eq 2560 -and $mainStream.height -eq 1920 -and $subExit -eq 0 -and $subStream.width -eq 704 -and $subStream.height -eq 480
$summary = [ordered]@{
    result = if ($pass) { "PASS_HIGHRES_BUBBLE_RELAY" } else { "FAIL_BUBBLE_RELAY" }
    ip = $Ip
    upstreamMain = $main
    upstreamSub = $sub
    streamPrefix = $prefix
    rtspMain = "rtsp://127.0.0.1:$RtspPort/${prefix}_main"
    rtspSub = "rtsp://127.0.0.1:$RtspPort/${prefix}_sub"
    mainCodec = $mainStream.codec_name
    mainWidth = $mainStream.width
    mainHeight = $mainStream.height
    mainFps = $mainStream.avg_frame_rate
    subCodec = $subStream.codec_name
    subWidth = $subStream.width
    subHeight = $subStream.height
    subFps = $subStream.avg_frame_rate
    artifactRoot = $artifactRoot
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifactRoot "bubble-relay-summary-$prefix.json") -Encoding UTF8
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifactRoot "bubble-relay-summary.json") -Encoding UTF8
$summary | ConvertTo-Json -Depth 8

if (-not $pass) {
    exit 1
}
