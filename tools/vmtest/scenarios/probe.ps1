# Run once when the VM is built, before its "ready" checkpoint: the desktop, the monitors and the
# keyboard path all work. Runs without the app.

Wait-Screens
Save-Screenshot "desktop"

# Hyper-V's virtual keyboard: Caps Lock is system-wide, so it shows up whichever window has focus.
$before = [System.Windows.Forms.Control]::IsKeyLocked([System.Windows.Forms.Keys]::CapsLock)
Send-Keys @("tap 0x14", "wait 300")
$after = [System.Windows.Forms.Control]::IsKeyLocked([System.Windows.Forms.Keys]::CapsLock)
if ($after -eq $before) { Fail "a key sent through Hyper-V's virtual keyboard didn't arrive" }
else { Step "keys from the host arrive" }
Send-Keys @("tap 0x14", "wait 300")
