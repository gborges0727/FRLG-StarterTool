# Helpers to drive FRLGStarterTool.exe for the step 5 checks: start it, find windows, click, type, screenshot.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ('G2' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class G2 {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll")] public static extern bool QueryPerformanceCounter(out long c);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(60); mouse_event(2, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(4, 0, 0, 0, IntPtr.Zero); }
    public static long ClickT(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(60); long t; QueryPerformanceCounter(out t); mouse_event(2, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(40); mouse_event(4, 0, 0, 0, IntPtr.Zero); return t; }
    public static long Key(byte vk) { long t; QueryPerformanceCounter(out t); keybd_event(vk, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(30); keybd_event(vk, 0, 2, IntPtr.Zero); return t; }
}
"@
}
[G2]::SetProcessDPIAware() | Out-Null

$script:Exe = 'C:\Users\User\source\FRLG-StarterTool\src\FRLG.StarterTool.App\bin\publish\win-x64\FRLGStarterTool.exe'
$script:Root = [System.Windows.Automation.AutomationElement]::RootElement

function Start-Tool {
    $p = Start-Process -FilePath $script:Exe -PassThru
    for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break } }
    Start-Sleep -Seconds 2
    $p.Refresh()
    [G2]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 40, 40, 0, 0, 0x0001) | Out-Null
    [G2]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    return $p
}

function Get-Windows($p) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    return $script:Root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Find-Named($el, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Click-El($el) {
    $b = $el.Current.BoundingRectangle
    [G2]::Click([int]($b.X + $b.Width / 2), [int]($b.Y + $b.Height / 2))
}

function Save-Window([IntPtr]$h, [string]$out) {
    $r = New-Object G2+RECT
    [G2]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $hgt)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "saved $out (${w}x${hgt})"
}

function Dump-Tree($el, [int]$depth = 0, [int]$max = 6) {
    if ($depth -gt $max) { return }
    $c = $el.Current
    $b = $c.BoundingRectangle
    "{0}{1} '{2}' id={3} class={4} at {5},{6} {7}x{8}" -f ('  ' * $depth), $c.ControlType.ProgrammaticName, $c.Name, $c.AutomationId, $c.ClassName, [int]$b.X, [int]$b.Y, [int]$b.Width, [int]$b.Height
    $kids = $el.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($k in $kids) { Dump-Tree $k ($depth + 1) $max }
}

function New-Rec([string]$name) {
    $dir = "C:\Users\User\source\frlg-beep-results\gui\$name"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Remove-Item "$dir\recording","$dir\stop","$dir\onsets.csv","$dir\envelope.csv" -ErrorAction SilentlyContinue
    Start-Process -FilePath 'C:\Users\User\source\flowtimer\BeepProbe\out\x86\BeepProbe.exe' -ArgumentList '--mode','record','--selftest-dir',$dir,'--max-s','900','--abs-thr','0.02' -WindowStyle Hidden -RedirectStandardOutput "$dir\record.log" | Out-Null
    for ($i = 0; $i -lt 50 -and -not (Test-Path "$dir\recording"); $i++) { Start-Sleep -Milliseconds 100 }
    Start-Sleep -Milliseconds 700
    return $dir
}

function Stop-Rec([string]$dir) {
    New-Item -ItemType File "$dir\stop" -Force | Out-Null
    for ($i = 0; $i -lt 100 -and -not (Test-Path "$dir\envelope.csv"); $i++) { Start-Sleep -Milliseconds 200 }
    Start-Sleep -Milliseconds 300
    return Import-Csv "$dir\onsets.csv"
}

function Type-Text([string]$text) {
    foreach ($ch in $text.ToCharArray()) { [G2]::Key([byte][char]$ch) | Out-Null; Start-Sleep -Milliseconds 40 }
}

