# Clicks through WaveFix's sidebar tabs with UI Automation and saves a screenshot of each.
# Usage: screenshot-tabs.ps1 -OutDir folder
param([string]$OutDir = $env:TEMP)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process WaveFix -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = [Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
foreach ($tab in 'Microphone', 'Voice changer', 'Soundboard', 'App mixer', 'Plugins', 'Shortcuts') {
  $cond = New-Object Windows.Automation.AndCondition(
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::RadioButton)),
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $tab)))
  $el = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
  if (-not $el) { "tab '$tab' not found"; continue }
  $el.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 600
  $file = Join-Path $OutDir ("wavefix-" + ($tab -replace ' ', '-').ToLower() + ".png")
  & (Join-Path $PSScriptRoot 'screenshot-window.ps1') -Out $file
}
