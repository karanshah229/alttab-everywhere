<#
.SYNOPSIS
  Makes the README's screenshots and GIF from staged sample windows only.

.DESCRIPTION
  Opens the pages in samples/ as app windows in a throwaway Edge profile, then runs the switcher
  with --demo --demo-defaults (built-in defaults, not your settings) and --demo-since (only windows
  of processes started after this script began), so none of your own windows appear. Every image is
  cropped to the switcher itself, so nothing behind it is captured either. Closes the sample windows
  and deletes the profile afterwards.

  Writes: switcher.png (the main monitor's switcher), monitors.png (every monitor's switcher, drawn
  on outline screens), tour.gif (the selection moving through the tiles).

.EXAMPLE
  ./tools/screenshots/make-screenshots.ps1
#>
param(
    [string] $Exe = "src/AltTabEverywhere/bin/Release/net10.0-windows/AltTabEverywhere.exe",
    [string] $Out = "docs/images"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, PresentationCore, WindowsBase
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Dpi { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[void][Dpi]::SetProcessDPIAware() # capture in physical pixels, like the switcher logs its position

$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$exePath = Resolve-Path (Join-Path $root $Exe)
New-Item -ItemType Directory -Force (Join-Path $root $Out) | Out-Null
$outDir = Resolve-Path (Join-Path $root $Out)
$log = Join-Path $env:LOCALAPPDATA "AltTabEverywhere/log.txt"
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" }
$profileDir = Join-Path $env:TEMP "alttab-showcase-$([guid]::NewGuid().ToString('N').Substring(0, 8))"

function Capture-Screen {
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($vs.X, $vs.Y, 0, 0, $bmp.Size)
    $g.Dispose()
    return @{ Bitmap = $bmp; X = $vs.X; Y = $vs.Y }
}
Add-Type -AssemblyName System.Windows.Forms

# The switcher only, cut to its rounded window shape (Windows 11: 8 px corners at 100%), so none
# of what's behind it shows at the corners or edges.
function Crop($shot, $overlay) {
    $rect = $overlay.Rect
    $r = New-Object System.Drawing.Rectangle ($rect.X - $shot.X), ($rect.Y - $shot.Y), $rect.W, $rect.H
    $plain = $shot.Bitmap.Clone($r, $shot.Bitmap.PixelFormat)
    $cut = New-Object System.Drawing.Bitmap $rect.W, $rect.H, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($cut)
    $g.SmoothingMode = 'AntiAlias'
    $d = [single](16 * $overlay.Scale); $w = [single]($rect.W - 3); $h = [single]($rect.H - 3)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(1, 1, $d, $d, 180, 90); $path.AddArc($w - $d + 1, 1, $d, $d, 270, 90)
    $path.AddArc($w - $d + 1, $h - $d + 1, $d, $d, 0, 90); $path.AddArc(1, $h - $d + 1, $d, $d, 90, 90); $path.CloseFigure()
    $g.SetClip($path)
    $g.DrawImageUnscaled($plain, 0, 0)
    $g.Dispose(); $plain.Dispose()
    return $cut
}

# The overlays' positions from the demo's log lines: "demo: monitor=\\.\DISPLAY2 primary=True ... overlay=(417,320) 1085x608".
function Overlays($lines) {
    foreach ($line in $lines) {
        $scale = if ($line -match 'monitorScale=([\d.]+)') { [double]$Matches[1] } else { 1.0 }
        if ($line -match 'demo: monitor=(\S+) primary=(\w+) work=\((-?\d+),(-?\d+)\) (\d+)x(\d+).* overlay=\((-?\d+),(-?\d+)\) (\d+)x(\d+)') {
            [pscustomobject]@{ Device = $Matches[1]; Primary = $Matches[2] -eq "True"; Scale = $scale
                Work = @{ X = [int]$Matches[3]; Y = [int]$Matches[4]; W = [int]$Matches[5]; H = [int]$Matches[6] }
                Rect = @{ X = [int]$Matches[7]; Y = [int]$Matches[8]; W = [int]$Matches[9]; H = [int]$Matches[10] } }
        }
    }
}

function Run-Demo([string[]] $extra, [scriptblock] $whileShown) {
    $start = (Get-Content $log).Count
    $demoArgs = @("--demo", "--demo-defaults", "--demo-since", $since) + $extra
    # Park the pointer in a screen corner, off the switcher, so no tile shows its hover close button.
    $pointer = [System.Windows.Forms.Cursor]::Position
    $corner = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ($corner.Right - 2), ($corner.Bottom - 2)
    $p = Start-Process $exePath -ArgumentList $demoArgs -PassThru
    # Wait for the demo's layout lines: logged once the switcher is on screen and settled.
    for ($i = 0; $i -lt 100; $i++) {
        $new = Get-Content $log | Select-Object -Skip $start
        if (($new | Where-Object { $_ -match 'demo: monitor=' }).Count -ge 1) { break }
        Start-Sleep -Milliseconds 100
    }
    Start-Sleep -Milliseconds 300
    $new = Get-Content $log | Select-Object -Skip $start
    $result = & $whileShown (Overlays $new)
    $p.WaitForExit(30000) | Out-Null
    [System.Windows.Forms.Cursor]::Position = $pointer
    return $result
}

$since = (Get-Date).ToUniversalTime().ToString("o")
Start-Sleep -Milliseconds 300
$samples = "dashboard", "weather", "music", "calendar", "notes", "photos"
$x = 80; $y = 80
foreach ($name in $samples) {
    $file = (Resolve-Path (Join-Path $PSScriptRoot "samples/$name.html")).Path -replace '\\', '/'
    Start-Process $edge -ArgumentList "--user-data-dir=`"$profileDir`"", "--no-first-run", "--no-default-browser-check",
        "--disable-sync", "--app=file:///$file", "--window-size=1000,640", "--window-position=$x,$y"
    $x += 40; $y += 30
    Start-Sleep -Milliseconds 900
}
Start-Sleep -Seconds 5

try {
    # 1. The switcher on every monitor.
    Run-Demo @("--demo-seconds", "3") {
        param($overlays)
        $shot = Capture-Screen
        $primary = $overlays | Where-Object Primary | Select-Object -First 1
        if (-not $primary) { $primary = $overlays | Select-Object -First 1 }
        $crop = Crop $shot $primary
        $crop.Save((Join-Path $outDir "switcher.png"), [System.Drawing.Imaging.ImageFormat]::Png)

        # Every monitor's switcher, on outline screens arranged as the monitors are (scaled down).
        $minX = ($overlays | ForEach-Object { $_.Work.X } | Measure-Object -Minimum).Minimum
        $minY = ($overlays | ForEach-Object { $_.Work.Y } | Measure-Object -Minimum).Minimum
        $maxX = ($overlays | ForEach-Object { $_.Work.X + $_.Work.W } | Measure-Object -Maximum).Maximum
        $maxY = ($overlays | ForEach-Object { $_.Work.Y + $_.Work.H } | Measure-Object -Maximum).Maximum
        $scale = [Math]::Min(1.0, [Math]::Min(1400.0 / ($maxX - $minX), 1000.0 / ($maxY - $minY)))
        $pad = 24
        $canvas = New-Object System.Drawing.Bitmap ([int](($maxX - $minX) * $scale) + 2 * $pad), ([int](($maxY - $minY) * $scale) + 2 * $pad)
        $g = [System.Drawing.Graphics]::FromImage($canvas)
        $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'
        $g.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
        foreach ($o in $overlays) {
            $sx = $pad + ($o.Work.X - $minX) * $scale; $sy = $pad + ($o.Work.Y - $minY) * $scale
            $screen = New-Object System.Drawing.RectangleF $sx, $sy, ($o.Work.W * $scale - 8), ($o.Work.H * $scale - 8)
            $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 36, 41, 51))), $screen)
            $g.DrawRectangle((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 90, 98, 112)), 3), $screen.X, $screen.Y, $screen.Width, $screen.Height)
            $part = Crop $shot $o
            $dx = $pad + ($o.Rect.X - $minX) * $scale; $dy = $pad + ($o.Rect.Y - $minY) * $scale
            $g.DrawImage($part, [single]$dx, [single]$dy, [single]($o.Rect.W * $scale), [single]($o.Rect.H * $scale))
        }
        $g.Dispose()
        $canvas.Save((Join-Path $outDir "monitors.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    } | Out-Null

    # 2. The selection moving through the tiles: one frame per step.
    $stepMs = 800
    $frames = Run-Demo @("--demo-tour", "$stepMs", "--demo-seconds", "1") {
        param($overlays)
        $primary = $overlays | Where-Object Primary | Select-Object -First 1
        if (-not $primary) { $primary = $overlays | Select-Object -First 1 }
        $list = New-Object System.Collections.Generic.List[object]
        $t = [System.Diagnostics.Stopwatch]::StartNew()
        for ($i = 0; $i -lt ($samples.Count + 1); $i++) {
            $due = $i * $stepMs + $stepMs / 2 - 300
            while ($t.ElapsedMilliseconds -lt $due) { Start-Sleep -Milliseconds 10 }
            $list.Add((Crop (Capture-Screen) $primary))
        }
        return , $list
    }

    $encoder = New-Object System.Windows.Media.Imaging.GifBitmapEncoder
    foreach ($frame in $frames) {
        $width = [Math]::Min(900, $frame.Width); $height = [int]($frame.Height * $width / $frame.Width)
        $small = New-Object System.Drawing.Bitmap $frame, $width, $height
        $ms = New-Object System.IO.MemoryStream
        $small.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $ms.Position = 0
        $decoded = New-Object System.Windows.Media.Imaging.PngBitmapDecoder($ms, 'PreservePixelFormat', 'OnLoad')
        $encoder.Frames.Add($decoded.Frames[0])
    }
    $gifStream = New-Object System.IO.MemoryStream
    $encoder.Save($gifStream)
    $bytes = $gifStream.ToArray()

    # WPF writes no frame delays and no looping: add both. Each frame's Graphic Control Extension gets
    # a delay, and a NETSCAPE2.0 loop block goes after the header and global colour table.
    $packed = $bytes[10]
    $headerEnd = 13 + $(if ($packed -band 0x80) { 3 * [Math]::Pow(2, ($packed -band 7) + 1) } else { 0 })
    $body = New-Object System.Collections.Generic.List[byte]
    $body.AddRange([byte[]]$bytes[0..($headerEnd - 1)])
    $body.AddRange([byte[]](@(0x21, 0xFF, 0x0B) + [System.Text.Encoding]::ASCII.GetBytes("NETSCAPE2.0") + @(0x03, 0x01, 0x00, 0x00, 0x00)))
    $rest = [byte[]]$bytes[$headerEnd..($bytes.Length - 1)]
    $delay = [int]($stepMs / 10)
    $i = 0; $sawGce = $false
    while ($i -lt $rest.Length) {
        if ($rest[$i] -eq 0x21 -and $rest[$i + 1] -eq 0xF9) {
            $body.AddRange([byte[]](0x21, 0xF9, 0x04, ($rest[$i + 3] -band 0xE3 -bor 0x04), ($delay -band 0xFF), ($delay -shr 8), $rest[$i + 6], 0x00))
            $i += 8; $sawGce = $true; continue
        }
        if ($rest[$i] -eq 0x2C -and -not $sawGce) {
            $body.AddRange([byte[]](0x21, 0xF9, 0x04, 0x04, ($delay -band 0xFF), ($delay -shr 8), 0x00, 0x00))
        }
        if ($rest[$i] -eq 0x2C) {
            # An image: descriptor (10 bytes), local colour table, then LZW sub-blocks. Copy it whole.
            $flags = $rest[$i + 9]
            $len = 10 + $(if ($flags -band 0x80) { 3 * [Math]::Pow(2, ($flags -band 7) + 1) } else { 0 }) + 1
            $body.AddRange([byte[]]$rest[$i..($i + $len - 1)]); $i += $len
            while ($rest[$i] -ne 0) { $n = $rest[$i]; $body.AddRange([byte[]]$rest[$i..($i + $n)]); $i += $n + 1 }
            $body.Add(0); $i++; $sawGce = $false; continue
        }
        $body.Add($rest[$i]); $i++
    }
    [System.IO.File]::WriteAllBytes((Join-Path $outDir "tour.gif"), $body.ToArray())
}
finally {
    # Close the sample windows (only the browser processes using the throwaway profile) and delete it.
    Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" |
        Where-Object { $_.CommandLine -like "*$profileDir*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
}
Get-ChildItem $outDir | ForEach-Object { "{0}  {1:N0} KB" -f $_.Name, ($_.Length / 1KB) }
