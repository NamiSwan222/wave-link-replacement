# Runs one of the tools in this folder once as SYSTEM via a temporary scheduled task, then
# deletes the task and copies its log to C:\ProgramData\wavefix-<tool>.log.
# Run from an elevated PowerShell. Example: run-as-system.ps1 -Tool set-mic-effect-slot [-Undo]
param([string]$Tool = 'set-mic-effect-slot', [switch]$Undo)
$script = Join-Path $PSScriptRoot "$Tool.ps1"
$log = "C:\ProgramData\wavefix-$Tool.log"
Remove-Item $log -ErrorAction SilentlyContinue
$argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -Log `"$log`"" + $(if ($Undo) { ' -Undo' } else { '' })
$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argLine
Register-ScheduledTask -TaskName 'WaveFixSystemTool' -Action $a -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
Start-ScheduledTask -TaskName 'WaveFixSystemTool'
Start-Sleep 2
for ($i = 0; $i -lt 30 -and (Get-ScheduledTask -TaskName 'WaveFixSystemTool').State -eq 'Running'; $i++) { Start-Sleep 1 }
Unregister-ScheduledTask -TaskName 'WaveFixSystemTool' -Confirm:$false
