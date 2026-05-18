<div align="center">

<img src="https://i.imgur.com/LOQNvsW.png" width="256" alt="GGSystemMonitor Icon"/>

# GGSystemMonitor

**Bring hardware sensor data and live widgets to your SteelSeries keyboard OLED screen.**  
A fully automated, installer-based Windows app that hooks into SteelSeries GG software and displays live system metrics, weather, media, time, and more - right on your Apex Pro's OLED display.

[![Release](https://img.shields.io/badge/release-v3.1.0-brightgreen?style=for-the-badge)](https://github.com/DaveTheTopDev/GGSystemMonitor/releases)
[![Platform](https://img.shields.io/badge/platform-Windows-blue?style=for-the-badge&logo=windows)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![Language](https://img.shields.io/badge/language-C%23%20.NET-purple?style=for-the-badge&logo=dotnet)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![RAM](https://img.shields.io/badge/memory-~16%20MB-orange?style=for-the-badge)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![License](https://img.shields.io/badge/license-GPL%203.0-gray?style=for-the-badge)](LICENSE)

### [⬇️ Download Latest Release](https://github.com/DaveTheTopDev/GGSystemMonitor/releases/download/3.1.0/GGSystemMonitor-V3.1.0.zip)

</div>

---

![Apex Pro TKL OLED Display of Temperatures](https://i.imgur.com/1GnrzHC.jpeg "Apex Pro TKL OLED Display of Temperatures")

---

## Installation

1. **Download** the latest release zip from the [Releases](https://github.com/DaveTheTopDev/GGSystemMonitor/releases) page and extract it
2. **Run** `install.bat` inside the extracted folder - the installer will launch and guide you through setup
3. When prompted, **accept the UAC dialog** to grant administrator rights (required for CPU temperature reading)
4. Choose your **install path**, select desired shortcuts, and click **Install**
5. The application will **launch automatically on Windows startup** - no further configuration needed

> ✅ That's it! The app runs silently in the background (~16 MB RAM) and your OLED will start displaying widgets automatically.

---

## Updating

Updating is simple:

1. **Download** the latest release zip from the [Releases](https://github.com/DaveTheTopDev/GGSystemMonitor/releases) page and extract it
2. **Run** `install.bat` inside the extracted folder - it will automatically update over your existing installation
3. Done!

---

## What's New in v3.1.0

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
| 🛡️ | **Antivirus detection** - automatically detects when an antivirus is blocking access to CPU temperature sensors and suggests a fix |
| 🗂️ | **Temperature tab removed** - all temperature-related settings are now configured directly inside each widget's settings, where they belong |
| 🐛 | **Bug fixes** - various stability and compatibility fixes throughout |

---

## Screenshots

<div align="center">

### Settings - Display Tab
<img src="https://i.imgur.com/O6HGDP0.png" alt="Display Settings Tab"/>

</div>

---

## Features

### Fully Automated Installer
The installer handles **everything** automatically:
- Choose your install directory (defaults to `%LocalAppData%`)
- Optionally add a **Start Menu** shortcut and/or a **Desktop** shortcut
- **Auto-starts with Windows** - no manual Task Scheduler configuration needed
- A one-time UAC prompt grants the required administrator privileges to read hardware sensors

> ⚠️ **Note:** Administrator rights are required to read CPU temperature sensors. You may decline the UAC prompt and the app will still install and run, but the CPU temperature sensor will display `N/A`.

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

| Widget | Example Output | Notes |
|---|---|---|
| CPU Temperature | `CPU: 72°C` | Requires admin rights; shows `N/A` without. Display in °C or °F. Select which CPU sensor to read. |
| CPU Usage % | `CPU USE: 45%` | Default label editable via format codes. |
| GPU Temperature | `GPU: 68°C` | Display in °C or °F. Select which GPU sensor to read. APU graphics supported. |
| GPU Usage % | `GPU USE: 30%` | APU graphics supported. |
| RAM Usage | `RAM: 8.2/32.0GB` | Display in GB or MB. Used / total. |
| **Weather** | `☀ 5°C - Sunny` | Live weather + conditions for your location. See note below. |
| **Now Playing** | `♪ Artist - Song` | Currently playing media. Auto-scrolls if too long. |
| **Time** | `🕒 11:32:06 AM` | Current time. Configurable format (12h / 24h). |
| **Date** | `📅 May 17, 2026` | Current date. Configurable format. |
| Custom Text | Any text | Auto-scrolls if too long for display. |

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

## ⚠️ CPU Temperature Showing `N/A` or `0.0°C`?

If your CPU temperature widget is showing `N/A` or `0.0°C`, the cause is almost always **an antivirus blocking access** to the CPU temperature sensor.

CPU temperature sensors are typically flagged as false positives by antivirus software because they require kernel-level access to read. Premium antivirus suites (such as **Norton**) usually don't flag them, but **Windows Defender** very commonly does on its own.

**GGSystemMonitor will automatically detect this and prompt you with a suggested fix.** If you want to fix it manually:

### Fix for Windows Defender
1. Open **Windows Security** → **Virus & threat protection**
2. Under "Virus & threat protection settings" click **Manage settings**
3. Scroll down to **Exclusions** and click **Add or remove exclusions**
4. Click **Add an exclusion** → **File**
5. Select the **GGSystemMonitor.exe** file in your install directory
6. Restart GGSystemMonitor

After the exclusion is added, the CPU temperature sensor will work normally.

> ℹ️ If you prefer not to add the exclusion, the application will still work completely fine - **only the CPU temperature widget will be unavailable**. All other widgets (GPU temp, usage, RAM, weather, media, time, date, etc.) will continue to work without issue.

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
