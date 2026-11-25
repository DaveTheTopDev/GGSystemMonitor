# Version detection
function Get-AppVersion($path) {
    try {
        $raw = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
        $p   = ($raw -split '\.')
        if ($p.Count -ge 3) { return "$($p[0]).$($p[1]).$($p[2])" }
        return $raw
    } catch { return "" }
}


function Resolve-SetupDestination([string]$Path, [string]$Source, [string]$ExeName) {
    $Path = $Path.Trim()
    if ($Path -notmatch '^[A-Za-z]:\\') { throw "Choose an absolute path on a local drive." }
    $target = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($target + '\')
    $drive = New-Object IO.DriveInfo($root)
    if ($drive.DriveType -notin @("Fixed", "Removable")) { throw "Choose a local installation drive, not a network location." }
    $blocked = @($root.TrimEnd('\'), $env:WINDIR, $env:USERPROFILE, $env:LOCALAPPDATA, $env:ProgramFiles, [Environment]::GetEnvironmentVariable("ProgramFiles(x86)"), $env:ProgramData)
    if ($blocked -contains $target) { throw "Choose a dedicated application folder, not a system or profile root." }
    $sourceFull = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    if ($target -eq $sourceFull -or $target.StartsWith($sourceFull + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $sourceFull.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Use a separate installation folder, not the extracted bundle."
    }
    if (Test-Path -LiteralPath $target) {
        $item = Get-Item -LiteralPath $target -Force -ErrorAction Stop
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Choose a real application folder, not a file or folder link."
        }
        if ((Get-ChildItem -LiteralPath $target -Force -ErrorAction Stop | Select-Object -First 1) -and
            -not (Test-Path -LiteralPath (Join-Path $target $ExeName) -PathType Leaf)) {
            # A failed copy can leave a partial payload. Allow retry only when
            # the directory contains known package files, not unrelated data.
            $sourceNames = @(Get-ChildItem -LiteralPath $Source -File -ErrorAction Stop | ForEach-Object Name)
            $unexpected = Get-ChildItem -LiteralPath $target -Force -ErrorAction Stop |
                Where-Object { $_.PSIsContainer -or $_.Name -notin $sourceNames } | Select-Object -First 1
            if ($unexpected) { throw "Choose an empty folder or an existing GGSystemMonitor installation." }
        }
    }
    return $target
}

function Stop-InstalledMonitor([string]$ExePath, [scriptblock]$OnStatus = {}, [scriptblock]$Pulse = {}) {
    $all = @(Get-Process -Name GGSystemMonitor -ErrorAction SilentlyContinue)
    if (-not $all.Count) { return }
    $signal = $null
    try {
        $ours = @($all | Where-Object { -not $_.HasExited -and $_.Path -eq $ExePath })
        if (-not $ours.Count) { return }
        # The monitor stop event is per user, not per install directory.
        # Don't signal it when another copy may be running in this session.
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if ($ours.Count -eq $all.Count) {
            try {
                $signal = [Threading.EventWaitHandle]::OpenExisting("Local\GGSystemMonitor.Stop.$sid")
                $signal.Set() | Out-Null
            } catch [Threading.WaitHandleCannotBeOpenedException] {}
            catch { & $OnStatus "Graceful stop signal unavailable: $($_.Exception.Message)" | Out-Null }
        }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        foreach ($process in $ours) {
            if ($process.HasExited) { continue }
            $closed = $process.CloseMainWindow()
            if (($closed -or $signal) -and (Wait-SetupProcess $process ([int][Math]::Max(0, 5000 - $clock.ElapsedMilliseconds)) $Pulse)) { continue }
            if (-not $process.HasExited) {
                & $OnStatus "Closing PID $($process.Id) after it did not exit gracefully." | Out-Null
                try { $process.Kill() } catch { if (-not $process.HasExited) { throw } }
                if (-not (Wait-SetupProcess $process 2000 $Pulse)) { throw "GGSystemMonitor did not stop. Close it or restart Windows and retry." }
            }
        }
    } finally {
        if ($signal) { $signal.Dispose() }
        foreach ($process in $all) { $process.Dispose() }
    }
}

function Wait-SetupProcess($Process, [int]$Milliseconds, [scriptblock]$Pulse) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remaining = [Math]::Max(0, $Milliseconds - $clock.ElapsedMilliseconds)
        if ($Process.WaitForExit([int][Math]::Min(150, $remaining))) { return $true }
        & $Pulse | Out-Null
    } while ($clock.ElapsedMilliseconds -lt $Milliseconds)
    return $false
}

function Assert-UnlinkedSetupPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^[A-Za-z]:\\') { throw "Choose a local installation folder." }
    for ($part = $full; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if (Test-Path -LiteralPath $part) {
            $item = Get-Item -LiteralPath $part -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Setup will not modify a linked file or directory: '$part'."
            }
        }
    }
}

