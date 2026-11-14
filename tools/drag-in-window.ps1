# Test helper: brings a window to the front and drags the mouse inside it (window-relative pixels).
# Usage: drag-in-window.ps1 -Title "Noise removal settings" -FromX 295 -ToX 360 -Y 189
param([string]$Title, [int]$FromX, [int]$ToX, [int]$Y)
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class DragWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
$p = Get-Process | Where-Object MainWindowTitle -eq $Title | Select-Object -First 1
if (-not $p) { "window not found: $Title"; exit 1 }
$h = $p.MainWindowHandle
[DragWin]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 400
if ([DragWin]::GetForegroundWindow() -ne $h) { "window isn't in front; not dragging (would click the wrong window)"; exit 1 }
$r = New-Object DragWin+RECT; [DragWin]::GetWindowRect($h, [ref]$r) | Out-Null
$y = $r.T + $Y
[DragWin]::SetCursorPos($r.L + $FromX, $y) | Out-Null; Start-Sleep -Milliseconds 150
[DragWin]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero)
$step = if ($ToX -ge $FromX) { 6 } else { -6 }
for ($x = $FromX; [Math]::Abs($x - $ToX) -ge 6; $x += $step) { [DragWin]::SetCursorPos($r.L + $x, $y) | Out-Null; Start-Sleep -Milliseconds 15 }
[DragWin]::SetCursorPos($r.L + $ToX, $y) | Out-Null
[DragWin]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
"dragged in '$Title' from x=$FromX to x=$ToX at y=$Y"
