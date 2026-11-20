# Live previews: with the default settings, a window whose content keeps changing has a tile whose
# preview changes too while the switcher is open. Needs 2 or more monitors: the window is on the
# first, so anything that changes on the second is its preview.

if ($Monitors -lt 2) { Fail "this scenario needs -Monitors 2 or more"; return }
Wait-Screens
New-TestWindow "Live test window" -screen 0 -live | Out-Null
New-TestWindow "Still test window" -screen 0 -x 300 -y 300 | Out-Null
$switcher = Start-Process $App -PassThru
Start-Sleep -Seconds 3
if ($switcher.HasExited) { Fail "the app exited at start (code $($switcher.ExitCode))"; return }

Send-Keys @("down 0x12", "wait 50", "tap 0x09", "wait 1000")
Save-Screenshot "previews-1"
Start-Sleep -Milliseconds 700
Save-Screenshot "previews-2"
Send-Keys @("up 0x12", "wait 500")

# The second monitor without its taskbar (its clock could tick over).
$b = (Get-Screens)[1].Bounds
$area = New-Object System.Drawing.Rectangle $b.X, $b.Y, $b.Width, ($b.Height - 60)
$diff = Compare-Screenshots "previews-1" "previews-2" $area
Step "monitor 2: $($diff.Pixels) pixels changed $($diff.Box)"
if ($diff.Pixels -lt 50) { Fail "the live window's preview on monitor 2 didn't change: previews aren't live" }

Stop-Process -Id $switcher.Id -Force
