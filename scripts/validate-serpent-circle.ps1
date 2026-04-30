param(
    [switch]$SkipDotnet
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$message) {
    $failures.Add($message) | Out-Null
}

if (-not $SkipDotnet) {
    dotnet build BossCamSuite.sln
    dotnet test BossCamSuite.sln
}

$sourceFiles = Get-ChildItem -Path src,docs,.codex,.github -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\|TestResults' -and $_.FullName -notmatch 'CameraEndpointTruthService.cs$|VideoTransportAdapters.cs$' }

$text = foreach ($file in $sourceFiles) {
    [pscustomobject]@{ Path = $file.FullName; Text = Get-Content -Raw -LiteralPath $file.FullName }
}

foreach ($item in $text) {
    $relative = Resolve-Path -Relative $item.Path
    if ($item.Text -match '\.264.*(=>|=|:).*h264' -or $item.Text -match 'h264.*(from|because).*\.264') {
        Add-Failure "Possible codec inference from .264 suffix: $relative"
    }
    if ($item.Text -match 'Declared.*Encoding.*Playback.*Codec' -and $item.Text -notmatch 'separate|mismatch|truth') {
        Add-Failure "Possible ONVIF declared encoding collapsed into playback codec: $relative"
    }
    if ($item.Text -match 'ServiceState.*Verified.*MovementControlsEnabled.*true') {
        Add-Failure "Possible PTZ service auto-enables mechanical movement: $relative"
    }
    if ($item.Text -match 'global.*endpoint.*verified|model.*endpoint.*verified') {
        Add-Failure "Possible global/model endpoint map treated as verified truth: $relative"
    }
    if ($item.Text -match 'live verified|live-verified' -and $item.Text -notmatch 'unless live|without live|Do not claim|actually ran|probe') {
        Add-Failure "Possible live verification claim without evidence guard: $relative"
    }
}

$requiredFiles = @(
    "SERPENT_CIRCLE.md",
    "CONTRIBUTING.md",
    ".codex/SKILL.md",
    ".codex/SERPENT_CIRCLE_SKILL.md",
    ".codex/prompts/BOSSCAM_SERPENT_CIRCLE_HEADER.md",
    ".codex/prompts/BOSSCAM_FAILURE_REPORT_TEMPLATE.md",
    "CODEX_SERPENT_CIRCLE_PROMPT.md",
    "AGENT_HANDOFF_SERPENT_CIRCLE.md",
    ".github/pull_request_template.md"
)

foreach ($required in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $required))) {
        Add-Failure "Missing required Serpent Circle surface: $required"
    }
}

if ($failures.Count -gt 0) {
    Write-Host "Serpent Circle validation failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "Serpent Circle validation passed." -ForegroundColor Green
