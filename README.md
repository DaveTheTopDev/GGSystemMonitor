<div align="center">

<img src="https://i.imgur.com/LOQNvsW.png" width="256" alt="GGSystemMonitor Icon"/>

# GGSystemMonitor

**Bring hardware sensor data to your SteelSeries keyboard OLED screen.**  
A fully automated, installer-based Windows app that hooks into SteelSeries GG software and displays live system metrics - right on your Apex Pro's OLED display.

[![Release](https://img.shields.io/badge/release-v3.0.0-brightgreen?style=for-the-badge)](https://github.com/DaveTheTopDev/GGSystemMonitor/releases)
[![Platform](https://img.shields.io/badge/platform-Windows-blue?style=for-the-badge&logo=windows)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![Language](https://img.shields.io/badge/language-C%23%20.NET-purple?style=for-the-badge&logo=dotnet)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![RAM](https://img.shields.io/badge/memory-~16%20MB-orange?style=for-the-badge)](https://github.com/DaveTheTopDev/GGSystemMonitor)
[![License](https://img.shields.io/badge/license-MIT-gray?style=for-the-badge)](LICENSE)

### [⬇️ Download Latest Release](https://github.com/DaveTheTopDev/GGSystemMonitor/releases/download/3.0.0/GGSystemMonitor-V3.0.0.zip)

</div>

---

![Apex Pro TKL OLED Display of Temperatures](https://i.imgur.com/1GnrzHC.jpeg "Apex Pro TKL OLED Display of Temperatures")

---

## Installation

1. **Download** the latest release zip from the [Releases](https://github.com/DaveTheTopDev/GGSystemMonitor/releases/tag/3.0.0) page and extract it
2. **Run** `install.bat` inside the extracted folder - the installer will launch and guide you through setup
3. When prompted, **accept the UAC dialog** to grant administrator rights (required for CPU temperature reading)
4. Choose your **install path**, select desired shortcuts, and click **Install**
5. The application will **launch automatically on Windows startup** - no further configuration needed

> ✅ That's it! The app runs silently in the background (~16 MB RAM) and your OLED will start displaying sensor data automatically.

---

## Updating

Updating is simple:

1. **Download** the latest release zip from the [Releases](https://github.com/DaveTheTopDev/GGSystemMonitor/releases/tag/3.0.0) page and extract it
2. **Run** `install.bat` inside the extracted folder - it will automatically update over your existing installation
3. Done!

---

## What's New in v3.0.0 - Major Overhaul

Version 3.0.0 is a **complete rewrite** of GGSystemMonitor. Almost every aspect of the application has been redesigned from the ground up.

| | Change |
|---|---|
| 🚀 | **Installer modal** - one-click setup with path selection, shortcuts, and automatic startup. No more manual Task Scheduler setup. |
| ⚙️ | **Settings GUI** - full in-app settings window with Display, Temperature, and General tabs |
| 🔁 | **Rotating display lines** - configure multiple sensor items per line and rotate between them on a timer |
| 📊 | **Expanded sensor support** - CPU temp, CPU usage %, GPU temp, GPU usage %, RAM usage, and custom text |
| 📝 | **Custom text items** - display any text you want, mixed in with sensor data; long text auto-scrolls |
| 🎛️ | **Format code control** - fine-grained control over how each value is displayed using placeholder format strings |
| 🌡️ | **Auto hardware detection** - automatically detects your CPU/GPU and applies manufacturer critical temperature limits |
| 🔔 | **Auto update detection** - the app notifies you when a new version is available |
| 🐛 | **Major bug fixes** - numerous stability and compatibility improvements |

---

## Screenshots
<div align="center">

### Settings - Display Tab
<img src="https://i.imgur.com/O6HGDP0.png" alt="Display Settings Tab"/>

### Settings - Temperature Tab
<img src="https://i.imgur.com/fNcIIAm.png" alt="Temperature Settings Tab"/>

</div>

---

## Features

### Fully Automated Installer
The new installer handles **everything** automatically:
- Choose your install directory (defaults to `%LocalAppData%`)
- Optionally add a **Start Menu** shortcut and/or a **Desktop** shortcut
- **Auto-starts with Windows** - no manual Task Scheduler configuration needed
- A one-time UAC prompt grants the required administrator privileges to read hardware sensors

> ⚠️ **Note:** Administrator rights are required to read CPU temperature sensors. You may decline the UAC prompt and the app will still install and run, but the CPU temperature sensor will display `N/A`.

---

### Display Configuration
The **Display** tab in Settings gives you complete control over what appears on your OLED:

- Configure **Top Line** and **Bottom Line** items independently
- Add any combination of sensors and text, then **reorder** or **remove** items freely
- Items **rotate** on a configurable interval (fallback: 3000ms by default)
- Each item has a default display string that can be **fully customized** using format placeholder codes
- A built-in **Placeholder Help** reference explains all available format codes

**Available display items:**

| Sensor | Example Output | Notes |
|---|---|---|
| CPU Temperature | `CPU: 72°C` | Requires admin rights; shows `N/A` without. Display in °C or °F. |
| CPU Usage % | `CPU USE: 45%` | Default label editable via format codes. |
| GPU Temperature | `GPU: 68°C` | Display in °C or °F. |
| GPU Usage % | `GPU USE: 30%` | Default label editable via format codes. |
| RAM Usage | `RAM: 8.2/32.0GB` | Display in GB or MB. Used / total. |
| Custom Text | Any text | Auto-scrolls if too long for display. |

---

### Temperature Warning System
The **Temperature** tab lets you configure warning thresholds for your hardware:

- **Auto Detect** - the app queries your hardware against its built-in database and uses manufacturer TJMax/Max values automatically
- Manually override **Warning** and **Critical** temperatures for both CPU and GPU
- Configure the **temperature poll interval** (how frequently sensor values are refreshed)
- Toggle warning indicators on or off per hardware independently
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
GGSystemMonitor automatically checks for new releases and notifies you when an update is available.

---

## General Settings

The **General** tab includes:

| Setting | Description |
|---|---|
| Update Available Notification | When a new version is available, a notification is shown on the OLED display. Toggle this on or off. |
| Caps Lock Indicator | Toggle the Caps Lock `🡅` display icon on/off. |
| SteelSeries GG Path | Path to your `coreProps.json` file. By default GG installs to `C:\ProgramData\SteelSeries\SteelSeries Engine 3\coreProps.json`. Only change this if you installed SteelSeries GG to a non-default location. |

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
- CPU temperature reading requires admin privileges; on systems where this is unavailable, CPU temp will show as `N/A`
- Designed and tested on Intel CPU + NVIDIA GPU systems; should work on AMD hardware but may have edge cases - please [open an issue](https://github.com/DaveTheTopDev/GGSystemMonitor/issues) if you encounter any

---

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.

---

<div align="center">

Made with ❤️ for the SteelSeries community

</div>
