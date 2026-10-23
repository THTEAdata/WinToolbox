# WinToolbox build script (ASCII only: Windows PowerShell 5.1 parses .ps1 as ANSI)
#
# Default: SELF-CONTAINED folder publish -> target machines need NOTHING installed.
#   publish\WinToolbox.exe + ~240 runtime files (~130 MB). Ship the folder (or zip it).
#
# -FrameworkDependent : small build (~5 files) but requires .NET 10 Desktop Runtime.
# -SingleFile         : one exe; WPF single-file host was seen to exit silently on some
#                       machines, so the folder publish stays the default.

param(
    [switch]$FrameworkDependent,
    [switch]$SingleFile,
    [string]$Output = "publish"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "== build ==" -ForegroundColor Cyan
dotnet build -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

$sc = if ($FrameworkDependent) { "false" } else { "true" }
$single = if ($SingleFile) { "true" } else { "false" }
Write-Host "== publish (selfContained=$sc singleFile=$single) ==" -ForegroundColor Cyan

dotnet publish -c Release -r win-x64 --self-contained $sc `
    -p:PublishSingleFile=$single `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $root (Join-Path $Output "WinToolbox.exe")
if (-not (Test-Path $exe)) { throw "output exe not found: $exe" }

$files = Get-ChildItem $Output -Recurse -File
$totalMb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
Write-Host ""
Write-Host "OK: $exe ($($files.Count) files, $totalMb MB)" -ForegroundColor Green
if ($FrameworkDependent) {
    Write-Host "Note: framework-dependent build needs .NET 10 Desktop Runtime installed." -ForegroundColor DarkGray
} else {
    Write-Host "Portable: no .NET runtime required on target machines - copy the whole folder." -ForegroundColor Green
}
