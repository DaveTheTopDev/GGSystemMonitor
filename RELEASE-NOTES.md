# 3.2.0 candidate — hardware selection and diagnostics

## Included
- Independent CPU/GPU sensor selection by identifier (legacy sensor names still work).
- Device selection on GPU temperature/usage and CPU temperature widgets; Auto prefers identifiable discrete GPUs.
- Explicit missing-device/sensor selections are retained instead of silently substituted.
- Pinned LibreHardwareMonitor 0.9.6 and transitive dependencies; SDK-style build retaining .NET Framework 4.7.2.
- Experimental GameSense resolution profiles: Arctis Pro Wireless 128x48, GameDAC 128x52.
- Diagnostics tab, export, upstream driver prerequisite link, and three-second OLED test pattern.
- Single-instance Settings activation, settings tray controls, graceful monitor stop.
- Installer STA launch, elevation exit propagation, log, script-scoped completion state, and post-close settings launch.
- Invalid readings are not automatically diagnosed as antivirus blocking.
- Unicode-safe custom-font scroll, bounded weather cache/logging, serialized main-thread settings reload.
- Removed forced GC and working-set trimming.

## Requirements and limitations
Windows 10/11 and .NET Framework 4.7.2 or later. Build x64 by default; an x86 packaging option is provided.
The release ZIP bundles the official signed PawnIO 2.2.0 setup and full .NET Framework 4.8 offline redistributable. Setup installs missing prerequisites silently; no separate downloads are needed. A Windows restart may be required. The current signed PawnIO driver requires 64-bit Windows (including when using the x86 application build). SteelSeries GG remains the required host application.
No security exclusions or Windows protection settings are changed automatically.
Permission problems, missing prerequisites, unsupported sensors, and security blocks are distinct possibilities.

Experimental profiles select GameSense screen resolutions, not individual USB devices.
All connected devices with the selected resolution may match. Keyboard remains the default.
No direct HID protocol, custom USB ID, or arbitrary image upload support is claimed.

## Still requires external validation
- Arctis Pro Wireless and GameDAC rendering and reconnect behavior (#8/#11).
- Apex 7 cold-boot lighting with GG 116 (#14); root cause remains unconfirmed.
- CPU access on the affected Intel/AMD systems (#10/#12/#13), especially after the library/driver change.
- Installer lifecycle in clean Windows 10/11 VMs and an eight-hour resource soak.

## Developer verification
See TEST-RESULTS.md for the locally executed checks and limitations.

Run:
    dotnet build tests/Regression.csproj -c Release
    tests\bin\Release\net472\Regression.exe
Optional local sensor probe (no GG output or security configuration changes):
    tests\bin\Release\net472\Regression.exe --sensors
Build release ZIP using unique staging directories:
    powershell -NoProfile -File Build-Release.ps1
Dependency versions are recorded in packages.lock.json. Review security detections for the exact
component rather than assuming any flagged executable or driver is a false positive.

## Installer and uninstall follow-up
- Detached hidden launcher prevents a waiting command window.
- Standalone temporary uninstall worker keeps progress visible while Settings exits
  to release the executable. Deletion and remaining registration/task/shortcuts are
  verified before success. Locked files report failure; shared prerequisites stay installed.
- A stray EXE without installation markers no longer triggers update mode.
- Same-version Repair refreshes the application and uninstaller while keeping settings.
