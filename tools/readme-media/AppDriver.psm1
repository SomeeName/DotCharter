# Drives a DotCharter instance for the README shots: launch it against an isolated data root,
# put its window on the NON-primary monitor at a fixed size, press things by UI Automation name
# or by posted mouse messages, and read the window back with PrintWindow. Posted messages and
# PrintWindow never touch the real mouse, keyboard or foreground window, so the owner can keep
# working on the main screen. Screen capture does not work on a hidden desktop, which is why the
# window is on the second monitor.
#
# Never kills anything it did not start: Stop-DemoApp takes the PID Start-DemoApp returned.
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class DcWin {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
[void][DcWin]::SetProcessDPIAware()

$script:Hwnd = [IntPtr]::Zero
$script:OutDir = ''

function Start-DemoApp {
    <# Launches the exe against $DataRoot, waits for the main window, places it. Returns the process. #>
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string]$DataRoot,
        [Parameter(Mandatory)][string]$OutDir,
        [int]$Width = 1920,
        [int]$Height = 1080,
        [hashtable]$Environment = @{}
    )
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $script:OutDir = $OutDir
    $env:DOTCHARTER_DATA_ROOT = $DataRoot
    foreach ($k in $Environment.Keys) { Set-Item -Path "Env:$k" -Value $Environment[$k] }
    $p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($p.MainWindowHandle -eq 0) { throw "No main window for pid $($p.Id)." }
    $script:Hwnd = $p.MainWindowHandle
    Set-DemoWindow -Width $Width -Height $Height
    $p
}

function Connect-DemoApp {
    # Attaches to an instance this session started earlier (its PID), for step-by-step exploration.
    param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$OutDir)
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $script:OutDir = $OutDir
    $p = Get-Process -Id $ProcessId
    $script:Hwnd = $p.MainWindowHandle
    $p
}

function Set-DemoWindow {
    # Restores the window and puts a Width x Height OUTER window on the second monitor.
    param([int]$Width = 1920, [int]$Height = 1080)
    $screen = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
    if (-not $screen) { throw 'No second monitor: the README shots must not use the main screen.' }
    [void][DcWin]::ShowWindow($script:Hwnd, 9)
    # SWP_NOACTIVATE (0x10) | SWP_NOZORDER (0x4): never takes the foreground.
    [void][DcWin]::SetWindowPos($script:Hwnd, [IntPtr]::Zero, $screen.Bounds.X + 40, $screen.Bounds.Y + 40, $Width, $Height, 0x14)
}

function Get-DemoShot {
    # Saves the window as PNG (client area only, so no title bar) and returns the path.
    param([Parameter(Mandatory)][string]$Name)
    $r = New-Object DcWin+RECT
    [void][DcWin]::GetWindowRect($script:Hwnd, [ref]$r)
    $w = $r.R - $r.L; $h = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][DcWin]::PrintWindow($script:Hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $path = Join-Path $script:OutDir "$Name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $path
}

function Find-DemoElement {
    param([Parameter(Mandatory)][string]$Name, [switch]$Contains)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($script:Hwnd)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        $n = $e.Current.Name
        if ($e.Current.IsOffscreen) { continue }
        if (($Contains -and $n -like "*$Name*") -or $n -eq $Name) { return $e }
    }
    $null
}

function Get-DemoNames {
    # Every visible named element: how the screens are explored.
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($script:Hwnd)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        if ($e.Current.IsOffscreen -or -not $e.Current.Name) { continue }
        $b = $e.Current.BoundingRectangle
        '{0} | {1} | {2:N0},{3:N0} {4:N0}x{5:N0}' -f $e.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), $e.Current.Name, $b.X, $b.Y, $b.Width, $b.Height
    }
}

function Send-DemoClick {
    # Posts a left click at WINDOW-relative pixel x,y (as measured on a shot).
    param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y)
    $r = New-Object DcWin+RECT
    [void][DcWin]::GetWindowRect($script:Hwnd, [ref]$r)
    $pt = New-Object DcWin+POINT
    $pt.X = $r.L + $X; $pt.Y = $r.T + $Y
    [void][DcWin]::ScreenToClient($script:Hwnd, [ref]$pt)
    $l = [IntPtr](($pt.Y -shl 16) -bor ($pt.X -band 0xFFFF))
    [void][DcWin]::PostMessage($script:Hwnd, 0x0200, [IntPtr]::Zero, $l); Start-Sleep -Milliseconds 80
    [void][DcWin]::PostMessage($script:Hwnd, 0x0201, [IntPtr]1, $l); Start-Sleep -Milliseconds 80
    [void][DcWin]::PostMessage($script:Hwnd, 0x0202, [IntPtr]::Zero, $l)
}

