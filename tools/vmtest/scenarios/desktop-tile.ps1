# The Show desktop tile (showDesktopTile): picking it with real keys shows the desktop, like Win+D.

Set-AppSettings '{ "showDesktopTile": true }'
Wait-Screens
New-TestWindow "Test window 1" -screen 0 | Out-Null
New-TestWindow "Test window 2" -screen 0 -x 300 -y 200 | Out-Null
$switcher = Start-Process $App -PassThru
Start-Sleep -Seconds 3
if ($switcher.HasExited) { Fail "the app exited at start (code $($switcher.ExitCode))"; return }

# The desktop tile does nothing when the desktop already has focus, so first switch to a window.
Press-AltTab
$before = Get-ForegroundTitle
Step "foreground before: '$before'"
if ($before -notlike "Test window*") { Fail "couldn't put a test window in front first" }

# Alt+Tab, End (the last tile: Show desktop), let go of Alt.
Send-Keys @("down 0x12", "wait 50", "tap 0x09", "wait 300", "tap 0x23", "wait 600")
Save-Screenshot "desktop-tile-selected"
Send-Keys @("up 0x12", "wait 1500")
Save-Screenshot "after"

$after = Get-ForegroundTitle
Step "foreground after: '$after'"
if ($after -like "Test window*") { Fail "a test window still has focus: the desktop wasn't shown" }
$log = Get-Content "$env:LOCALAPPDATA\AltTabEverywhere\log.txt" -Raw
if ($log -match "show desktop: (.*)") { Fail "the app logged: show desktop: $($Matches[1])" }
if ($log -notmatch "showed the desktop from the switcher") { Fail "the app didn't log showing the desktop" }

Stop-Process -Id $switcher.Id -Force