function Invoke-LegacyDriverCommand([string]$Action, [string]$Name, [scriptblock]$Pulse = {}) {
    $process = Start-Process -FilePath "$env:WINDIR\System32\sc.exe" -ArgumentList @($Action, ('"{0}"' -f $Name)) -WindowStyle Hidden -PassThru -ErrorAction Stop
    try {
        if (-not (Wait-SetupProcess $process 5000 $Pulse)) { throw "Windows timed out trying to $Action legacy service '$Name'." }
        $code = $process.ExitCode
        if ($code -notin @(0, 1060, 1062, 1072)) { throw "Could not $Action legacy service '$Name' (Windows error $code)." }
    } finally { $process.Dispose() }
}

function Find-LegacyDriverServices([string]$ExpectedPath, [scriptblock]$Pulse) {
    # Local service registry only, no WMI/CIM or machine-wide driver operations.
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    try {
        $services = $base.OpenSubKey('SYSTEM\CurrentControlSet\Services')
        if (-not $services) { return }
        try {
            foreach ($name in $services.GetSubKeyNames()) {
                & $Pulse | Out-Null
                if ($name -eq 'PawnIO' -or $name -match '["\x00-\x1f]') { continue }
                $key = $null
                try {
                    $key = $services.OpenSubKey($name)
                    if (-not $key -or $key.GetValue('Type') -notin @(1, 2)) { continue }
                    $image = [Environment]::ExpandEnvironmentVariables(([string]$key.GetValue('ImagePath')).Trim().Trim('"'))
                    if ($image.StartsWith('\??\')) { $image = $image.Substring(4) }
                    if ($image.StartsWith('\SystemRoot\', [StringComparison]::OrdinalIgnoreCase)) {
                        $image = Join-Path $env:SystemRoot $image.Substring(12)
                    } elseif ($image.StartsWith('System32\', [StringComparison]::OrdinalIgnoreCase)) {
                        $image = Join-Path $env:SystemRoot $image
                    }
                    if ($image -match '^[A-Za-z]:\\' -and [IO.Path]::GetFullPath($image) -eq $ExpectedPath) { $name }
                } catch [Security.SecurityException] {}
                catch [UnauthorizedAccessException] {}
                catch [ArgumentException] {}
                finally { if ($key) { $key.Dispose() } }
            }
        } finally { $services.Dispose() }
    } finally { $base.Dispose() }
}

function Remove-LegacyDriverFile([string]$Path, [scriptblock]$Pulse) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            if (Test-Path -LiteralPath $Path -ErrorAction Stop) {
                Assert-UnlinkedSetupPath $Path
                Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
            }
            return
        } catch {
            if ($clock.ElapsedMilliseconds -ge 3500) { throw }
            & $Pulse | Out-Null
            Start-Sleep -Milliseconds 150
        }
    } while ($true)
}

function Clear-LegacyAppDriver([string]$TargetDir, [scriptblock]$OnStatus = {}, [scriptblock]$Pulse = {}) {
    $path = Join-Path $TargetDir 'GGSystemMonitor.sys'
    if (-not (Test-Path -LiteralPath $path -ErrorAction Stop)) { return }
    Assert-UnlinkedSetupPath $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf -ErrorAction Stop)) {
        throw "Expected a regular legacy driver file, not a directory: '$path'."
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $serviceWarning = $null
    try {
        foreach ($name in @(Find-LegacyDriverServices $path $Pulse)) {
            if ($clock.ElapsedMilliseconds -ge 12000) { throw 'The legacy service cleanup time limit was reached.' }
            & $OnStatus "Releasing legacy driver service '$name'..." | Out-Null
            foreach ($action in @('stop', 'delete')) {
                try { Invoke-LegacyDriverCommand $action $name $Pulse }
                catch {
                    $serviceWarning = $_.Exception.Message
                    & $OnStatus $serviceWarning | Out-Null
                }
            }
        }
    } catch {
        $serviceWarning = $_.Exception.Message
        & $OnStatus $serviceWarning | Out-Null
    }
    try {
        # Keep the file available for another cleanup attempt if service
        # release failed; don't make an incomplete uninstall look complete.
        if (-not $serviceWarning) { Remove-LegacyDriverFile $path $Pulse }
    } catch {
        $detail = "Could not remove '$path' (0x{0:X8}): {1}" -f $_.Exception.HResult, $_.Exception.Message
        & $OnStatus $detail | Out-Null
        return "$detail Restart Windows and retry. Setup did not change permissions or security settings."
    }
    if ($serviceWarning) { return "$serviceWarning Restart Windows and retry to finish legacy driver cleanup." }
}