function Send-DemoMove {
    # Posts a mouse move (hover) at window-relative x,y.
    param([int]$X, [int]$Y)
    $r = New-Object DcWin+RECT
    [void][DcWin]::GetWindowRect($script:Hwnd, [ref]$r)
    $pt = New-Object DcWin+POINT
    $pt.X = $r.L + $X; $pt.Y = $r.T + $Y
    [void][DcWin]::ScreenToClient($script:Hwnd, [ref]$pt)
    [void][DcWin]::PostMessage($script:Hwnd, 0x0200, [IntPtr]::Zero, [IntPtr](($pt.Y -shl 16) -bor ($pt.X -band 0xFFFF)))
}

function Invoke-DemoElement {
    # Presses the element with this UI Automation name: Invoke, then Select, then Toggle, then a posted click at its centre.
    param([Parameter(Mandatory)][string]$Name, [switch]$Contains)
    $e = Find-DemoElement -Name $Name -Contains:$Contains
    if (-not $e) { throw "No visible element named '$Name'." }
    $o = $null
    foreach ($pattern in @([System.Windows.Automation.InvokePattern]::Pattern, [System.Windows.Automation.SelectionItemPattern]::Pattern, [System.Windows.Automation.TogglePattern]::Pattern)) {
        if ($e.TryGetCurrentPattern($pattern, [ref]$o)) {
            if ($pattern -eq [System.Windows.Automation.InvokePattern]::Pattern) { $o.Invoke() }
            elseif ($pattern -eq [System.Windows.Automation.SelectionItemPattern]::Pattern) { $o.Select() }
            else { $o.Toggle() }
            return
        }
    }
    $b = $e.Current.BoundingRectangle
    $pt = New-Object DcWin+POINT
    $pt.X = [int]($b.X + ($b.Width / 2)); $pt.Y = [int]($b.Y + ($b.Height / 2))
    $r = New-Object DcWin+RECT
    [void][DcWin]::GetWindowRect($script:Hwnd, [ref]$r)
    Send-DemoClick -X ($pt.X - $r.L) -Y ($pt.Y - $r.T)
}

function Send-DemoKey {
    # Posts a key (virtual-key code) down+up; -Ctrl wraps it in a held Control.
    param([Parameter(Mandatory)][int]$Vk, [switch]$Ctrl, [switch]$Shift)
    if ($Ctrl) { [void][DcWin]::PostMessage($script:Hwnd, 0x0100, [IntPtr]0x11, [IntPtr]0) }
    if ($Shift) { [void][DcWin]::PostMessage($script:Hwnd, 0x0100, [IntPtr]0x10, [IntPtr]0) }
    [void][DcWin]::PostMessage($script:Hwnd, 0x0100, [IntPtr]$Vk, [IntPtr]0)
    [void][DcWin]::PostMessage($script:Hwnd, 0x0101, [IntPtr]$Vk, [IntPtr]0)
    if ($Shift) { [void][DcWin]::PostMessage($script:Hwnd, 0x0101, [IntPtr]0x10, [IntPtr]0) }
    if ($Ctrl) { [void][DcWin]::PostMessage($script:Hwnd, 0x0101, [IntPtr]0x11, [IntPtr]0) }
    Start-Sleep -Milliseconds 150
}

function Stop-DemoApp {
    # Closes the window the normal way, then ends the process by PID if it lingers. Only ever our own PID.
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process)
    if ($Process.HasExited) { return }
    [void]$Process.CloseMainWindow()
    if (-not $Process.WaitForExit(15000)) { Stop-Process -Id $Process.Id -Force }
}

Export-ModuleMember -Function Start-DemoApp, Connect-DemoApp, Set-DemoWindow, Get-DemoShot, Find-DemoElement, Get-DemoNames, Send-DemoClick, Send-DemoMove, Invoke-DemoElement, Send-DemoKey, Stop-DemoApp
