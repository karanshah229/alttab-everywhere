# The default scenario: windows on every monitor, a screenshot of the switcher (--demo), then the
# navigation self-test (--selftest) on every monitor.

Wait-Screens
$screens = Get-Screens
for ($i = 0; $i -lt $screens.Count; $i++) {
    New-TestWindow "Test window A$($i + 1)" -screen $i | Out-Null
    New-TestWindow "Test window B$($i + 1)" -screen $i -x 300 -y 200 | Out-Null
}
Start-TestWindow "explorer.exe" "C:\vmtest" -title "vmtest" -screen 0 -x 500 -y 300 | Out-Null
Start-Sleep -Seconds 2
Save-Screenshot "desktop"

$demo = Start-Process $App -ArgumentList "--demo", "--demo-seconds", "6" -PassThru
Start-Sleep -Seconds 3
Save-Screenshot "switcher"
$demo.WaitForExit()
if ($demo.ExitCode -ne 0) { Fail "--demo exit code $($demo.ExitCode)" }

Step "self-test..."
$t = Start-Process $App -ArgumentList "--selftest" -PassThru -Wait
if ($t.ExitCode -ne 0) { Fail "--selftest exit code $($t.ExitCode) (details in log.txt)" }
else { Step "self-test passed" }
