# Installs or upgrades the MSI (elevated). Stops the camera services first so the media source DLL
# is not in use, then reports the camera status. Usage: powershell -File upgrade-install.ps1 [-Report file]
param([string]$Report = (Join-Path $env:TEMP 'msv-upgrade.txt'))
$msi = (Get-ChildItem (Join-Path $PSScriptRoot 'bin\x64\Release') -Filter 'MouseSwipeVisualizer-*-x64.msi' | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
$out = New-Object System.Collections.Generic.List[string]
foreach ($s in 'FrameServer', 'FrameServerMonitor') { sc.exe stop $s | Out-Null }
Start-Sleep 2
$p = Start-Process msiexec -ArgumentList @('/i', "`"$msi`"", '/qn', '/l*v', "`"$env:TEMP\msv-upgrade.log`"") -Wait -PassThru
$out.Add("msiexec /i $msi -> exit $($p.ExitCode)")
$exe = Join-Path $env:ProgramFiles 'MouseSwipeVisualizer\MouseSwipeVisualizer.exe'
$status = Join-Path $env:TEMP 'msv-upgrade-status.txt'
Start-Process $exe -ArgumentList '--camera-status' -RedirectStandardOutput $status -Wait -NoNewWindow
$out.AddRange([string[]](Get-Content $status))
$out.Add(("installed exe version: {0}" -f (Get-Item $exe).VersionInfo.ProductVersion))
$out | Set-Content $Report -Encoding UTF8
