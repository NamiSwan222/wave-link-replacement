# Clicks WaveFix controls by their accessible name (tabs, buttons) via UI Automation.
# Usage: ui-click.ps1 -Names "Plugins","Scan this PC for plugins" [-DelayMs 800]
param([string[]]$Names, [int]$DelayMs = 800)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process WaveFix -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = [Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
foreach ($n in $Names) {
  $el = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $n)))
  if (-not $el) { "not found: $n"; continue }
  $pat = $null
  if ($el.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke() }
  elseif ($el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select() }
  elseif ($el.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { $pat.Toggle() }
  else { "no click pattern: $n"; continue }
  "clicked: $n"
  Start-Sleep -Milliseconds $DelayMs
}
