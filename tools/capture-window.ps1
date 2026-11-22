<#
.SYNOPSIS
  Starts an executable, waits for its main window, and saves a screenshot of it (UI smoke check).
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Out,
    [int]$WaitSeconds = 8,
    [string]$Arguments = "",
    [switch]$KeepRunning
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class FluoriteWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
"@

# Per-monitor v2 so window rectangles are physical pixels on scaled displays.
[FluoriteWin32]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
$p = if ($Arguments) { Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru } else { Start-Process -FilePath $Exe -PassThru }
$deadline = (Get-Date).AddSeconds(30)
while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline -and -not $p.HasExited) { Start-Sleep -Milliseconds 250; $p.Refresh() }
if ($p.HasExited) { Write-Error "Process exited early with code $($p.ExitCode)"; exit 1 }
Start-Sleep -Seconds $WaitSeconds
$p.Refresh()
if ($p.HasExited) { Write-Error "Process exited with code $($p.ExitCode)"; exit 1 }

$h = $p.MainWindowHandle
$r = New-Object FluoriteWin32+RECT
[FluoriteWin32]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT (2) captures DirectComposition/WinUI content even if the window is occluded.
[FluoriteWin32]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Host "Captured ${w}x${hgt} from pid $($p.Id) -> $Out"
if (-not $KeepRunning) { Stop-Process -Id $p.Id -Force }
