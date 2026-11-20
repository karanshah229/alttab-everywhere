# The app as you use it: started normally, then a real Alt+Tab from the keyboard (through
# Hyper-V's virtual keyboard, so the keyboard hook sees real key presses).

Wait-Screens
$screens = Get-Screens
New-TestWindow "Test window 1" -screen 0 | Out-Null
New-TestWindow "Test window 2" -screen ($screens.Count - 1) -x 300 -y 200 | Out-Null
Start-Sleep -Seconds 1

$switcher = Start-Process $App -PassThru
Start-Sleep -Seconds 3
if ($switcher.HasExited) { Fail "the app exited at start (code $($switcher.ExitCode))"; return }
$before = [VmTestWin]::Title([VmTestWin]::GetForegroundWindow())
Step "foreground before: '$before'"

# Hold Alt, press Tab: the switcher should be on every monitor while Alt is down.
Send-Keys @("down 0x12", "wait 50", "tap 0x09", "wait 700")
Save-Screenshot "alt-held"
Send-Keys @("up 0x12", "wait 700")
Save-Screenshot "after-release"

# Releasing Alt switches to the previous window.
$after = [VmTestWin]::Title([VmTestWin]::GetForegroundWindow())
Step "foreground after: '$after'"
if ($after -eq $before) { Fail "Alt+Tab didn't change the foreground window" }

Stop-Process -Id $switcher.Id -Force
