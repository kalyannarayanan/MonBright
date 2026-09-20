# MonBright for Windows

> A brightness slider for every monitor on your PC. By Kalyan.

Windows gives your laptop screen a brightness slider and gives your external
monitors nothing. MonBright fixes that. It puts a brightness slider in your
notification area — one for your laptop screen, and one for each external
monitor you've plugged in.

Click the icon. Drag the slider. The screen gets brighter or darker. That's it.

```
┌──────────────────────────────────────┐
│  Built-in Display              30%   │
│  ·  [======o==========]  ☀           │
│                                      │
│  MSI MD271UL                   85%   │
│  ·  [==============o==]  ☀           │
│  ──────────────────────────────────  │
│   Ctrl+Alt+F2 brighter · Ctrl+Alt+F1 dimmer  │
└──────────────────────────────────────┘
```

You can also press **Ctrl+Alt+F2** to brighten or **Ctrl+Alt+F1** to dim — it
changes whichever screen your mouse cursor is on, in 5% steps. Hold the keys
down to keep changing in a smooth ramp. No need to open the panel first.

## Will this work on my PC?

Almost certainly, if:

1. **Windows 10 (version 1903 or newer) or Windows 11.** Nothing to install —
   MonBright uses the .NET Framework that is already built into Windows.
2. **Your monitor is connected by HDMI, DisplayPort, or USB-C.** Brightness is
   sent down the same cable that carries the picture, using a standard called
   DDC/CI. Most monitors made in the last decade support it.

A few monitors ship with DDC/CI switched **off** in their on-screen menu. If a
slider does nothing, look through the monitor's own settings menu for an option
called *DDC/CI* and turn it on.

Works on Intel, AMD, and ARM PCs. Desktops with no built-in screen work fine —
you just get one slider per external monitor.

## How to install it

