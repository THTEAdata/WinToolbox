param(
    [Parameter(Mandatory = $true)]
    [string]$Repo,                       # owner/name, e.g. someuser/WinToolbox

    [string]$Version = "1.0.0",
    [string]$Token = $env:GITHUB_TOKEN,

    [switch]$CreateRepo,                 # create the repo via API when it does not exist
    [switch]$Release,                    # create a GitHub Release and upload dist assets
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not (Test-Path (Join-Path $root "WinToolbox.csproj"))) {
    $root = Split-Path -Parent $PSScriptRoot
}
if (-not (Test-Path (Join-Path $root "WinToolbox.csproj"))) {
    throw "Run this script from inside the WinToolbox repository."
}

function Write-Step([string]$m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok([string]$m) { Write-Host "    $m" -ForegroundColor Green }
function Write-Warn2([string]$m) { Write-Host "    $m" -ForegroundColor Yellow }

$hasToken = -not [string]::IsNullOrWhiteSpace($Token)
$git = "git"

# ---------------------------------------------------------------------------
# 1) local repo
# ---------------------------------------------------------------------------
Write-Step "Local repository"
if (-not (Test-Path (Join-Path $root ".git"))) {
    if ($DryRun) { Write-Warn2 "(dry run) git init -b main" }
    else {
        & $git -C $root init -b main | Out-Null
        & $git -C $root config user.name "WinToolbox"
        & $git -C $root config user.email "wintoolbox@users.noreply.github.com"
        Write-Ok "initialized (change author later with: git commit --amend --reset-author)"
    }
} else {
    Write-Ok "already initialized"
}

$status = & $git -C $root status --porcelain
if ([string]::IsNullOrWhiteSpace(($status -join ""))) {
    Write-Ok "working tree clean"
} else {
    if ($DryRun) { Write-Warn2 "(dry run) git add -A; git commit" }
    else {
        & $git -C $root add -A
        & $git -C $root commit -m "release: v$Version" | Out-Null
        Write-Ok "committed"
    }
}

# ---------------------------------------------------------------------------
# 2) remote
# ---------------------------------------------------------------------------
$url = "https://github.com/$Repo.git"
Write-Step "Remote -> $url"
$existing = & $git -C $root remote 2>$null
if (-not $DryRun) {
    if ($existing -contains "origin") { & $git -C $root remote set-url origin $url }
    else { & $git -C $root remote add origin $url }
}
Write-Ok "origin configured"

# ---------------------------------------------------------------------------
# 3) optional: create repository through the API
# ---------------------------------------------------------------------------
if ($CreateRepo) {
    Write-Step "Create repository $Repo"
    if (-not $hasToken) { Write-Warn2 "no GITHUB_TOKEN, skipping (create it on github.com manually)" }
    elseif ($DryRun) { Write-Warn2 "(dry run) POST /user/repos" }
    else {
        $name = $Repo.Split('/')[-1]
        $body = @{
            name        = $name
            description = "WinToolbox - native Windows 10/11 toolbox (C#/WPF)"
            private     = $false
            has_issues  = $true
        } | ConvertTo-Json -Compress
        try {
            Invoke-RestMethod -Method Post -Uri "https://api.github.com/user/repos" `
                -Headers @{ Authorization = "token $Token"; "User-Agent" = "WinToolbox" } `
                -Body $body -ContentType "application/json" | Out-Null
            Write-Ok "created"
        } catch {
            Write-Warn2 "API said: $($_.Exception.Message) (likely already exists, continuing)"
        }
    }
}

# ---------------------------------------------------------------------------
# 4) push
# ---------------------------------------------------------------------------
Write-Step "Push to GitHub"
if ($DryRun) {
    Write-Warn2 "(dry run) git push -u origin main"
} else {
    if ($hasToken) {
        # keep the token out of .git/config: pass it as a one-shot header
        $pair = "x-access-token:$Token"
        $b64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($pair))
        & $git -C $root -c "http.extraheader=AUTHORIZATION: basic $b64" push -u origin main
    } else {
        & $git -C $root push -u origin main
    }
    if ($LASTEXITCODE -ne 0) { throw "git push failed with exit code $LASTEXITCODE" }
    Write-Ok "pushed"
}

# ---------------------------------------------------------------------------
# 5) optional: release + assets
# ---------------------------------------------------------------------------
if ($Release) {
    Write-Step "Create release v$Version"
    if (-not $hasToken) { Write-Warn2 "no GITHUB_TOKEN, skipping release" }
    elseif ($DryRun) { Write-Warn2 "(dry run) POST /repos/$Repo/releases + upload assets" }
    else {
        $tag = "v$Version"
        $headers = @{ Authorization = "token $Token"; "User-Agent" = "WinToolbox"; Accept = "application/vnd.github+json" }
        $payload = @{
            tag_name = $tag
            name     = "WinToolbox $tag"
            body     = "See CHANGELOG.md for details."
            draft    = $false
        } | ConvertTo-Json -Compress

        $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" `
            -Headers $headers -Body $payload -ContentType "application/json"
        Write-Ok "release created: $($release.html_url)"

        $dist = Join-Path $root "dist"
        if (Test-Path $dist) {
            foreach ($file in Get-ChildItem $dist -File -Include *.msi, *.zip) {
                $uploadUrl = "https://uploads.github.com/repos/$Repo/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($file.Name))"
                Invoke-RestMethod -Method Post -Uri $uploadUrl -Headers $headers `
                    -Body ([IO.File]::ReadAllBytes($file.FullName)) -ContentType "application/octet-stream" | Out-Null
                Write-Ok "uploaded $($file.Name)"
            }
        }
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
if (-not $hasToken) {
    Write-Host "Tip: set a token for one session only, then remove it:  `$env:GITHUB_TOKEN = '<token>'  ...  Remove-Item Env:GITHUB_TOKEN"
}
