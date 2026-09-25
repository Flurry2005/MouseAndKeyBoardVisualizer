# Install / uninstall lifecycle test for the virtual camera. Must run elevated (msiexec per-machine).
#   1. remove any development registration (--camera-uninstall-elevated)
#   2. install the MSI, verify registration + enumeration + a consumer read
#   3. uninstall the MSI, verify that no camera device, COM registration or file remains
#   4. install the MSI again (leaves the product installed), verify
# Writes a report to -Report (default: %TEMP%\msv-lifecycle.txt).
param(
    [string]$Msi = (Get-ChildItem (Join-Path $PSScriptRoot 'bin\x64\Release') -Filter 'MouseSwipeVisualizer-*-x64.msi' | Sort-Object LastWriteTime | Select-Object -Last 1).FullName,
    [string]$DevExe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\MouseSwipeVisualizer\bin\Release\net9.0-windows\MouseSwipeVisualizer.exe'),
    [string]$Report = (Join-Path $env:TEMP 'msv-lifecycle.txt'),
    [switch]$SkipFinalInstall
)

$ErrorActionPreference = 'Continue'
$installDir = Join-Path $env:ProgramFiles 'MouseSwipeVisualizer'
$installedExe = Join-Path $installDir 'MouseSwipeVisualizer.exe'
$clsidKey = 'HKLM:\SOFTWARE\Classes\CLSID\{0728D89A-2065-4F45-85F7-B128DA227817}'
$lines = New-Object System.Collections.Generic.List[string]
function Log([string]$text) { $lines.Add($text); Write-Host $text }

function Run-Exe([string]$exe, [string[]]$arguments) {
    $out = Join-Path $env:TEMP ("msv-" + [guid]::NewGuid().ToString('N') + '.txt')
    $p = Start-Process $exe -ArgumentList $arguments -RedirectStandardOutput $out -Wait -PassThru -NoNewWindow
    $text = if (Test-Path $out) { Get-Content $out -Raw } else { '' }
    Remove-Item $out -ErrorAction SilentlyContinue
    return @{ Code = $p.ExitCode; Output = $text }
}

function State([string]$title, [string]$exe) {
    Log "---- $title"
    $devices = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'SWD\VCAMDEVAPI\*' })
    Log ("camera device nodes (SWD\VCAMDEVAPI): {0}" -f $devices.Count)
    foreach ($d in $devices) { Log ("   {0} {1} {2}" -f $d.Status, $d.FriendlyName, $d.InstanceId) }
    Log ("COM registration present: {0}" -f (Test-Path $clsidKey))
    if (Test-Path "$clsidKey\InprocServer32") { Log ("   -> {0}" -f (Get-ItemProperty "$clsidKey\InprocServer32").'(default)') }
    Log ("install folder present: {0}" -f (Test-Path $installDir))
    if (Test-Path $installDir) { Get-ChildItem $installDir -Recurse -File | ForEach-Object { Log ("   {0}" -f $_.FullName) } }
    if ($exe -and (Test-Path $exe)) {
        $status = Run-Exe $exe @('--camera-status')
        foreach ($l in ($status.Output -split "`r?`n")) { if ($l -match 'registered|enumerable|friendly name|Status:') { Log "   $l" } }
    }
}

Log "Lifecycle test $(Get-Date -Format s)  MSI: $Msi"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Log "ERROR: run elevated"; $lines | Set-Content $Report; exit 5 }

Log "== 1. remove development registration"
$r = Run-Exe $DevExe @('--camera-uninstall-elevated'); Log $r.Output
State 'after removing the development registration' $DevExe

Log "== 2. MSI install"
$p = Start-Process msiexec -ArgumentList @('/i', "`"$Msi`"", '/qn', '/l*v', "`"$env:TEMP\msv-install.log`"") -Wait -PassThru
Log "msiexec /i exit code: $($p.ExitCode)"
State 'after MSI install' $installedExe
$t = Run-Exe $installedExe @('--camera-test', '--frames', '60'); Log ("camera test (installed exe): exit {0}" -f $t.Code); Log $t.Output

Log "== 3. MSI uninstall"
$p = Start-Process msiexec -ArgumentList @('/x', "`"$Msi`"", '/qn', '/l*v', "`"$env:TEMP\msv-uninstall.log`"") -Wait -PassThru
Log "msiexec /x exit code: $($p.ExitCode)"
State 'after MSI uninstall' $DevExe
$left = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'SWD\VCAMDEVAPI\*' }).Count
Log ("RESULT uninstall clean: {0}" -f (($left -eq 0) -and -not (Test-Path $clsidKey) -and -not (Test-Path $installedExe)))

if (-not $SkipFinalInstall) {
    Log "== 4. MSI install again (left installed)"
    $p = Start-Process msiexec -ArgumentList @('/i', "`"$Msi`"", '/qn', '/l*v', "`"$env:TEMP\msv-install2.log`"") -Wait -PassThru
    Log "msiexec /i exit code: $($p.ExitCode)"
    State 'final state' $installedExe
}

$lines | Set-Content $Report -Encoding UTF8
