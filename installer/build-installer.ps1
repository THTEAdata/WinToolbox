param(
    [string]$Version = "1.0.0",
    [switch]$SkipPortable
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "publish"
$installerDir = Join-Path $root "installer"
$dist = Join-Path $root "dist"

if (-not (Test-Path $publish)) {
    throw "publish folder not found. Run build.ps1 first (self-contained publish)."
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    throw "WiX not found. Install it with: dotnet tool install --global wix --version 5.0.2"
}

# ---------------------------------------------------------------------------
# 1) Generate Components.wxs from the publish folder
# ---------------------------------------------------------------------------
$files = Get-ChildItem $publish -Recurse -File | Sort-Object FullName
Write-Host ("Scanning publish folder: " + $files.Count + " files")

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')

$componentIds = New-Object System.Collections.Generic.List[string]
$index = 0
$groups = @{}
$rootFiles = New-Object System.Collections.Generic.List[object]

foreach ($f in $files) {
    $rel = $f.FullName.Substring($publish.Length).TrimStart('\')
    $dir = [System.IO.Path]::GetDirectoryName($rel)
    if ([string]::IsNullOrEmpty($dir)) {
        $rootFiles.Add($f)
    } else {
        if (-not $groups.ContainsKey($dir)) { $groups[$dir] = New-Object System.Collections.Generic.List[object] }
        $groups[$dir].Add($f)
    }
}

function Add-Component([object]$file, [string]$rel, [int]$i) {
    $src = "publish\" + $rel.Replace('/', '\')
    $id = "cmp_{0}" -f $i
    $fid = "fil_{0}" -f $i
    [void]$sb.AppendLine('        <Component Id="' + $id + '" Guid="*">')
    [void]$sb.AppendLine('          <File Id="' + $fid + '" Source="' + $src + '" KeyPath="yes" />')
    [void]$sb.AppendLine('        </Component>')
    return $id
}

foreach ($f in $rootFiles) {
    $id = Add-Component $f $f.Name $index
    $componentIds.Add($id)
    $index++
}

foreach ($dirName in ($groups.Keys | Sort-Object)) {
    $safe = ($dirName -replace '[^A-Za-z0-9]', '_')
    [void]$sb.AppendLine('      <Directory Id="dir_' + $safe + '" Name="' + $dirName + '">')
    foreach ($f in $groups[$dirName]) {
        $rel = $f.FullName.Substring($publish.Length).TrimStart('\')
        $id = Add-Component $f $rel $index
        $componentIds.Add($id)
        $index++
    }
    [void]$sb.AppendLine('      </Directory>')
}

[void]$sb.AppendLine('    </DirectoryRef>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="AppFiles">')
foreach ($id in $componentIds) { [void]$sb.AppendLine('      <ComponentRef Id="' + $id + '" />') }
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

$componentsPath = Join-Path $installerDir "Components.wxs"
[System.IO.File]::WriteAllText($componentsPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("Generated " + $componentsPath + " (" + $componentIds.Count + " components)")

# ---------------------------------------------------------------------------
# 2) Build MSI
# ---------------------------------------------------------------------------
$msiPath = Join-Path $dist ("WinToolbox-Setup-" + $Version + ".msi")
Write-Host "Building MSI ..."

& wix build (Join-Path $installerDir "Package.wxs") $componentsPath `
    -ext WixToolset.UI.wixext -arch x64 -d Version=$Version -o $msiPath

if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE" }

# ---------------------------------------------------------------------------
# 3) Portable ZIP
# ---------------------------------------------------------------------------
if (-not $SkipPortable) {
    $zipPath = Join-Path $dist ("WinToolbox-" + $Version + "-portable.zip")
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publish, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host ("Portable zip: " + $zipPath)
}

Write-Host ""
Write-Host "Done:"
Get-ChildItem $dist -File | Select-Object Name, @{N = "SizeMB"; E = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