function Invoke-AppInstallation($Options, $Messages) {
    $ErrorActionPreference = "Stop"
    $SourceDir = $Options.SourceDir
    $InstallDir = Resolve-SetupDestination $Options.InstallDir $SourceDir "GGSystemMonitor.exe"
    Assert-UnlinkedSetupPath $InstallDir
    $isUpdate = $Options.IsUpdate
    $isSameVersion = $Options.IsSameVersion
    $AppName = "GGSystemMonitor"
    $ExeName = "$AppName.exe"
    $ExePath = Join-Path $InstallDir $ExeName
    $IconPath = Join-Path $InstallDir "GGSystemMonitor_icon.ico"
    $TaskName = $AppName
    $Publisher = "DaveTheTopDev"
    $UninstallRegKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
    $report = { param($Percent, $Message) $Messages.Enqueue(@{ Percent = $Percent; Message = $Message }) }

    & $report 3 "Checking offline prerequisites..."
    $prerequisites = Invoke-OfflinePrerequisites (Join-Path $SourceDir "Prerequisites") {
        param($message) & $report 8 $message
    }

    & $report 18 "Closing running copies of this installation..."
    Stop-InstalledMonitor $ExePath { param($message) & $report 18 $message }

    & $report 25 "Checking for legacy driver files..."
    $legacyNotice = Clear-LegacyAppDriver $InstallDir { param($message) & $report 25 $message }

    & $report 40 "Checking application files for locks..."
    $files = @(Get-ChildItem -LiteralPath $SourceDir -File | Where-Object {
        $_.Extension -ne ".sys" -and ($_.Name -ne "settings.json" -or -not (Test-Path -LiteralPath (Join-Path $InstallDir $_.Name)))
    } | Sort-Object { $_.Name -eq $ExeName })
    foreach ($file in $files) {
        $destination = Join-Path $InstallDir $file.Name
        if (Test-Path -LiteralPath $destination) {
            Assert-UnlinkedSetupPath $destination
            try { [IO.File]::Open($destination, "Open", "ReadWrite", "None").Dispose() }
            catch { throw "Cannot replace '$($file.Name)'. Close the application or restart Windows and retry. $($_.Exception.Message)" }
        }
    }

    & $report 52 "Copying application files..."
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    foreach ($file in $files) {
        Copy-Item -LiteralPath $file.FullName -Destination $InstallDir -Force
    }
    # Write Uninstall.bat into the install folder
    $uninstallBat = Join-Path $InstallDir "Uninstall.bat"
    "@echo off`npowershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""%~dp0Install.ps1"" -Uninstall" |
        Set-Content $uninstallBat -Encoding ASCII
    & $report 62 "Configuring startup..."

    # Scheduled task is an install-time preference. Updates preserve the
    # user's existing startup choice instead of recreating it implicitly.
    $notice = $prerequisites.Notice
    if ($legacyNotice) { $notice = ("$notice Legacy driver cleanup is incomplete. $legacyNotice").Trim() }
    try {
        if (-not $isUpdate) {
            if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
                Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
            }
            if ($Options.AutoStart) {
                $action    = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory $InstallDir
                $trigger   = New-ScheduledTaskTrigger -AtLogon -User ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
                $settings  = New-ScheduledTaskSettingsSet `
                                 -AllowStartIfOnBatteries `
                                 -DontStopIfGoingOnBatteries `
                                 -ExecutionTimeLimit ([System.TimeSpan]::Zero) `
                                 -MultipleInstances IgnoreNew
                $principal = New-ScheduledTaskPrincipal `
                                 -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) `
                                 -LogonType Interactive `
                                 -RunLevel Highest
                Register-ScheduledTask `
                    -TaskName    $TaskName `
                    -Action      $action `
                    -Trigger     $trigger `
                    -Settings    $settings `
                    -Principal   $principal `
                    -Description "Displays CPU and GPU temperatures on SteelSeries keyboard OLED. https://github.com/$Publisher/$AppName" `
                    | Out-Null
            }
        }
    } catch {
        $notice = ($notice + " Automatic startup could not be configured. You can still open GGSystemMonitor manually. " + $_.Exception.Message).Trim()
        & $report 70 $notice
    }
    & $report 74 "Registering GGSystemMonitor..."

    # Add / Remove Programs registry entry
    $installedVer = Get-AppVersion $ExePath
    New-Item -Path $UninstallRegKey -Force | Out-Null
    $displayIcon = if (Test-Path $IconPath) { $IconPath } else { $ExePath }

    Set-ItemProperty -Path $UninstallRegKey -Name "DisplayName"     -Value $AppName
    Set-ItemProperty -Path $UninstallRegKey -Name "DisplayVersion"  -Value $installedVer
    Set-ItemProperty -Path $UninstallRegKey -Name "Publisher"       -Value $Publisher
    Set-ItemProperty -Path $UninstallRegKey -Name "InstallLocation" -Value $InstallDir
    Set-ItemProperty -Path $UninstallRegKey -Name "DisplayIcon"     -Value $displayIcon
    Set-ItemProperty -Path $UninstallRegKey -Name "UninstallString" -Value "cmd.exe /c `"$uninstallBat`""
    Set-ItemProperty -Path $UninstallRegKey -Name "NoModify"        -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallRegKey -Name "NoRepair"        -Value 1 -Type DWord
    & $report 84 "Updating shortcuts..."

    $shortcutIcon = if (Test-Path $IconPath) { "$IconPath,0" } else { "$ExePath,0" }

    # Refresh existing shortcuts on updates. Repair also restores the canonical
    # Settings entry if it was lost; a normal update does not add opted-out links.
    $lnkPath = if ($env:APPDATA) { Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk" }
    $desktopFolder = [Environment]::GetFolderPath("DesktopDirectory")
    $deskLnkPath = if ($desktopFolder) { Join-Path $desktopFolder "GG System Monitor.lnk" }
    $refreshStart = $lnkPath -and ((-not $isUpdate -and $Options.StartMenu) -or
        ($isUpdate -and ((Test-Path -LiteralPath $lnkPath -ErrorAction SilentlyContinue) -or $isSameVersion)))
    $refreshDesktop = $deskLnkPath -and ((-not $isUpdate -and $Options.Desktop) -or
        ($isUpdate -and (Test-Path -LiteralPath $deskLnkPath -ErrorAction SilentlyContinue)))
    if ((-not $lnkPath -and ((-not $isUpdate -and $Options.StartMenu) -or $isSameVersion)) -or
        (-not $deskLnkPath -and -not $isUpdate -and $Options.Desktop)) {
        $notice = ($notice + " Windows did not provide a folder for one of the requested shortcuts. You can still launch GGSystemMonitor from its install folder.").Trim()
    }
    foreach ($shortcutPath in @(
        $(if ($refreshStart) { $lnkPath }),
        $(if ($refreshDesktop) { $deskLnkPath })
    )) {
        if (-not $shortcutPath) { continue }
        $shell = $null; $lnk = $null; $temporaryLink = $null
        try {
            $parent = Split-Path -Parent $shortcutPath
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            # Save a brand-new link to clear stale Run as administrator flags.
            # Leave the current shortcut intact if creation fails.
            $temporaryLink = Join-Path $parent ("GGSystemMonitor-" + [guid]::NewGuid().ToString("N") + ".lnk")
            $shell = New-Object -ComObject WScript.Shell
            $lnk = $shell.CreateShortcut($temporaryLink)
            $lnk.TargetPath = $ExePath
            $lnk.Arguments = "--settings"
            $lnk.WorkingDirectory = $InstallDir
            $lnk.Description = "GGSystemMonitor Settings"
            $lnk.IconLocation = $shortcutIcon
            $lnk.Save()
            if ([IO.File]::Exists($shortcutPath)) {
                [IO.File]::Replace($temporaryLink, $shortcutPath, [Management.Automation.Language.NullString]::Value)
            }
            else { [IO.File]::Move($temporaryLink, $shortcutPath) }
        } catch {
            $notice = ($notice + " Could not update the shortcut at '$shortcutPath'. " + $_.Exception.Message).Trim()
        } finally {
            if ($lnk) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($lnk) | Out-Null }
            if ($shell) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
            if ($temporaryLink) { Remove-Item -LiteralPath $temporaryLink -Force -ErrorAction SilentlyContinue }
        }
    }
    & $report 96 "Finishing setup..."


    return [pscustomobject]@{
        Version = $installedVer
        InstallDir = $InstallDir
        RestartRequired = $prerequisites.RestartRequired
        Notice = $notice
    }
}
