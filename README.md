<div align="center">

<img src="https://i.imgur.com/LOQNvsW.png" width="256" alt="GGSystemMonitor Icon"/>

# GGSystemMonitor

**Bring hardware sensor data and live widgets to your SteelSeries keyboard OLED screen.**  
A fully automated, installer-based Windows app that hooks into SteelSeries GG software and displays live system metrics, weather, media, time, and more - right on your Apex Pro's OLED display.

[![Version](https://img.shields.io/badge/version-v3.3.0-brightgreen?style=for-the-badge)](https://github.com/DaveTheTopDev/GGSystemMonitor/releases)
[![Platform](https://img.shields.io/badge/platform-Windows-blue?style=for-the-badge&logo=windows)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![Language](https://img.shields.io/badge/language-C%23%20.NET-purple?style=for-the-badge&logo=dotnet)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![RAM](https://img.shields.io/badge/memory-lightweight-orange?style=for-the-badge)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![License](https://img.shields.io/badge/license-GPL%203.0-gray?style=for-the-badge)](LICENSE)

### [⬇️ Download Latest Release](https://github.com/DaveTheTopDev/GGSystemMonitor/releases/download/3.3.0/GGSystemMonitorRelease-V3.3.0.zip)

</div>

---

![Apex Pro TKL OLED Display of Temperatures](https://i.imgur.com/1GnrzHC.jpeg "Apex Pro TKL OLED Display of Temperatures")

---

## Installation

1. **Download** the latest release zip from the [Releases](https://github.com/DaveTheTopDev/GGSystemMonitor/releases) page and extract it
2. **Run** `Install.bat` at the top of the extracted folder - the installer will launch and guide you through setup
3. When prompted, **accept the UAC dialog** to grant administrator rights (required for CPU temperature reading)
4. Choose your **install path**, select desired shortcuts, and click **Install**
5. Setup installs missing **PawnIO** and **.NET Framework 4.8** components from the offline bundle. Restart if requested. Automatic startup is enabled only if you select it.

> ✅ No separate prerequisite downloads are needed. Keep the extracted bundle together; SteelSeries GG must already be installed.

---

## Updating

Updating is simple:

1. **Download** the latest release zip from the [Releases](https://github.com/DaveTheTopDev/GGSystemMonitor/releases) page and extract it
2. **Run** `Install.bat` at the top of the extracted folder - setup detects your existing installation
3. Setup preserves settings and startup/shortcut choices. Settings opens after setup closes unless a restart is required.

The same version offers **Repair** to refresh application files without resetting settings.

---

## What's New in v3.3.0

| | Change |
|---|---|
| 💾 | **Display presets** - switch layouts instantly and reopen Settings on the active preset. Create presets or use the gear menu to rename, delete, or clone them. Layouts from before presets are kept as **Custom 1**. |
| 🖥️ | **New factory layout** - CPU and GPU temperatures can take over their lines at their hardware-specific warning temperatures. CPU and RAM usage join rotation at 70%; GPU usage joins at 75%. Both lines use a 3-second fallback interval. |
| ➕ | **Widget list controls** - add and select widgets with **+**. Right-click to reorder, remove, copy, paste, or hide widgets. The mouse wheel scrolls the list under the cursor. Hidden widgets stay in the list but never rotate or take priority. |
| 🎛️ | **Clearer widget settings** - OLED settings, widget-specific options, and **Priority and Visibility** are grouped into separate sections. |
| 📌 | **Priority and visibility** - control when a widget appears or takes over a line, including during media playback or when a threshold is met. Tied widgets take turns; a pinned widget keeps looping its scroll. |
| 🔄 | **Synchronized OLED rotation** - paired widgets stay together for the longer duration. Priority takeovers don't interrupt the other line, and a line with only one enabled widget keeps its own schedule. |
| 🔤 | **Per-widget font and size** - use different fonts on the two lines. **(default)** follows General; **Native font** overrides it. Mixed layouts keep one bitmap binding, so changing fonts during rotation doesn't flash the SteelSeries logo. Built-in icons retain the same appearance. |
| 🌡️ | **Expanded temperature Auto Detect** - adds more AMD Ryzen, Ryzen AI, Threadripper, Intel Core Ultra, and desktop GeForce models. Manual temperature limits are preserved. |
| 🔥 | **GPU hotspot warnings** - a sustained temperature gap cycles the paste warning, core temperature, and hotspot on the GPU widget's own line, even while it has priority. |
| 🛠 | **Setup and uninstall recovery** - setup limits how long it waits for running copies or stale driver services. A locked obsolete `GGSystemMonitor.sys` no longer blocks an update. If it blocks uninstall, setup preserves the uninstaller and asks you to restart and retry. |

Presets store the two widget lists and rotation settings. Switching presets saves Display changes and applies the selected layout; pending General changes still need **Save**. The first edit to **(default)** opens a new **Custom N** preset and leaves the factory layout untouched. Click **Save** to apply that edit.

Set **Now Playing → Priority and Visibility → Only while playing** to take over a line during playback, or **While media is present** to keep paused tracks visible as well. Temperature takeover defaults to the widget's warning temperature and can be overridden. RAM takeover targets accept percentage, MB, or GB; its normal **Show in rotation** threshold is a percentage. Use **Change priority** to move a widget up or down the ranking: it can join the next level in a tie or move out of a tie into its own level.

For Weather, **Priority and Visibility → Show in rotation** can include the widget only at or above or only at or below a temperature. The rule uses the widget’s configured location and display units.

If setup fails or appears stuck, check the current stage and `%TEMP%\GGSystemMonitor-install.log`. No Windows security settings are changed.

---

## Previous Update - v3.2.1

| | Change |
|---|---|
| 📦 | **PawnIO update fix** - detects compatible installed drivers even without an Apps & Features entry. A PawnIO conflict no longer blocks the app update; setup warns if CPU sensor support could not be verified. |
| 🛠 | **Responsive installer** - prerequisite installation and file operations run in the background, with stage progress, time limits for prerequisite installers, and a setup log. |
| 🔒 | **Safer update recovery** - existing settings stay intact, other GGSystemMonitor copies are not stopped, and failed setup no longer deletes the destination folder. |

If setup fails, see the current stage and the log at %TEMP%\GGSystemMonitor-install.log. Extract the full ZIP locally before running setup.

---

## Previous Update - v3.2.0

Version 3.2.0 focuses on hardware selection, standalone setup, diagnostics, and reliability.

| | Change |
|---|---|
| 🖥 | **Device selection** - select the CPU/GPU for temperature widgets and GPU for utilization widgets |
| 🌡 | **Independent sensor IDs** - each widget keeps its own selection; unavailable explicit selections are not silently replaced |
| 📦 | **Offline prerequisites** - official PawnIO 2.2.0 and .NET Framework 4.8 installers are bundled and installed automatically when needed |
| 🔎 | **Diagnostics** - hardware inventory, support-report export, and an OLED test pattern |
| 🎧 | **Experimental device profiles** - Arctis Pro Wireless and GameDAC resolution targets; physical-device validation is still required |
| 🪟 | **Settings access** - single-instance activation, tray controls, and graceful monitor stop |
| 🛠 | **Installer fixes** - detached hidden launcher, preserved update preferences, same-version repair, and post-update settings launch |
| 🗑 | **Verified uninstall** - temporary progress worker releases EXE locks and verifies removal before success; shared prerequisites are retained |
| 🔤 | **Unicode-safe scrolling** - custom-font scrolling no longer splits surrogate-pair characters |
| 📉 | **Performance improvements** - bounded caches/logs, disposed resources, and no forced working-set trimming |
| 🩺 | **Accurate sensor diagnostics** - missing CPU readings no longer automatically claim antivirus blocking |

> **Test candidate:** External OLED devices, affected CPU systems, and Apex 7 cold-boot lighting still need owner validation.

---

## Previous Update - v3.1.0

Version 3.1.0 is a feature update that adds new widgets, drastically improves the UI, and refines how temperature settings are configured.

| | Change |
|---|---|
| 🎨 | **Drastically improved UI** - the entire settings interface has been redesigned for a cleaner, more intuitive experience |
| ☀️ | **Weather widget** - displays the current weather conditions for your location |
| 🎵 | **Now Playing widget** - displays the currently playing media |
| 🕒 | **Time widget** - displays the current time |
| 📅 | **Date widget** - displays the current date |
| 🔤 | **Custom OLED fonts** - select from custom fonts in General settings to change how text is rendered on the OLED |
| 🧠 | **APU support** - integrated graphics on AMD APUs are now supported |
| 🌡️ | **Per-widget temperature sensor selection** - choose exactly which CPU/GPU sensor each temperature widget reads from |
| 🛡️ | **Sensor diagnostics** - reports missing CPU readings and helps investigate possible causes |
| 🗂️ | **Temperature tab removed** - all temperature-related settings are now configured directly inside each widget's settings, where they belong |
| 🐛 | **Bug fixes** - various stability and compatibility fixes throughout |

---

## Screenshots

<div align="center">

### Settings - Display Tab
<img src="https://i.imgur.com/3EN3WHh.png" alt="Display Settings Tab"/>

</div>

---

## Features

### Verified Uninstall
Use Uninstall in Settings or Windows Installed Apps. Settings exits to release its EXE.
A separate progress window stays open until the application folder, shortcuts, startup task,
and registration have been verified removed. Locked files produce an error rather than
false success. Shared PawnIO and .NET prerequisites are retained.

---


### Fully Automated Installer
The installer handles **everything** automatically:
- Choose your install directory (defaults to `%LocalAppData%`)
- Optionally add a **Start Menu** shortcut and/or a **Desktop** shortcut
- Optionally **start with Windows** - no manual Task Scheduler configuration needed
- A one-time UAC prompt grants the required administrator privileges to read hardware sensors

> **Note:** Setup requires administrator permission. Declining setup UAC cancels installation. Sensor availability also depends on hardware support and system policy.

---

### Display Configuration
The **Display** tab in Settings gives you complete control over what appears on your OLED:

- Configure **Top Line** and **Bottom Line** widgets independently
- Add widgets with **+**. Right-click to **reorder**, **remove**, **copy**, **paste**, or **hide** widgets
- Paste to either line before the row you right-click, or right-click below the last row to append a copy
- Save reusable **Display presets** and switch between them without affecting General settings
- Widgets **rotate** on a configurable interval (fallback: 3000ms by default)
- Each widget has its own settings panel for fine-grained customization
- Each widget has a default display string that can be **fully customized** using format placeholder codes
- A built-in **Placeholder Help** reference explains all available format codes

---

### Available Widgets

<table>
  <thead>
    <tr>
      <th width="16%">Widget</th>
      <th width="16%">Example Output</th>
      <th width="60%">Notes</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>CPU Temperature</td>
      <td><code>CPU: 72°C</code></td>
      <td>Shows <code>N/A</code> when a reading is unavailable. Display in °C or °F. Select which CPU sensor to read.</td>
    </tr>
    <tr>
      <td>CPU Usage %</td>
      <td><code>CPU USE: 45%</code></td>
      <td>Default label editable via format codes.</td>
    </tr>
    <tr>
      <td>GPU Temperature</td>
      <td><code>GPU: 68°C</code></td>
      <td>Display in °C or °F. Select which GPU sensor to read. APU graphics supported.</td>
    </tr>
    <tr>
      <td>GPU Usage %</td>
      <td><code>GPU USE: 30%</code></td>
      <td>APU graphics supported.</td>
    </tr>
    <tr>
      <td>RAM Usage</td>
      <td><code>RAM: 8.2/32.0GB</code></td>
      <td>Display in GB or MB. Used / total.</td>
    </tr>
    <tr>
      <td>Weather</td>
      <td><code>☀ 5°C - Sunny</code></td>
      <td>Live weather + conditions for your location. See note below.</td>
    </tr>
    <tr>
      <td>Now Playing</td>
      <td><code>♪ Artist - Song</code></td>
      <td>Currently playing media. Auto-scrolls if too long.</td>
    </tr>
    <tr>
      <td>Time</td>
      <td><code>⏰11:32:06 AM</code></td>
      <td>Current time. Configurable format (12h / 24h).</td>
    </tr>
    <tr>
      <td>Date</td>
      <td><code>📅 May 17, 2026</code></td>
      <td>Current date. Configurable format.</td>
    </tr>
    <tr>
      <td>Custom Text</td>
      <td>Any text</td>
      <td>Auto-scrolls if too long for display.</td>
    </tr>
  </tbody>
</table>

> 🌤️ **Weather Widget Note:** Weather uses wttr.in worldwide. For Canadian locations, GGSystemMonitor uses Environment Canada station data when available.

---

### Temperature Warnings (Per Widget)
Temperature warnings are now configured directly inside each temperature widget's settings:

- **Auto Detect** - known models use the built-in temperature database to set alerts below the listed maximum
- Manually override **Warning** and **Critical** temperatures for each individual widget
- Configure the **temperature poll interval** (how frequently sensor values are refreshed)
- Toggle warning indicators on or off per widget
- Choose which **specific sensor** the widget reads from
- At the **warning threshold**, a `⚠` icon blinks on the display
- At the **critical threshold**, a `🔥` icon blinks instead

**Default thresholds (auto-detected where supported):**

|  | Warning | Critical |
|---|---|---|
| CPU | 10°C below the critical alert | TJMax minus 3°C, rounded down to the nearest 5°C |
| GPU | Listed maximum minus 13°C | Listed maximum minus 4°C |

---

### GPU Thermal Paste Health Monitoring
When the difference between your **GPU core** and **hotspot** temperatures stays above the configured gap (15°C by default), the GPU widget cycles through a warning, core temperature, and hotspot temperature on its own OLED line. Both sensor readings must be available.

---

### Caps Lock Indicator
When Caps Lock is active, a `🡅` icon appears on the top line of the OLED - toggleable from the General settings tab.

---

### Auto Update Detection
GGSystemMonitor automatically checks for new releases and notifies you on the OLED when an update is available.

---

## General Settings

The **General** tab includes:

| Setting | Description |
|---|---|
| Update Available Notification | When a new version is available, a notification is shown on the OLED display. Toggle this on or off. |
| Caps Lock Indicator | Toggle the Caps Lock `🡅` display icon on/off. |
| Custom OLED Font | Set the default OLED font for widgets. Choose the native font or a supported font installed in Windows. |
| SteelSeries GG Path | Path to your `coreProps.json` file. By default GG installs to `C:\ProgramData\SteelSeries\SteelSeries Engine 3\coreProps.json`. Only change this if you installed SteelSeries GG to a non-default location. |

> 🔤 **Custom Fonts Note:** Built-in Caps Lock, temperature, media, weather, clock, and calendar icons keep their native appearance in every font. Other characters and symbols in custom text may still vary by font.

---

## CPU temperature unavailable?
A zero or missing temperature does not prove antivirus blocking. Check Diagnostics for
hardware inventory, sensor state, elevation and the PawnIO service.
The standalone release installer bundles and installs PawnIO and .NET automatically when needed. No separate prerequisite download is necessary. A restart can be required; Windows security policies are not bypassed.
Review protection history for specific blocked components. Do not disable antivirus or
Memory Integrity. Exclusions do not fix every failure. Other widgets remain available.

Arctis Pro Wireless and GameDAC output profiles remain experimental and need device-owner testing.

---

## Requirements

- **SteelSeries GG** software installed and running
- **SteelSeries Apex Pro** keyboard (other SteelSeries OLED keyboards may work but are untested)
- **Windows 10/11**
- **.NET Framework 4.8** (included with installer)
- **Administrator rights** (for CPU sensor access - granted once at install time via UAC)

---

## Technical Notes

- Built with **C# / .NET Windows Forms**
- Uses **LibreHardwareMonitor** for hardware sensor access
- Runs in the background with bounded rendering caches; Weather and Now Playing are only polled when an enabled widget needs them
- CPU sensor access depends on elevation, hardware support, and the PawnIO driver; if a reading is unavailable, the widget shows `N/A`
- Designed and tested on Intel CPU + NVIDIA GPU systems; AMD CPUs and APUs are now supported - please [open an issue](https://github.com/DaveTheTopDev/GGSystemMonitor/issues) if you encounter any problems

---

## License

This project is licensed under the GNU General Public License v3.0. See [LICENSE](LICENSE) for details.

---

<div align="center">

Made with ❤️ for the SteelSeries community

</div>
