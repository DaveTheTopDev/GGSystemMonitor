# 3.2.0 candidate verification

Local verification performed during implementation:

- Release application and regression builds: zero warnings and zero errors.
- 56 automated checks: passed. Covers invalid/selected sensor values, discrete GPU preference,
  CPU model normalization, native text caps budgets at four scroll phases, output dimensions,
  screen-only GameSense bindings through a local mock HTTP server, settings construction,
  dialog ownership/singletons, persisted sensor/device IDs and unknown note fields.
- Custom-font stress: 2,000 rendered image frames after warm-up; approximately 1.4 MB private-byte
  growth in the last measured run. This is a synthetic stress check, not a long-duration leak proof.
- Separate Settings process smoke test: launched successfully; duplicate launch exited successfully
  instead of leaving a second settings process.
- Local upstream sensor probe: detected Intel i9-11900K, memory, and NVIDIA RTX 3080 Ti.
  GPU core/hotspot/memory temperatures were readable. CPU sensors were enumerated with no readings
  in this session. CPU access is NOT claimed validated.
- x64 and x86 release packaging exercised with locked dependency restore.
- Installer PowerShell parser and git whitespace checks: passed.

Not performed: destructive install/update/uninstall against the user's live installation,
Windows 10/11 clean-VM lifecycle matrix, eight-hour soak, affected reporters' CPU systems,
Arctis/GameDAC physical screens, or Apex 7 cold boot with GG 116.

These builds are test candidates, not proof that every issue is resolved on external hardware.
No GitHub issues have been closed.

## Standalone prerequisite installer verification
- Official PawnIO 2.2.0 and Microsoft .NET Framework 4.8 offline redistributables downloaded at build time.
- Both package SHA-256 hashes and Authenticode publisher signatures validated on this PC.
- 17 prerequisite tests pass using mocked process execution: existing components, fresh chain,
  reboot-required codes, failures, post-install verification, missing payload, and tampering.
- Runtime prerequisite installation is offline and never automatically removes shared components.
- Actual machine-wide prerequisite installation and reboot behavior still require a clean-VM test;
  no real driver/runtime installation was performed by the automated tests on this PC.

## Installer lifecycle regression
12 isolated tests passed for fresh/update detection, complete removal, stray executables,
and locked-file failure retaining recovery registration. Registry/tasks/processes were
mocked; actual file deletion and exclusive file locking used temporary fixture folders.
No live installation was removed during testing.
