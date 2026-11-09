<#
.SYNOPSIS
    Captures the running Explorer Helper window to a PNG.
.DESCRIPTION
    The window moves between monitors and can sit at negative coordinates, so this finds its
    rect at run time, captures the whole virtual desktop and crops to it. Use it only when Jacob
    asks for a visual check.
.PARAMETER Out
    Where to write the PNG. Defaults to %TEMP%\eh_shot.png.
#>
param([string]$Out = "$env:TEMP\eh_shot.png")

Add-Type -AssemblyName System.Drawing,System.Windows.Forms
Add-Type @"
using System;using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
}
"@
$p = Get-Process ExplorerHelper | Select-Object -First 1
[Win]::ShowWindow($p.MainWindowHandle,9) | Out-Null      # SW_RESTORE
[Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 400
$r = New-Object Win+RECT; [Win]::GetWindowRect($p.MainWindowHandle,[ref]$r) | Out-Null
$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
$full = New-Object System.Drawing.Bitmap $vs.Width,$vs.Height
[System.Drawing.Graphics]::FromImage($full).CopyFromScreen($vs.Location,[System.Drawing.Point]::Empty,$vs.Size)
$w=$r.R-$r.L; $h=$r.B-$r.T
$crop = New-Object System.Drawing.Bitmap $w,$h
[System.Drawing.Graphics]::FromImage($crop).DrawImage($full,
  (New-Object System.Drawing.Rectangle 0,0,$w,$h),
  (New-Object System.Drawing.Rectangle ($r.L-$vs.Left),($r.T-$vs.Top),$w,$h),
  [System.Drawing.GraphicsUnit]::Pixel)
$crop.Save($Out)
Write-Host "Saved $Out"
