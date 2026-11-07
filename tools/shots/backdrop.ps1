# A full-screen gradient 'wallpaper' so screenshots never show the real desktop. Closes when the flag file appears (or after MaxSeconds).
param([string]$Flag, [int]$MaxSeconds = 240)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$f = New-Object Windows.Forms.Form
$f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'
$b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
$f.Bounds = $b; $f.TopMost = $false; $f.ShowInTaskbar = $false; $f.BackColor = [Drawing.Color]::FromArgb(40, 30, 90)
$f.Add_Paint({
  param($s, $e)
  $g = $e.Graphics; $g.SmoothingMode = 'AntiAlias'
  $rect = New-Object Drawing.Rectangle 0, 0, $b.Width, $b.Height
  $lg = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::FromArgb(255, 255, 126, 95)), ([Drawing.Color]::FromArgb(255, 52, 143, 220)), 35
  $cb = New-Object Drawing.Drawing2D.ColorBlend 3
  $cb.Colors = @([Drawing.Color]::FromArgb(255, 255, 140, 92), [Drawing.Color]::FromArgb(255, 190, 80, 150), [Drawing.Color]::FromArgb(255, 60, 140, 225))
  $cb.Positions = @(0.0, 0.5, 1.0); $lg.InterpolationColors = $cb
  $g.FillRectangle($lg, $rect)
  foreach ($blob in @(@(0.15, 0.7, 0.45, 255, 220, 120, 70), @(0.8, 0.25, 0.4, 120, 220, 255, 70), @(0.55, 0.95, 0.5, 255, 120, 200, 60))) {
    $r = [int]($b.Width * $blob[2]); $cx = [int]($b.Width * $blob[0]); $cy = [int]($b.Height * $blob[1])
    $path = New-Object Drawing.Drawing2D.GraphicsPath; $path.AddEllipse($cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $pg = New-Object Drawing.Drawing2D.PathGradientBrush $path
    $pg.CenterColor = [Drawing.Color]::FromArgb($blob[6], $blob[3], $blob[4], $blob[5]); $pg.SurroundColors = @([Drawing.Color]::FromArgb(0, $blob[3], $blob[4], $blob[5]))
    $g.FillPath($pg, $path)
  }
})
$f.Add_Shown({ $f.Activate(); New-Item ($Flag + '.ready') -ItemType File -Force | Out-Null })
$started = Get-Date
$t = New-Object Windows.Forms.Timer; $t.Interval = 300
$t.Add_Tick({ if ((Test-Path $Flag) -or ((Get-Date) - $started).TotalSeconds -gt $MaxSeconds) { $f.Close() } })
$t.Start()
[void]$f.ShowDialog()
