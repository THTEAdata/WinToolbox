param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,

    [switch]$SkipBuild,
    [switch]$Commit,
    [switch]$Tag,
    [switch]$Push,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.2.3 (got '$Version')"
}

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# 1) bump <Version> in the csproj (single source of truth)
Write-Step "Set version $Version in WinToolbox.csproj"
$csproj = Join-Path $root "WinToolbox.csproj"
$xml = Get-Content $csproj -Raw
if ($xml -notmatch '<Version>[^<]+</Version>') {
    throw "Could not find <Version> element in WinToolbox.csproj"
}
$updated = [regex]::Replace($xml, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
if ($DryRun) {
    Write-Host "    (dry run) would update WinToolbox.csproj"
} else {
    [System.IO.File]::WriteAllText($csproj, $updated, (New-Object System.Text.UTF8Encoding($false)))
}

# 2) self-contained publish
if (-not $SkipBuild) {
    Write-Step "Publish self-contained build (build.ps1)"
    if ($DryRun) {
        Write-Host "    (dry run) build.ps1"
    } else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build.ps1")
        if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed with exit code $LASTEXITCODE" }
    }
}

# 3) MSI + portable zip
Write-Step "Build installer and portable zip"
if ($DryRun) {
    Write-Host "    (dry run) installer\build-installer.ps1 -Version $Version"
} else {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "installer\build-installer.ps1") -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "installer build failed with exit code $LASTEXITCODE" }
}

# 4) summary
Write-Step "Artifacts"
$dist = Join-Path $root "dist"
if (Test-Path $dist) {
    Get-ChildItem $dist -File |
        Select-Object Name, @{N = "SizeMB"; E = { [math]::Round($_.Length / 1MB, 1) } } |
        Format-Table -AutoSize
}

# 5) optional git steps
if ($Commit) {
    Write-Step "git commit"
    if ($DryRun) {
        Write-Host "    (dry run) git add WinToolbox.csproj CHANGELOG.md; git commit"
    } else {
        & git -C $root add WinToolbox.csproj CHANGELOG.md
        & git -C $root commit -m "chore: release v$Version"
    }
}

if ($Tag) {
    Write-Step "git tag v$Version"
    if ($DryRun) {
        Write-Host "    (dry run) git tag v$Version"
    } else {
        & git -C $root tag "v$Version"
    }
}

if ($Push) {
    Write-Step "git push (branch + tags)"
    if ($DryRun) {
        Write-Host "    (dry run) git push; git push --tags"
    } else {
        & git -C $root push
        & git -C $root push --tags
    }
}

Write-Host ""
Write-Host "Done: v$Version" -ForegroundColor Green
if (-not $DryRun) {
    Write-Host "Next: update CHANGELOG.md, review dist\, then push the tag so CI publishes the release."
}
