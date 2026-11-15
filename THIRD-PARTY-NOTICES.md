# Third-party components

GGSystemMonitor is distributed under GPL-3.0; bundled dependencies retain their own licenses.
Exact direct and transitive versions and integrity hashes are recorded in packages.lock.json.

- LibreHardwareMonitorLib 0.9.6: MPL-2.0.
  Source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6
  The application consumes the published upstream package without modifying its library.
- Newtonsoft.Json: MIT. https://github.com/JamesNK/Newtonsoft.Json
- Microsoft System.* and Microsoft.Bcl.* packages: upstream .NET licenses.
  https://github.com/dotnet/runtime
- HidSharp: https://github.com/IntergatedCircuits/HidSharp
- DiskInfoToolkit, RAMSPDToolkit-NDD and their transitive dependencies:
  consult the package metadata and license files in the restored NuGet packages.

PawnIO 2.2.0 is bundled unmodified and installed automatically when missing or outdated. It is a shared prerequisite and is not removed on uninstall. Driver source (GPL-2.0-or-later with the upstream exception), COPYING, and the pinned PawnPP interpreter source are included in Prerequisites source archives. Microsoft .NET Framework 4.8 is redistributed unmodified under its accompanying Microsoft terms. No antivirus exclusions are changed.

Moq and Castle.Core are development/test dependencies only and are not included in release ZIPs.
