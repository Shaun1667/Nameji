param([int]$X=800, [int]$Y=600, [int]$W=700, [int]$H=400, [string]$Title="ShimejiTestWindow", [switch]$Fullscreen)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class D { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[D]::SetProcessDPIAware() | Out-Null
$f = New-Object System.Windows.Forms.Form
$f.Text = $Title
$f.StartPosition = 'Manual'
$f.BackColor = [System.Drawing.Color]::FromArgb(235, 240, 250)
if ($Fullscreen) {
  $f.FormBorderStyle = 'None'
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $f.Bounds = $b
  $f.TopMost = $true
} else {
  $f.Location = New-Object System.Drawing.Point $X, $Y
  $f.TopMost = $true
  $f.Size = New-Object System.Drawing.Size $W, $H
}
$l = New-Object System.Windows.Forms.Label
$l.Text = "Shimeji test window (closes automatically)"
$l.AutoSize = $true
$l.Location = New-Object System.Drawing.Point 20, 20
$f.Controls.Add($l)
$t = New-Object System.Windows.Forms.Timer
$t.Interval = 180000
$t.Add_Tick({ $f.Close() })
$t.Start()
[System.Windows.Forms.Application]::Run($f)
