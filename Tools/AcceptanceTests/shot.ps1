param([string]$Out, [int]$X=0, [int]$Y=0, [int]$W=0, [int]$H=0, [double]$Scale=0.5)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
if (-not ("Gdi" -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Gdi {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
"@ }
[Gdi]::SetProcessDPIAware() | Out-Null
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
if ($W -eq 0) { $X=$b.X; $Y=$b.Y; $W=$b.Width; $H=$b.Height }
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc(); $sdc = [Gdi]::GetDC([IntPtr]::Zero)
[Gdi]::BitBlt($hdc, 0, 0, $W, $H, $sdc, $X, $Y, 0x00CC0020 -bor 0x40000000) | Out-Null
[Gdi]::ReleaseDC([IntPtr]::Zero, $sdc) | Out-Null; $g.ReleaseHdc($hdc)
$sw=[int]($W*$Scale); $sh=[int]($H*$Scale)
$small = New-Object System.Drawing.Bitmap $bmp, $sw, $sh
$small.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $small.Dispose()
"saved $Out ${W}x${H}"
