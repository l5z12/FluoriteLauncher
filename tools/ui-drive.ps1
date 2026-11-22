<#
.SYNOPSIS
  Drives the launcher UI via UI Automation for smoke checks: invoke elements by accessible name and capture screenshots.
  Run with Windows PowerShell 5.1 (powershell.exe), which ships the UIAutomationClient assemblies.
.EXAMPLE
  powershell -File tools/ui-drive.ps1 -Exe <path> -Steps "invoke:Get started","wait:2","capture:C:\tmp\home.png"
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string[]]$Steps,
    [string]$Arguments = "",
    [switch]$KeepRunning
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class UiDriveWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@
[UiDriveWin32]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
$Steps = @($Steps | ForEach-Object { $_ -split ';' } | Where-Object { $_ })

$p = if ($Arguments) { Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru } else { Start-Process -FilePath $Exe -PassThru }
$deadline = (Get-Date).AddSeconds(30)
while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline -and -not $p.HasExited) { Start-Sleep -Milliseconds 250; $p.Refresh() }
if ($p.HasExited) { Write-Error "Process exited early with code $($p.ExitCode)"; exit 1 }
Start-Sleep -Seconds 3

function Find-ByName([string]$name, [int]$timeoutSec = 10) {
    # "<name>#dialog" only matches inside a popup (an open ContentDialog), not the page behind it.
    $inDialog = $name.EndsWith('#dialog')
    if ($inDialog) { $name = $name.Substring(0, $name.Length - 7) }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $until = (Get-Date).AddSeconds($timeoutSec)
    do {
        if ($inDialog) {
            $el = $null
            foreach ($candidate in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
                $a = $walker.GetParent($candidate)
                for ($i = 0; $i -lt 12 -and $a -and -not $el; $i++) {
                    if ($a.Current.ClassName -eq 'Popup') { $el = $candidate }
                    $a = $walker.GetParent($a)
                }
                if ($el) { break }
            }
        }
        else { $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) }
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $until)
    return $null
}

function Capture([string]$out) {
    $h = $p.MainWindowHandle
    $r = New-Object UiDriveWin32+RECT
    [UiDriveWin32]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [UiDriveWin32]::PrintWindow($h, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Host "captured -> $out"
}

$failed = $false
foreach ($step in $Steps) {
    $kind, $arg = $step -split ':', 2
    switch ($kind) {
        'wait' { Start-Sleep -Seconds ([double]$arg) }
        'capture' { Capture $arg }
        'invoke' {
            $el = Find-ByName $arg
            if (-not $el) { Write-Warning "element '$arg' not found"; $failed = $true; break }
            $pattern = $null
            # Text inside an icon+text button matches first; walk up to the invokable ancestor.
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $probe = $el
            for ($i = 0; $i -lt 4 -and $probe; $i++) {
                if ($probe.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $el = $probe; break }
                if ($probe.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $el = $probe; break }
                $probe = $walker.GetParent($probe)
            }
            $pattern = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
            elseif ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
            else {
                # Some controls (CommunityToolkit Segmented items) expose no patterns: click their centre.
                $r = $el.Current.BoundingRectangle
                if ($r.Width -gt 0) {
                    [UiDriveWin32]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
                    [UiDriveWin32]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
                    [UiDriveWin32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); [UiDriveWin32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
                }
                else { Write-Warning "element '$arg' has no invoke/select pattern"; $failed = $true }
            }
            Write-Host "invoked '$arg'"
        }
        'exec' { Invoke-Expression $arg }
        'key' {
            # key:<SendKeys string>, e.g. key:{ENTER} — to the launcher window (dialogs' default buttons).
            Add-Type -AssemblyName System.Windows.Forms
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
            try { $root.SetFocus() } catch { }
            [System.Windows.Forms.SendKeys]::SendWait($arg)
            Write-Host "sent keys '$arg'"
        }
        'type' {
            # type:<element name>=<text> — sets an edit control's value.
            $name, $text = $arg -split '=', 2
            $el = Find-ByName $name
            $vp = $null
            # Composite controls (AutoSuggestBox) carry the name; their inner Edit carries the value.
            if ($el -and -not $el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
                $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
                $inner = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCond)
                if ($inner) { [void]$inner.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp) }
            }
            if ($vp) { $vp.SetValue($text); Write-Host "typed into '$name'" }
            else { Write-Warning "edit '$name' not found"; $failed = $true }
        }
        'select' {
            # select:<combo name>=<item name> — expands a ComboBox and selects the item. A trailing * matches a prefix.
            $name, $item = $arg -split '=', 2
            $combo = Find-ByName $name
            if (-not $combo) { Write-Warning "combo '$name' not found"; $failed = $true; break }
            $ec = $null
            if ($combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$ec)) { $ec.Expand(); Start-Sleep -Milliseconds 600 }
            $itemCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
            $target = $null
            $until = (Get-Date).AddSeconds(15)
            do {
                foreach ($scope in @($combo, [System.Windows.Automation.AutomationElement]::RootElement)) {
                    foreach ($candidate in $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)) {
                        if ($candidate.Current.Name -like $item) { $target = $candidate; break }
                    }
                    if ($target) { break }
                }
                if (-not $target) { Start-Sleep -Milliseconds 500 }
            } while (-not $target -and (Get-Date) -lt $until)
            $sp = $null
            if ($target -and $target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Select(); Write-Host "selected '$item' in '$name'" }
            else { Write-Warning "item '$item' not found in '$name'"; $failed = $true }
            if ($ec) { try { $ec.Collapse() } catch { } }
        }
        'count' {
            # count:<name>=<expected> — number of Button elements with that accessible name.
            $name, $expected = $arg -split '=', 2
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
            $cond = New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
            $n = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond).Count
            Write-Host "count '$name' = $n (expected $expected)"
            if ("$n" -ne $expected) { $failed = $true }
        }
        'visible' {
            $v = [UiDriveWin32]::IsWindowVisible($p.MainWindowHandle)
            Write-Host "window visible: $v (expected $arg)"
            if ("$v" -ne $arg) { $failed = $true }
        }
        'expect' {
            if (Find-ByName $arg 5) { Write-Host "found '$arg'" } else { Write-Warning "expected '$arg' not found"; $failed = $true }
        }
    }
}

if (-not $KeepRunning) { Stop-Process -Id $p.Id -Force }
if ($failed) { exit 1 }
