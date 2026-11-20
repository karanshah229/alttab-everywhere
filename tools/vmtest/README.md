# Test VM

`vmtest.ps1` tests AltTab Everywhere in a Hyper-V virtual machine with 1 to 4 monitors. Nothing appears on your own screens, so you can keep working while it runs. It's one script using only Windows' own Hyper-V and PowerShell: no VirtualBox, Packer or other tools to install.

## Use

Once, ever: turn on Hyper-V (one administrator prompt, then restart Windows).

```powershell
./tools/vmtest/vmtest.ps1 -Setup
```

Then run a test:

```powershell
./tools/vmtest/vmtest.ps1
```

```powershell
./tools/vmtest/vmtest.ps1 -Scenario alttab -Monitors 3
```

The exit code is 0 if the scenario passed. Screenshots, the scenario's log and the app's log go to `.vmtest\results\<time>-<scenario>-<monitors>mon\`.

The first run builds the VM, which takes about 30 minutes and needs one administrator prompt (to write Windows onto the virtual disk). Every run after that takes a minute or two.

## How it works

| Step | When | What |
|---|---|---|
| Turn on Hyper-V | Once (`-Setup`) | Turns on the Hyper-V feature and adds you to "Hyper-V Administrators", so later runs need no administrator prompt. WSL2 and Docker keep working: they already run on the same hypervisor |
| Download | First run | Microsoft's free Windows 11 Enterprise evaluation ISO (8.2 GB) and the [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (130 KB), both from pinned URLs. The driver is checked against a pinned SHA-256. The ISO's `setup.exe` must be signed by Microsoft, and the script prints the ISO's SHA-256 so it can be pinned too |
| Build the VM | First run | Writes Windows straight onto a virtual disk with an answer file that skips every setup screen and signs in a local account automatically. The account's random password is kept encrypted for you alone (Windows DPAPI) in `.vmtest\vm\credential.xml` |
| Prepare it | First run | Installs the virtual display driver, turns off sleep, the lock screen and notifications, checks that the monitors and keyboard work, then saves a "ready" checkpoint |
| Test | Every run | Builds the app from this source, restores the checkpoint, copies the build in, sets the monitor count, runs the scenario on the VM's desktop, copies the results back and turns the VM off |

Details:

- **Real key presses.** The app ignores simulated keys on purpose, so scenarios ask the host for keys, which it types through Hyper-V's virtual keyboard. The VM sees them as real key presses.
- **No network.** The VM has no network adapter: no updates, and nothing leaves it.
- **The evaluation never expires.** The VM's clock doesn't follow this PC's, so after the checkpoint is restored, Windows never sees time pass. If it ever says it's expired, rebuild with `-Rebuild`.
- **Resources.** The VM uses 4 GB of memory and 2 processors, and only while a test runs. It's off the rest of the time. Disk: about 35 GB in `.vmtest\` next to the repository, which git ignores.

## Options

| Option | Default | What it does |
|---|---|---|
| `-Scenario` | `selftest` | A file in `scenarios/` (without `.ps1`), or the path of your own scenario script |
| `-Monitors` | `2` | Monitors in the VM, 1 to 4: Hyper-V's own screen plus virtual ones |
| `-AppDir` | | Test this folder (the switcher's publish output) instead of building the app |
| `-WithSettings` | off | Also build the settings window into the VM (self-contained, about 150 MB more to copy) |
| `-KeepRunning` | off | Leave the VM running afterwards, to look at it in Hyper-V Manager |
| `-TimeoutMinutes` | `15` | Give up on a scenario after this long (exit code 2) |
| `-Setup` | | Turn on Hyper-V and add you to Hyper-V Administrators |
| `-Rebuild` | | Delete the VM and build it again from the downloaded ISO |
| `-Remove` | | Delete the VM. Downloads are kept: delete `.vmtest\downloads` to free them |
| `-Root` | `.vmtest` next to the repo | Where the VM, downloads and results go |
| `-MemoryMB`, `-Processors`, `-Resolution` | `4096`, `2`, `1920x1080` | VM size, used when it's built |

## Scenarios

| Scenario | What it checks |
|---|---|
| `selftest` | Two windows on every monitor, a screenshot of the switcher (`--demo`), then `--selftest` on every monitor |
| `alttab` | Starts the app normally, holds Alt and presses Tab, takes a screenshot with the switcher open, releases Alt, and checks that the foreground window changed |
| `probe` | Used once when the VM is built: the monitors and the keyboard path work |

A scenario is a PowerShell script that runs on the VM's desktop as the signed-in user, not elevated. These are available to it:

| Name | What it is |
|---|---|
| `$App`, `$Settings` | Paths of the switcher and settings exes in the VM |
| `$Monitors`, `$Out` | The monitor count, and the folder whose files come back as results |
| `Wait-Screens [n]` | Waits until Windows shows n monitors (default: `$Monitors`) |
| `Save-Screenshot name` | A PNG of all monitors |
| `New-TestWindow title [-screen n -x -y -width -height]` | A plain window in its own process, on monitor n |
| `Start-TestWindow file args -title text [-screen n ...]` | Starts a program and moves its window, found by title, to monitor n |
| `Send-Keys lines` | Real key presses: `down 0x12`, `up 0x12`, `tap 0x09`, `text abc`, `wait 200` (virtual-key codes) |
| `Press-AltTab [tabs] [holdMs]` | Alt+Tab, pressing Tab `tabs` times |
| `Step text`, `Fail text` | Log a line, or record a failure (the run then exits with 1) |
