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

function Stop-InstalledMonitor([string]$ExePath) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remaining = $false
        foreach ($process in @(Get-Process -Name GGSystemMonitor -ErrorAction SilentlyContinue)) {
            try {
                if ($process.Path -ne $ExePath -or $process.HasExited) { continue }
                $process.Kill()
                if (-not $process.WaitForExit(500)) { $remaining = $true }
            } catch {
                if (-not $process.HasExited) { throw "Could not stop the installed GGSystemMonitor process: $($_.Exception.Message)" }
            } finally { $process.Dispose() }
        }
        if (-not $remaining) { return }
    } while ($deadline.Elapsed.TotalSeconds -lt 5)
    throw "GGSystemMonitor did not stop. Close it or restart Windows, then retry setup."
}

function Invoke-LegacyDriverCommand([string]$Action, [string]$Name) {
    $process = Start-Process -FilePath "$env:WINDIR\System32\sc.exe" -ArgumentList @($Action, ('"{0}"' -f $Name)) -WindowStyle Hidden -PassThru -ErrorAction Stop
    try {
        if (-not $process.WaitForExit(8000)) { throw "Windows did not finish trying to $Action legacy driver '$Name'. Restart Windows and try again." }
        $code = $process.ExitCode
        if ($code -notin @(0, 1060, 1062, 1072)) { throw "Could not $Action legacy driver '$Name' (Windows error $code). Restart Windows and try again." }
    } finally { $process.Dispose() }
}

function Stop-HardwareMonitorDrivers([string]$TargetDir) {
    if (-not (Test-Path -LiteralPath $TargetDir -PathType Container)) { return }
    # Avoid a machine-wide WMI query; only inspect expected legacy services.
    $names = @("GGSystemMonitor", "LibreHardwareMonitorLib", "OpenHardwareMonitorLib", "WinRing0_1_2_0") +
        @(Get-ChildItem -LiteralPath $TargetDir -Filter "*.sys" -File -ErrorAction Stop | ForEach-Object BaseName)
    $prefix = [IO.Path]::GetFullPath($TargetDir).TrimEnd('\') + '\'
    foreach ($name in ($names | Select-Object -Unique)) {
        if ($name -eq "PawnIO") { continue } # Shared driver, not part of app cleanup.
        $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
        if (-not (Test-Path -LiteralPath $key)) { continue }
        $driver = Get-ItemProperty -LiteralPath $key -ErrorAction Stop
        if ($driver.Type -notin @(1, 2)) { continue }
        $image = [Environment]::ExpandEnvironmentVariables(([string]$driver.ImagePath).Trim().Trim('"'))
        if ($image.StartsWith('\??\')) { $image = $image.Substring(4) }
        if ($image.StartsWith('\SystemRoot\', [StringComparison]::OrdinalIgnoreCase)) {
            $image = Join-Path $env:SystemRoot $image.Substring(12)
        } elseif ($image.StartsWith('System32\', [StringComparison]::OrdinalIgnoreCase)) {
            $image = Join-Path $env:SystemRoot $image
        }
        if ($image -notmatch '^[A-Za-z]:\\') { continue }
        $image = [IO.Path]::GetFullPath($image)
        if (-not $image.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        Invoke-LegacyDriverCommand stop $name
        Invoke-LegacyDriverCommand delete $name
    }
}

function Remove-SysFilesWithRetry([string]$TargetDir) {
    if (-not (Test-Path -LiteralPath $TargetDir -PathType Container)) { return }
    foreach ($file in @(Get-ChildItem -LiteralPath $TargetDir -Filter "*.sys" -File -ErrorAction Stop | Where-Object Name -ne "PawnIO.sys")) {
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        do {
            try {
                if (Test-Path -LiteralPath $file.FullName) { Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop }
                break
            } catch {
                if ($deadline.Elapsed.TotalSeconds -ge 5) { throw "Unable to remove legacy driver '$($file.Name)'. Restart Windows and retry. $($_.Exception.Message)" }
                Start-Sleep -Milliseconds 250
            }
        } while ($true)
    }
}

function Invoke-AppInstallation($Options, $Messages) {
    $ErrorActionPreference = "Stop"
    $SourceDir = $Options.SourceDir
    $InstallDir = Resolve-SetupDestination $Options.InstallDir $SourceDir "GGSystemMonitor.exe"
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
    Stop-InstalledMonitor $ExePath

    & $report 25 "Checking for legacy driver files..."
    Stop-HardwareMonitorDrivers $InstallDir
    Remove-SysFilesWithRetry $InstallDir

    & $report 40 "Checking application files for locks..."
    $files = @(Get-ChildItem -LiteralPath $SourceDir -File | Where-Object {
        $_.Name -ne "settings.json" -or -not (Test-Path -LiteralPath (Join-Path $InstallDir $_.Name))
    } | Sort-Object { $_.Name -eq $ExeName })
    foreach ($file in $files) {
        $destination = Join-Path $InstallDir $file.Name
        if (Test-Path -LiteralPath $destination) {
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