**Step 1 — Download.** Grab `MonBright-1.0.0.zip` from the
[Releases page](https://github.com/kalyannarayanan/MonBright/releases/latest).

**Step 2 — Extract it** somewhere you'll keep it, such as your Documents
folder. MonBright is a single `MonBright.exe` — there is no installer, and
nothing is written to Program Files or the Windows folder.

**Step 3 — Run `MonBright.exe`.**

⚠️ **The first time only**, Windows may show a blue box that says
*"Windows protected your PC"*. This appears because the app isn't
code-signed with a paid certificate — not because anything is wrong with it.
Click **More info**, then **Run anyway**.

**Step 4 — Find the icon.** Look at the bottom-right of your screen, near the
clock. On **Windows 11 the icon starts out hidden**: click the small **^**
arrow to see hidden icons. To keep it permanently visible, drag the MonBright
icon from that pop-up onto the taskbar. (Or: *Settings → Personalization →
Taskbar → Other system tray icons* → switch MonBright on.)

**Step 5 (optional) — Start it automatically.** Right-click the MonBright icon
and tick **Start with Windows**.

You're done.

## How to use it

- **Left-click the icon** to open the panel, then drag any slider.
- **Scroll the mouse wheel** over a slider to nudge it in 5% steps.
- **Ctrl+Alt+F2 / Ctrl+Alt+F1** brighten or dim the monitor your cursor is on,
  from anywhere, without opening the panel. Hold to ramp.
- **Arrow keys** work too once the panel is open — up/down picks a monitor,
  left/right adjusts it, Home/End jump to 0% and 100%, Esc closes.
- **Right-click the icon** for options: start with Windows, restore brightness
  at startup, turn the hotkeys off, refresh displays, About, quit.

If a slider is greyed out, the row underneath says why — usually DDC/CI being
switched off in that monitor's own menu. If another app has already claimed
Ctrl+Alt+F1/F2, MonBright says so instead of the shortcuts silently doing
nothing.

Brightness is remembered per monitor, so levels stay put when you reboot,
unplug a monitor, or plug it into a different port.

### Darker than your monitor's minimum

Turn an external monitor's own brightness all the way down and it's usually
still brighter than you want at night. That's not a bug in the monitor — a
monitor's "0" means *the dimmest its backlight goes*, not *off*, and on a
typical panel that's still bright enough to read a white page by in a dark
room.

The bottom third of MonBright's slider goes past that. Down there the monitor
is already at its floor, so MonBright dims the picture itself instead, which
gets you meaningfully darker than the buttons on the back of the monitor can.
The tradeoff is that very dark shades start blending together, so use it when
you want a dark screen rather than for colour work.

A few details worth knowing:

- **It's invisible to screenshots and screen sharing.** Your screen looks dim
  to you, but a screenshot — or what your team sees when you share on a call —
  looks completely normal. (Windows 10 version 2004 or later; on older builds
  the dimming still works but does show up in captures.)
- **Quitting MonBright always undoes it.** The dimming is a window MonBright
  owns, so if anything ever looks wrong, quitting restores your display
  immediately.
- **Night Light is left alone.** MonBright doesn't touch the colour settings
  Night Light uses, so the two work together.
- **Laptop screens get the regular range.** Built-in panels already dim
  genuinely dark on their own, so this only applies to external monitors.

## How to uninstall

1. Right-click the icon → **Quit MonBright**.
2. Delete `MonBright.exe`.
3. Delete the folder `%APPDATA%\MonBright` (settings) and
   `%LOCALAPPDATA%\MonBright` (the diagnostic log), if you want them gone.

If you turned on *Start with Windows*, untick it before quitting — or delete
the `MonBright` value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

Nothing else is ever written anywhere. There is no installer and no uninstaller
because there is nothing to uninstall.

## Is this safe?

Yes. MonBright is built to require zero permissions and zero privileges.

- ❌ No administrator rights (it refuses to ask — the manifest says `asInvoker`)
- ❌ No internet or network access of any kind
- ❌ No access to your files, documents, mail, or photos
- ❌ No keyboard logging — it registers exactly two hotkeys with Windows and
  never sees any other keystroke
- ❌ No screen capture, camera, or microphone
- ❌ Nothing written to Program Files, System32, or `HKEY_LOCAL_MACHINE`

MonBright only adjusts the brightness of monitors you've already plugged in,
using the same public Windows APIs that the Settings app uses.

Things you can verify for yourself:

- **Network activity:** zero outbound connections. Watch it in Task Manager's
  *App history*, Resource Monitor, or any firewall — the log stays empty.
- **What it writes:** `%APPDATA%\MonBright\settings.ini` (a few lines of
  plain text) and `%LOCALAPPDATA%\MonBright\monbright.log` (written only when
  something fails).
- **Size:** a single ~190 KB executable. No bundled runtime, no DLLs, no
  telemetry SDK, nothing to phone home with.
- **Source code:** ~2,200 lines of C# in this repository.

---

<details>
<summary><b>For curious developers — how the brightness control actually works</b></summary>

Two completely different paths, because Windows has no single API that covers
both kinds of display:

- **External monitors → DDC/CI.** Windows ships a public Monitor Configuration
  API in `dxva2.dll`. `GetPhysicalMonitorsFromHMONITOR` gives you a handle per
  monitor, and `SetMonitorBrightness` sends a brightness command down the video
  cable. Under the hood that is VCP code `0x10` (Luminance) — the exact same
  command the macOS build of MonBright writes over I²C. Monitors that reject
  the high-level call are retried with a raw `SetVCPFeature(0x10, …)`.

- **The built-in laptop panel → WMI.** Laptop panels are driven by the
  graphics driver, not DDC/CI, so brightness goes through
  `WmiMonitorBrightnessMethods.WmiSetBrightness` in the `root\wmi` namespace —
  the same call behind the Windows brightness slider.

Neither needs elevation.

**Monitor ranges aren't always 0-100.** Some panels report `min=20, max=80` or
`0-10`. Everything in the app works in percent and converts at the boundary.

**Two gotchas worth knowing if you build something similar:**

1. *`GetPhysicalMonitorsFromHMONITOR` can hand back a handle whose value is
   literally `0`, and it works fine.* It is an opaque token, not a pointer.
   Treating `0` as failure — the reflex any C programmer has — silently
   disables working monitors. This code carries a separate `HasDdc` flag and
   never tests the handle against zero.

2. *Windows reports nearly every monitor as "Generic PnP Monitor".* The real
   model name lives in the monitor's EDID blob, which Windows caches at
   `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<hw-id>\<instance>\Device Parameters\EDID`.
   MonBright parses it byte-for-byte the same way the macOS build parses the
   EDID it reads over I²C, so the same physical monitor gets the same
   `MANU-PPPP-SSSSSSSS` identifier on both platforms.

</details>

<details>
<summary><b>For curious developers — why this is one binary when the Mac build needs two</b></summary>

The macOS version of MonBright ships two executables: a menu-bar app and a
`setter` CLI helper. That is a workaround for an Apple-specific defect — on
Apple Silicon, a Swift binary that links AppKit sees `DCPAVServiceProxy`
entries with an empty property dictionary, so the GUI process physically cannot
open the display's AV service. It has to shell out to a plain CLI binary for
every write.

Windows has no equivalent problem. `dxva2.dll` behaves identically whether the
caller is a console app, a WinForms app, an STA thread, or an MTA thread
(verified across all four). So the Windows build is a single process with no
subprocess spawn per brightness change.

DDC writes still cost roughly 30-70 ms and block the calling thread, so each
monitor owns a worker thread with a single "latest value wins" slot. Dragging a
slider overwrites the pending value instead of queueing, so the panel converges
on where your finger actually stopped rather than replaying the whole drag.

</details>

<details>
<summary><b>For curious developers — building it</b></summary>

```powershell
git clone https://github.com/kalyannarayanan/MonBright.git
cd MonBright\windows
.\build.ps1 -Run
```

There is **nothing to install first**. `build.ps1` uses the C# compiler that
ships inside Windows itself (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`)
— no Visual Studio, no .NET SDK, no NuGet, no MSBuild. It also generates the
multi-resolution `.ico` by hand, since `System.Drawing` can read icons but not
write them.

```powershell
.\build.ps1          # build to bin\MonBright.exe
.\build.ps1 -Run     # build, then launch
.\build.ps1 -Zip     # build, then package dist\MonBright-1.0.0.zip
```

Targeting .NET Framework 4.8 rather than .NET 8 is deliberate: 4.8 is a
component of Windows itself, so users download one ~190 KB file and it runs.
A .NET 8 build would either need users to install the Desktop Runtime or ship
a ~90 MB self-contained bundle.

| File | Purpose |
|------|---------|
| `src\Program.cs` | Entry point, single-instance guard, DPI setup |
| `src\TrayApp.cs` | Tray icon (drawn at runtime), menu, hotkeys, hotplug |
| `src\FlyoutForm.cs` | The owner-drawn panel: layout, painting, mouse/keyboard |
| `src\AboutForm.cs` | About window — real controls, so screen readers can read it |
| `src\Displays.cs` | Display enumeration, EDID parsing, stable per-monitor ids |
| `src\Brightness.cs` | The two backends: DDC/CI via dxva2, and WMI |
| `src\MonitorModel.cs` | Per-monitor state + the coalescing writer thread |
| `src\Settings.cs` | `settings.ini`, and the per-user "start with Windows" key |
| `src\Theme.cs` | Follows the system light/dark setting and accent colour |
| `src\Native.cs` | Every P/Invoke, in one place |

Diagnostic log: `%LOCALAPPDATA%\MonBright\monbright.log` (failures only).

</details>

## Also available for Mac

The macOS version — same idea, same per-monitor identifiers, built for Apple
Silicon — lives in [`macos/`](../macos/) in this same repository.

## Support

If MonBright saves you from squinting at your monitor every day, you can buy me
a coffee. Totally optional — the app stays free either way.

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-FFDD00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/kalyannarayanan)

## License

MIT — see [LICENSE](LICENSE).
