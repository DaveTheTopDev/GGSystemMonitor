<div align="center">

<img src="https://i.imgur.com/LOQNvsW.png" width="256" alt="GGSystemMonitor Icon"/>

# GGSystemMonitor

**Bring hardware sensor data and live widgets to your SteelSeries keyboard OLED screen.**  
A fully automated, installer-based Windows app that hooks into SteelSeries GG software and displays live system metrics, weather, media, time, and more - right on your Apex Pro's OLED display.

[![Release](https://img.shields.io/badge/release-v3.2.1-brightgreen?style=for-the-badge)](https://github.com/DaveTheTopDev/GGSystemMonitor/releases)
[![Platform](https://img.shields.io/badge/platform-Windows-blue?style=for-the-badge&logo=windows)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![Language](https://img.shields.io/badge/language-C%23%20.NET-purple?style=for-the-badge&logo=dotnet)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![RAM](https://img.shields.io/badge/memory-lightweight-orange?style=for-the-badge)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![License](https://img.shields.io/badge/license-GPL%203.0-gray?style=for-the-badge)](LICENSE)

### [⬇️ Download Latest Release](https://github.com/DaveTheTopDev/GGSystemMonitor/releases/download/3.2.1/GGSystemMonitor-V3.2.1.zip)

</div>

---

![Apex Pro TKL OLED Display of Temperatures](https://i.imgur.com/1GnrzHC.jpeg "Apex Pro TKL OLED Display of Temperatures")

---

## Installation

1. **Download** the latest release zip from the [Releases](https://github.com/DaveTheTopDev/GGSystemMonitor/releases) page and extract it
2. **Run** `install.bat` inside the extracted folder - the installer will launch and guide you through setup
3. When prompted, **accept the UAC dialog** to grant administrator rights (required for CPU temperature reading)
4. Choose your **install path**, select desired shortcuts, and click **Install**
5. Setup installs missing **PawnIO** and **.NET Framework** components from the offline bundle. Restart if requested. Automatic startup is enabled only if you select it.

> ✅ No separate prerequisite downloads are needed. Keep the extracted bundle together; SteelSeries GG must already be installed.

---

## Updating

Updating is simple:

1. **Download** the latest release zip from the [Releases](https://github.com/DaveTheTopDev/GGSystemMonitor/releases) page and extract it
2. **Run** `install.bat` inside the extracted folder - it will automatically update over your existing installation
3. Setup preserves settings and startup/shortcut choices. Settings opens after setup closes unless a restart is required.

The same version offers **Repair** to refresh application files without resetting settings.

---

## What's New in v3.2.1

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
- **Auto-starts with Windows** - no manual Task Scheduler configuration needed
- A one-time UAC prompt grants the required administrator privileges to read hardware sensors

> **Note:** Setup requires administrator permission. Declining setup UAC cancels installation. Sensor availability also depends on hardware support and system policy.

---

### Display Configuration
The **Display** tab in Settings gives you complete control over what appears on your OLED:

- Configure **Top Line** and **Bottom Line** widgets independently
- Add any combination of widgets, then **reorder** or **remove** them freely
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
      <td>Requires admin rights; shows <code>N/A</code> without. Display in °C or °F. Select which CPU sensor to read.</td>
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
      <td><code>🕒 11:32:06 AM</code></td>
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

> 🌤️ **Weather Widget Note:** For locations in **Canada**, weather data is pulled directly from **Environment Canada** for high accuracy. For the rest of the world, it pulls from a global database and may be slightly less accurate.

---

### Temperature Warnings (Per Widget)
Temperature warnings are now configured directly inside each temperature widget's settings:

- **Auto Detect** - the widget queries your hardware against the built-in database and uses manufacturer TJMax/Max values automatically
- Manually override **Warning** and **Critical** temperatures for each individual widget
- Configure the **temperature poll interval** (how frequently sensor values are refreshed)
- Toggle warning indicators on or off per widget
- Choose which **specific sensor** the widget reads from
- When temperature nears the **warning threshold** a `⚠` icon blinks on the display
- When temperature nears the **critical threshold** a `🔥` icon blinks instead

**Default thresholds (auto-detected where supported):**

|  | Warning | Critical |
|---|---|---|
| CPU | TJMax - 15°C | TJMax - 5°C |
| GPU | TJMax - 13°C | TJMax - 4°C |

---

### GPU Thermal Paste Health Monitoring
When the difference between your **GPU core** and **hot spot** temperatures exceeds **15°C**, the bottom OLED line begins scrolling a warning message - giving you an early heads-up that GPU thermal cooling may need attention.

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
| Custom OLED Font | Choose from a selection of bundled custom fonts to change how text is rendered on the OLED. See note below. |
| SteelSeries GG Path | Path to your `coreProps.json` file. By default GG installs to `C:\ProgramData\SteelSeries\SteelSeries Engine 3\coreProps.json`. Only change this if you installed SteelSeries GG to a non-default location. |

> 🔤 **Custom Fonts Note:** Using a custom font may change the appearance of some symbols and icons (e.g. `⚠`, `🔥`, `🡅`, music notes) on the OLED. If an icon looks incorrect with your chosen font, try a different font or switch back to the default.

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
- **.NET Runtime** (included with installer)
- **Administrator rights** (for CPU sensor access - granted once at install time via UAC)

---

## Technical Notes

- Built with **C# / .NET Windows Forms**
- Uses **LibreHardwareMonitor** for hardware sensor access
- Runs in the background using ~**16 MB** of memory
- CPU temperature reading requires admin privileges and an unblocked antivirus; on systems where this is unavailable, CPU temp will show as `N/A`
- Designed and tested on Intel CPU + NVIDIA GPU systems; AMD CPUs and APUs are now supported - please [open an issue](https://github.com/DaveTheTopDev/GGSystemMonitor/issues) if you encounter any problems

---

## License

This project is licensed under the GNU General Public License v3.0. See [LICENSE](LICENSE) for details.

---

<div align="center">

Made with ❤️ for the SteelSeries community

</div>
