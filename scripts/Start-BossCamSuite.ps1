param(
    [switch]$NoBuild,
    [switch]$SkipService,
    [switch]$SkipDesktop,
    [switch]$RunProbe,
    [string]$ProbeIps = "10.0.0.4,10.0.0.29,10.0.0.227",
    [string]$HealthUrl = "http://127.0.0.1:5317/api/health",
    [int]$HealthTimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"

function Assert-CommandExists {
    param([string]$CommandName)
    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "Required command not found: $CommandName"
    }
}

function Wait-ForHealth {
    param(
        [string]$Url,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec 5
            if ($null -ne $response) {
                return $true
            }
        }
        catch {
            Start-Sleep -Milliseconds 800
        }
    }

    return $false
}

Assert-CommandExists "dotnet"

$repoRoot = Split-Path -Parent $PSScriptRoot
$serviceProject = Join-Path $repoRoot "src\BossCam.Service\BossCam.Service.csproj"
$desktopProject = Join-Path $repoRoot "src\BossCam.Desktop\BossCam.Desktop.csproj"
$probeProject = Join-Path $repoRoot "src\BossCam.ProbeRunner\BossCam.ProbeRunner.csproj"
$solutionPath = Join-Path $repoRoot "BossCamSuite.sln"

$runtimeDir = Join-Path $repoRoot "artifacts\runtime"
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

Push-Location $repoRoot
try {
    if (-not $NoBuild) {
        Write-Host "Building solution..."
        dotnet build $solutionPath --nologo
    }

    $serviceProc = $null
    if (-not $SkipService) {
        if (Wait-ForHealth -Url $HealthUrl -TimeoutSeconds 2) {
            Write-Host "Service already healthy at $HealthUrl (using existing instance)."
        }
        else {
        $serviceOutLog = Join-Path $runtimeDir "service-$stamp.out.log"
        $serviceErrLog = Join-Path $runtimeDir "service-$stamp.err.log"
        Write-Host "Starting service..."
        $serviceArgs = @("run", "--project", $serviceProject)
        if ($NoBuild) {
            $serviceArgs += "--no-build"
        }
        $serviceProc = Start-Process -FilePath "dotnet" -ArgumentList $serviceArgs -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $serviceOutLog -RedirectStandardError $serviceErrLog

        Write-Host "Waiting for service health: $HealthUrl"
        if (-not (Wait-ForHealth -Url $HealthUrl -TimeoutSeconds $HealthTimeoutSeconds)) {
            throw "Service did not become healthy within $HealthTimeoutSeconds seconds. Check logs: $serviceOutLog and $serviceErrLog"
        }
        Write-Host "Service healthy (PID $($serviceProc.Id)). Logs: $serviceOutLog ; $serviceErrLog"
        }
    }

    if ($RunProbe) {
        Write-Host "Running probe against: $ProbeIps"
        $probeArgs = @("run", "--project", $probeProject, "--", "--mode", "SafeReadOnly", "--device-ips", $ProbeIps, "--resume", "true", "--include-persistence", "false", "--export-dir", (Join-Path $repoRoot "artifacts"), "--export-summary", (Join-Path $repoRoot "artifacts\probe-summary-latest.json"))
        if ($NoBuild) {
            $probeArgs.Insert(4, "--no-build")
        }
        dotnet @probeArgs
    }

    if (-not $SkipDesktop) {
        $desktopOutLog = Join-Path $runtimeDir "desktop-$stamp.out.log"
        $desktopErrLog = Join-Path $runtimeDir "desktop-$stamp.err.log"
        Write-Host "Starting desktop app..."
        $desktopArgs = @("run", "--project", $desktopProject)
        if ($NoBuild) {
            $desktopArgs += "--no-build"
        }
        $desktopProc = Start-Process -FilePath "dotnet" -ArgumentList $desktopArgs -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $desktopOutLog -RedirectStandardError $desktopErrLog
        Start-Sleep -Seconds 2
        if ($desktopProc.HasExited) {
            $desktopErrorTail = ""
            if (Test-Path $desktopErrLog) {
                $desktopErrorTail = (Get-Content $desktopErrLog -Tail 40 -ErrorAction SilentlyContinue) -join [Environment]::NewLine
            }
            throw "Desktop failed to stay running. Check logs: $desktopOutLog ; $desktopErrLog`n$desktopErrorTail"
        }
        Write-Host "Desktop started (PID $($desktopProc.Id)). Logs: $desktopOutLog ; $desktopErrLog"
    }

    Write-Host ""
    Write-Host "BossCamSuite launch complete."
    if ($serviceProc) {
        Write-Host "To stop service: Stop-Process -Id $($serviceProc.Id)"
    }
}
finally {
    Pop-Location
}
