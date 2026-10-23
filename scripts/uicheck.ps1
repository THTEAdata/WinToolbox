# Headless UI check: load the compiled WPF assembly, instantiate every page,
# apply its templates and report any XAML/resource error.
# Usage: powershell -STA -File scripts\uicheck.ps1 [-Assembly publish\WinToolbox.dll]
param(
    [string]$Assembly = "publish\WinToolbox.dll"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dll = Join-Path $root $Assembly
if (-not (Test-Path $dll)) { Write-Error "assembly not found: $dll"; exit 1 }

$asm = [System.Reflection.Assembly]::LoadFrom($dll)

# Build the application object so Application.Resources (Theme.xaml) is loaded.
$appType = $asm.GetType("WinToolbox.App")
$app = [System.Activator]::CreateInstance($appType)
$appType.GetMethod("InitializeComponent").Invoke($app, @())

$failed = 0
$dummy = New-Object System.Windows.Controls.Border
$size = New-Object System.Windows.Size(1200, 800)

function Check-View([string]$typeName) {
    try {
        $t = $asm.GetType($typeName)
        if (-not $t) { throw "type not found: $typeName" }
        $v = [System.Activator]::CreateInstance($t)
        $v.Measure($size)
        $v.Arrange((New-Object System.Windows.Rect(0, 0, 1200, 800)))
        $v.UpdateLayout()
        $count = 0
        if ($v -is [System.Windows.Controls.Panel]) { $count = $v.Children.Count }
        Write-Host ("PASS  {0}" -f $typeName) -ForegroundColor Green
        $v = $null
    }
    catch {
        $script:failed++
        $inner = $_.Exception
        while ($inner.InnerException) { $inner = $inner.InnerException }
        Write-Host ("FAIL  {0}: {1} - {2}" -f $typeName, $inner.GetType().Name, $inner.Message) -ForegroundColor Red
    }
}

Write-Host "== WinToolbox UI check ==" -ForegroundColor Cyan
Check-View "WinToolbox.MainWindow"
Check-View "WinToolbox.Views.SystemInfoView"
Check-View "WinToolbox.Views.CleanupView"
Check-View "WinToolbox.Views.ProcessView"
Check-View "WinToolbox.Views.ServiceView"
Check-View "WinToolbox.Views.NetworkView"

Write-Host ""
if ($failed -eq 0) {
    Write-Host "result: all views loaded" -ForegroundColor Green
    exit 0
}
Write-Host ("result: {0} view(s) failed" -f $failed) -ForegroundColor Red
exit 1
