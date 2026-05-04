<#
.SYNOPSIS
    GGSystemMonitor Installer, Updater, and Uninstaller.
.PARAMETER Uninstall
    Removes GGSystemMonitor from the system completely (silent - called by Uninstall.bat).
#>
param([switch]$Uninstall, [string]$DoneFile = "", [int]$ExcludePid = 0)

$AppName         = "GGSystemMonitor"
$ExeName         = "GGSystemMonitor.exe"
$IconName        = "GGSystemMonitor_icon.ico"
$Publisher       = "DaveTheTopDev"
$InstallDir      = Join-Path $env:LOCALAPPDATA $AppName
$ExePath         = Join-Path $InstallDir $ExeName
$IconPath        = Join-Path $InstallDir $IconName
$TaskName        = $AppName
$UninstallRegKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"

if ($Uninstall -and $PSCommandPath) {
    $InstallDir = Split-Path -Parent $PSCommandPath
    $ExePath    = Join-Path $InstallDir $ExeName
    $IconPath   = Join-Path $InstallDir $IconName
}

function Get-NormalizedDriverPath([string]$path) {
    if (-not $path) { return "" }

    $clean = $path.Trim().Trim('"')
    if ($clean.StartsWith("\??\")) { $clean = $clean.Substring(4) }
    if ($clean.StartsWith("\SystemRoot\", [System.StringComparison]::OrdinalIgnoreCase)) {
        $clean = Join-Path $env:WINDIR $clean.Substring(12)
    }
    if ($clean.StartsWith("System32\", [System.StringComparison]::OrdinalIgnoreCase)) {
        $clean = Join-Path $env:WINDIR $clean
    }

    return $clean
}

function Get-HardwareMonitorDriverServices([string]$TargetDir) {
    $targetFullPath = ""
    if ($TargetDir -and (Test-Path $TargetDir)) {
        try { $targetFullPath = [System.IO.Path]::GetFullPath($TargetDir).TrimEnd('\') } catch {}
    }

    $knownNames = @(
        "LibreHardwareMonitorLib",
        "OpenHardwareMonitorLib",
        $AppName,
        [System.IO.Path]::GetFileNameWithoutExtension($ExeName)
    ) | Where-Object { $_ } | Select-Object -Unique

    $services = @{}
    foreach ($name in $knownNames) {
        $svc = Get-CimInstance Win32_SystemDriver -Filter "Name='$name'" -ErrorAction SilentlyContinue
        if ($svc) { $services[$svc.Name] = $svc }
    }

    Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue | ForEach-Object {
        $driverPath = Get-NormalizedDriverPath $_.PathName
        $isAppDriver =
            $knownNames -contains $_.Name -or
            $_.Name -match "GGSystemMonitor" -or
            $_.DisplayName -match "GGSystemMonitor"
        $isInInstallDir =
            $targetFullPath -and
            $driverPath -and
            $driverPath.StartsWith($targetFullPath, [System.StringComparison]::OrdinalIgnoreCase)

        if ($isAppDriver -or $isInInstallDir) {
            $services[$_.Name] = $_
        }
    }

    return @($services.Values)
}

function Stop-HardwareMonitorDrivers([string]$TargetDir) {
    $drivers = @(Get-HardwareMonitorDriverServices $TargetDir)
    foreach ($driver in $drivers) {
        $name = $driver.Name
        if (-not $name) { continue }

        $state = $driver.State
        if ($state -and $state -ne "Stopped") {
            sc.exe stop $name | Out-Null
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                $current = Get-CimInstance Win32_SystemDriver -Filter "Name='$name'" -ErrorAction SilentlyContinue
                if (-not $current -or $current.State -eq "Stopped") { break }
                Start-Sleep -Milliseconds 250
            }
        }

        sc.exe delete $name | Out-Null
    }

    Start-Sleep -Milliseconds 1000
}

function Remove-SysFilesWithRetry([string]$TargetDir) {
    if (-not (Test-Path $TargetDir)) { return }

    $sysFiles = @(Get-ChildItem $TargetDir -Filter "*.sys" -Recurse -ErrorAction SilentlyContinue)
    foreach ($sysFile in $sysFiles) {
        $retries = 40
        while ($retries -gt 0 -and (Test-Path $sysFile.FullName)) {
            try {
                Remove-Item $sysFile.FullName -Force -ErrorAction Stop
                break
            } catch {
                Stop-HardwareMonitorDrivers $TargetDir
                $retries--
                if ($retries -gt 0) { Start-Sleep -Milliseconds 500 }
            }
        }

        if (Test-Path $sysFile.FullName) {
            throw "Unable to delete locked driver file '$($sysFile.FullName)'. Restart Windows and run uninstall again."
        }
    }
}

# -- Self-elevate silently (no console window) --
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $psArgs = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`""
    if ($Uninstall)    { $psArgs += " -Uninstall" }
    if ($DoneFile)     { $psArgs += " -DoneFile `"$DoneFile`"" }
    if ($ExcludePid)   { $psArgs += " -ExcludePid $ExcludePid" }
    Start-Process powershell.exe $psArgs -Verb RunAs
    exit
}

# ===========================================================================
#  UNINSTALL  (silent -- confirmation was already shown by SettingsForm)
# ===========================================================================
if ($Uninstall) {
    function Set-DoneFile([string]$value) {
        if ($DoneFile) { try { [System.IO.File]::WriteAllText($DoneFile, $value) } catch {} }
    }

    $uninstallError = $null
    try {
        Set-DoneFile "5"
        $procName = [System.IO.Path]::GetFileNameWithoutExtension($ExeName)
        Get-Process -Name $procName -ErrorAction SilentlyContinue |
            Where-Object { $ExcludePid -eq 0 -or $_.Id -ne $ExcludePid } |
            Stop-Process -Force -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline) {
            $remaining = @(Get-Process -Name $procName -ErrorAction SilentlyContinue |
                Where-Object { $ExcludePid -eq 0 -or $_.Id -ne $ExcludePid })
            if ($remaining.Count -eq 0) { break }
            Start-Sleep -Milliseconds 200
        }
        Set-DoneFile "20"

        Stop-HardwareMonitorDrivers $InstallDir
        Set-DoneFile "35"

        Remove-SysFilesWithRetry $InstallDir
        Set-DoneFile "55"

        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        }
        Set-DoneFile "65"

        if (Test-Path $UninstallRegKey) { Remove-Item $UninstallRegKey -Recurse -Force }

        $lnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
        if (Test-Path $lnk) { Remove-Item $lnk -Force }

        $deskLnk = Join-Path ([System.Environment]::GetFolderPath('Desktop')) "GG System Monitor.lnk"
        if (Test-Path $deskLnk) { Remove-Item $deskLnk -Force }
        $deskLnkLegacy = Join-Path ([System.Environment]::GetFolderPath('Desktop')) "$AppName.lnk"
        if (Test-Path $deskLnkLegacy) { Remove-Item $deskLnkLegacy -Force }
        Set-DoneFile "75"

        # Delete everything we can right now (non-script files will succeed)
        if (Test-Path $InstallDir) {
            Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        # If the folder still exists it is because PowerShell holds a handle on
        # Install.ps1 while executing it.  Schedule a deferred rmdir that runs
        # ~3 seconds after this script exits and releases the lock.
        if (Test-Path $InstallDir) {
            $rmArgs = "/c timeout /t 15 /nobreak >nul 2>&1 && rmdir /s /q `"$InstallDir`""
            Start-Process -FilePath "cmd.exe" -ArgumentList $rmArgs -WindowStyle Hidden
        }

        Set-DoneFile "DONE"
    } catch {
        $uninstallError = "$_"
        Set-DoneFile "ERROR:$uninstallError"
    }

    # When called from SettingsForm (-DoneFile set) it handles UI; otherwise show a MessageBox
    if (-not $DoneFile) {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.Application]::EnableVisualStyles()
        if ($uninstallError) {
            [System.Windows.Forms.MessageBox]::Show(
                "Uninstall failed:`n$uninstallError",
                "Uninstall Failed",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        } else {
            [System.Windows.Forms.MessageBox]::Show(
                "GGSystemMonitor has been successfully uninstalled.",
                "Uninstall Complete",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
        }
    }
    exit 0
}

# ===========================================================================
#  INSTALL / UPDATE  -- GUI
# ===========================================================================
$SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# Validate payload source. In release packages this script lives in the
# GGSystemMonitor subfolder next to the files it installs.
if (-not (Test-Path (Join-Path $SourceDir $ExeName))) {
    [System.Windows.Forms.MessageBox]::Show(
        "$ExeName was not found in:`n  $SourceDir`n`nEnsure the release folder contains Install.bat next to a GGSystemMonitor folder, and that Install.ps1 is inside that folder with $ExeName.",
        "Installation Error",
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    exit 1
}

# Version detection
function Get-AppVersion($path) {
    try {
        $raw = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
        $p   = ($raw -split '\.')
        if ($p.Count -ge 3) { return "$($p[0]).$($p[1]).$($p[2])" }
        return $raw
    } catch { return "" }
}

$script:isUpdate      = (Test-Path $UninstallRegKey) -and (Test-Path $ExePath)
$newVer               = Get-AppVersion (Join-Path $SourceDir $ExeName)
$curVer               = if ($script:isUpdate) { Get-AppVersion $ExePath } else { "" }
$script:isSameVersion = $script:isUpdate -and $newVer -ne "" -and $curVer -eq $newVer

# -- Colours --
$clrBg        = [System.Drawing.Color]::FromArgb(245, 245, 248)
$clrHeader    = [System.Drawing.Color]::FromArgb(18,  18,  28)
$clrSubtitle  = [System.Drawing.Color]::FromArgb(148, 148, 180)
$clrFooter    = [System.Drawing.Color]::FromArgb(228, 228, 236)
$clrBlue      = [System.Drawing.Color]::FromArgb(0,   120, 215)
$clrGreen     = [System.Drawing.Color]::FromArgb(16,  148,  54)
$clrBannerFg  = [System.Drawing.Color]::FromArgb(20,  25,  55)

$clrBannerBg  = if ($script:isSameVersion) { [System.Drawing.Color]::FromArgb(238, 238, 244) } `
                elseif ($script:isUpdate)   { [System.Drawing.Color]::FromArgb(255, 244, 222) } `
                else                        { [System.Drawing.Color]::FromArgb(232, 240, 255) }
$clrBannerIcon = if ($script:isSameVersion) { [System.Drawing.Color]::FromArgb(130, 130, 150) } `
                 elseif ($script:isUpdate)  { [System.Drawing.Color]::FromArgb(186, 110,   0) } `
                 else                       { $clrBlue }

# -- Form --
$form                 = New-Object System.Windows.Forms.Form
$form.Text            = "GGSystemMonitor Installer"
$form.ClientSize      = New-Object System.Drawing.Size(480, 430)
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
$form.StartPosition   = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.MaximizeBox     = $false
$form.MinimizeBox     = $false
$form.BackColor       = $clrBg
$form.Font            = New-Object System.Drawing.Font("Segoe UI", 9)

# -- Header --
$header           = New-Object System.Windows.Forms.Panel
$header.Dock      = [System.Windows.Forms.DockStyle]::Top
$header.Height    = 84
$header.BackColor = $clrHeader

$lblTitle          = New-Object System.Windows.Forms.Label
$lblTitle.Text     = "GGSystemMonitor"
$lblTitle.Font     = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
$lblTitle.ForeColor = [System.Drawing.Color]::White
$lblTitle.AutoSize = $true
$lblTitle.Location = New-Object System.Drawing.Point(20, 11)
$header.Controls.Add($lblTitle)

$lblSub           = New-Object System.Windows.Forms.Label
$lblSub.Text      = if ($script:isSameVersion) { "v$curVer - Already installed" } `
                    elseif ($script:isUpdate)   { "v$curVer --> v$newVer" } `
                    else                        { "v$newVer - Keyboard OLED System Monitor" }
$lblSub.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
$lblSub.ForeColor = $clrSubtitle
$lblSub.AutoSize  = $true
$lblSub.Location  = New-Object System.Drawing.Point(22, 52)
$header.Controls.Add($lblSub)

$form.Controls.Add($header)

# -- Status banner --
$banner           = New-Object System.Windows.Forms.Panel
$banner.Location  = New-Object System.Drawing.Point(0, 84)
$banner.Size      = New-Object System.Drawing.Size(480, 62)
$banner.BackColor = $clrBannerBg

$picBannerIcon          = New-Object System.Windows.Forms.PictureBox
$picBannerIcon.Size     = New-Object System.Drawing.Size(32, 32)
$picBannerIcon.Location = New-Object System.Drawing.Point(16, 15)
$picBannerIcon.SizeMode = [System.Windows.Forms.PictureBoxSizeMode]::Zoom
$srcExePath = Join-Path $SourceDir $ExeName
if (Test-Path $srcExePath) {
    $appIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($srcExePath)
    $picBannerIcon.Image = $appIcon.ToBitmap()
}
$banner.Controls.Add($picBannerIcon)

$lblBannerText           = New-Object System.Windows.Forms.Label
$lblBannerText.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
$lblBannerText.ForeColor = $clrBannerFg
$lblBannerText.AutoSize  = $false
$lblBannerText.Size      = New-Object System.Drawing.Size(420, 38)
$lblBannerText.Location  = New-Object System.Drawing.Point(52, 15)
$lblBannerText.Text      = if ($script:isSameVersion) {
    "GGSystemMonitor v$curVer is already installed.`nThis version is up to date - no update is required."
} elseif ($script:isUpdate) {
    "GGSystemMonitor v$curVer is already installed.`nClick Update to install version $newVer."
} else {
    "GGSystemMonitor will be installed to your user profile folder.`nNo administrator rights are required after this one-time setup."
}
$banner.Controls.Add($lblBannerText)
$form.Controls.Add($banner)

# -- Divider --
$divider           = New-Object System.Windows.Forms.Panel
$divider.Location  = New-Object System.Drawing.Point(0, 146)
$divider.Size      = New-Object System.Drawing.Size(480, 1)
$divider.BackColor = [System.Drawing.Color]::FromArgb(210, 210, 218)
$form.Controls.Add($divider)

# -- Install path --
$lblPathCaption           = New-Object System.Windows.Forms.Label
$lblPathCaption.Text      = "Install location:"
$lblPathCaption.Font      = New-Object System.Drawing.Font("Segoe UI", 8, [System.Drawing.FontStyle]::Bold)
$lblPathCaption.ForeColor = [System.Drawing.Color]::FromArgb(100, 100, 115)
$lblPathCaption.BackColor = $clrBg
$lblPathCaption.AutoSize  = $true
$lblPathCaption.Location  = New-Object System.Drawing.Point(20, 154)
$form.Controls.Add($lblPathCaption)

$txtInstallPath          = New-Object System.Windows.Forms.TextBox
$txtInstallPath.Text     = $InstallDir
$txtInstallPath.Font     = New-Object System.Drawing.Font("Segoe UI", 8)
$txtInstallPath.Location = New-Object System.Drawing.Point(20, 176)
$txtInstallPath.Size     = New-Object System.Drawing.Size(382, 22)
$txtInstallPath.Enabled  = -not $script:isUpdate
$form.Controls.Add($txtInstallPath)

$btnBrowseInstall          = New-Object System.Windows.Forms.Button
$btnBrowseInstall.Text     = "Browse"
$btnBrowseInstall.Location = New-Object System.Drawing.Point(408, 174)
$btnBrowseInstall.Size     = New-Object System.Drawing.Size(56, 24)
$btnBrowseInstall.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$btnBrowseInstall.Enabled  = -not $script:isUpdate
$btnBrowseInstall.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(185, 185, 198)
$form.Controls.Add($btnBrowseInstall)

# -- Options group --
$optGroup           = New-Object System.Windows.Forms.GroupBox
$optGroup.Text      = "Options"
$optGroup.Location  = New-Object System.Drawing.Point(16, 210)
$optGroup.Size      = New-Object System.Drawing.Size(448, 120)
$optGroup.ForeColor = [System.Drawing.Color]::FromArgb(50, 50, 68)
$form.Controls.Add($optGroup)

$chkAutoStart          = New-Object System.Windows.Forms.CheckBox
$chkAutoStart.Text     = "Start automatically when Windows starts"
$chkAutoStart.AutoSize = $true
$chkAutoStart.Checked  = $true
$chkAutoStart.Location = New-Object System.Drawing.Point(14, 24)
$optGroup.Controls.Add($chkAutoStart)

$chkShortcut          = New-Object System.Windows.Forms.CheckBox
$chkShortcut.Text     = "Create a Start Menu shortcut"
$chkShortcut.AutoSize = $true
$chkShortcut.Checked  = $true
$chkShortcut.Location = New-Object System.Drawing.Point(14, 56)
$optGroup.Controls.Add($chkShortcut)

$chkDesktop          = New-Object System.Windows.Forms.CheckBox
$chkDesktop.Text     = "Create a Desktop shortcut"
$chkDesktop.AutoSize = $true
$chkDesktop.Checked  = $false
$chkDesktop.Location = New-Object System.Drawing.Point(14, 88)
$optGroup.Controls.Add($chkDesktop)

$progressBar          = New-Object System.Windows.Forms.ProgressBar
$progressBar.Location = New-Object System.Drawing.Point(16, 346)
$progressBar.Size     = New-Object System.Drawing.Size(448, 16)
$progressBar.Minimum  = 0
$progressBar.Maximum  = 100
$progressBar.Value    = 0
$progressBar.Style    = [System.Windows.Forms.ProgressBarStyle]::Continuous
$form.Controls.Add($progressBar)

# -- Footer --
$footer           = New-Object System.Windows.Forms.Panel
$footer.Dock      = [System.Windows.Forms.DockStyle]::Bottom
$footer.Height    = 54
$footer.BackColor = $clrFooter
$form.Controls.Add($footer)

$btnInstall           = New-Object System.Windows.Forms.Button
$btnInstall.Text      = if ($script:isSameVersion) { "Up to Date" } elseif ($script:isUpdate) { "Update" } else { "Install" }
$btnInstall.Size      = New-Object System.Drawing.Size(100, 32)
$btnInstall.Location  = New-Object System.Drawing.Point(366, 11)
$btnInstall.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$btnInstall.BackColor = if ($script:isSameVersion) { [System.Drawing.Color]::FromArgb(180, 180, 195) } else { $clrBlue }
$btnInstall.ForeColor = [System.Drawing.Color]::White
$btnInstall.Enabled   = -not $script:isSameVersion
$btnInstall.Font      = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
$btnInstall.FlatAppearance.BorderSize = 0
$footer.Controls.Add($btnInstall)

$btnCancel           = New-Object System.Windows.Forms.Button
$btnCancel.Text      = "Cancel"
$btnCancel.Size      = New-Object System.Drawing.Size(88, 32)
$btnCancel.Location  = New-Object System.Drawing.Point(268, 11)
$btnCancel.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$btnCancel.BackColor = $clrBg
$btnCancel.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(185, 185, 198)
$footer.Controls.Add($btnCancel)

# -- Events --
$script:installDone = $false

$btnBrowseInstall.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description  = "Select installation folder"
    $dlg.SelectedPath = $txtInstallPath.Text
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtInstallPath.Text = $dlg.SelectedPath
    }
    $dlg.Dispose()
})

$btnCancel.Add_Click({ $form.Close() })

$btnInstall.Add_Click({
    # Second click (after install) -> close
    if ($script:installDone) { $form.Close(); return }

    $InstallDir = $txtInstallPath.Text.Trim()
    $ExePath    = Join-Path $InstallDir $ExeName

    $btnInstall.Text     = "Installing..."
    $btnInstall.Enabled  = $false
    $btnCancel.Enabled   = $false
    $chkAutoStart.Enabled  = $false
    $chkShortcut.Enabled   = $false
    $txtInstallPath.Enabled   = $false
    $btnBrowseInstall.Enabled = $false
    $chkDesktop.Enabled      = $false
    $form.Refresh()

    try {
        # Stop any running instance and wait for it to fully exit
        $procName = [System.IO.Path]::GetFileNameWithoutExtension($ExeName)
        Get-Process -Name $procName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline -and (Get-Process -Name $procName -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 200
        }
        $progressBar.Value = 15; $form.Refresh()

        # Stop and delete the kernel driver service to fully release the .sys file lock
        Stop-HardwareMonitorDrivers $InstallDir
        Remove-SysFilesWithRetry $InstallDir
        $progressBar.Value = 25; $form.Refresh()

        # Copy files (preserve existing settings.json on update)
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        $existingSettings = Test-Path (Join-Path $InstallDir "settings.json")
        Get-ChildItem -Path $SourceDir -File | ForEach-Object {
            if ($_.Name -eq "settings.json" -and $existingSettings) { return }
            try {
                Copy-Item $_.FullName -Destination $InstallDir -Force -ErrorAction Stop
            } catch {
                if ($_.Exception.Message -like "*being used by another process*") {
                    throw "A file from a previous installation is still locked by Windows:`n$($_.Exception.Message)`n`nPlease restart your computer and run the installer again."
                }
                throw
            }
        }
        $progressBar.Value = 55; $form.Refresh()

        # Write Uninstall.bat into the install folder
        $uninstallBat = Join-Path $InstallDir "Uninstall.bat"
        "@echo off`npowershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""%~dp0Install.ps1"" -Uninstall" |
            Set-Content $uninstallBat -Encoding ASCII
        $progressBar.Value = 62; $form.Refresh()

        # Scheduled task (always remove old one first, then recreate if checked)
        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        }
        if ($chkAutoStart.Checked) {
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
        $progressBar.Value = 74; $form.Refresh()

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
        $progressBar.Value = 84; $form.Refresh()

        $shortcutIcon = if (Test-Path $IconPath) { "$IconPath,0" } else { "$ExePath,0" }

        # Start Menu shortcut
        if ($chkShortcut.Checked) {
            $lnkPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
            $shell   = New-Object -ComObject WScript.Shell
            $lnk     = $shell.CreateShortcut($lnkPath)
            $lnk.TargetPath       = $ExePath
            $lnk.Arguments        = "--settings"
            $lnk.WorkingDirectory = $InstallDir
            $lnk.Description      = "GGSystemMonitor Settings"
            $lnk.IconLocation     = $shortcutIcon
            $lnk.Save()
        }
        $progressBar.Value = 92; $form.Refresh()

        # Desktop shortcut
        if ($chkDesktop.Checked) {
            $deskLnkPath = Join-Path ([System.Environment]::GetFolderPath('Desktop')) "GG System Monitor.lnk"
            $shell       = New-Object -ComObject WScript.Shell
            $lnk         = $shell.CreateShortcut($deskLnkPath)
            $lnk.TargetPath       = $ExePath
            $lnk.Arguments        = "--settings"
            $lnk.WorkingDirectory = $InstallDir
            $lnk.Description      = "GGSystemMonitor Settings"
            $lnk.IconLocation     = $shortcutIcon
            $lnk.Save()
        }
        $progressBar.Value = 96; $form.Refresh()

        # -- Success state --
        $script:installDone = $true
        $progressBar.Value  = 100
        $form.Refresh()

        # Use a timer so the UI thread stays free and the progress bar animation
        # can finish painting before the banner and close button appear.
        $completeTimer = New-Object System.Windows.Forms.Timer
        $completeTimer.Interval = 600
        $completeTimer.Add_Tick({
            param($s, $e)
            $s.Stop()
            $s.Dispose()

            $banner.BackColor   = [System.Drawing.Color]::FromArgb(220, 246, 228)
            $lblBannerText.Text = if ($script:isUpdate) {
                "GGSystemMonitor has been updated to v$installedVer successfully."
            } else {
                "GGSystemMonitor has been installed successfully.`nIt will start automatically at your next startup."
            }
            $btnInstall.Text      = "Close"
            $btnInstall.Enabled   = $true
            $btnInstall.BackColor = $clrGreen
            $form.Refresh()

            # Launch the app
            if (-not $script:isUpdate) {
                if ($chkAutoStart.Checked) {
                    Start-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
                } else {
                    Start-Process -FilePath $ExePath -WorkingDirectory $InstallDir -ErrorAction SilentlyContinue
                }
                Start-Process -FilePath $ExePath -ArgumentList "--settings" -WorkingDirectory $InstallDir -ErrorAction SilentlyContinue
            } else {
                Start-Process -FilePath $ExePath -WorkingDirectory $InstallDir -ErrorAction SilentlyContinue
            }

            $closeTimer = New-Object System.Windows.Forms.Timer
            $closeTimer.Interval = 2000
            $closeTimer.Add_Tick({ param($s2, $e2) $s2.Stop(); $form.Close() })
            $closeTimer.Start()
        })
        $completeTimer.Start()

    } catch {
        [System.Windows.Forms.MessageBox]::Show(
            "Installation failed:`n`n$_",
            "Installation Error",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null

        if (-not $script:isUpdate) {
            if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
                Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
            }
            if (Test-Path $UninstallRegKey) { Remove-Item $UninstallRegKey -Recurse -Force -ErrorAction SilentlyContinue }
            $lnkClean = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
            if (Test-Path $lnkClean) { Remove-Item $lnkClean -Force -ErrorAction SilentlyContinue }
            $deskLnkClean = Join-Path ([System.Environment]::GetFolderPath('Desktop')) "GG System Monitor.lnk"
            if (Test-Path $deskLnkClean) { Remove-Item $deskLnkClean -Force -ErrorAction SilentlyContinue }
            $deskLnkCleanLegacy = Join-Path ([System.Environment]::GetFolderPath('Desktop')) "$AppName.lnk"
            if (Test-Path $deskLnkCleanLegacy) { Remove-Item $deskLnkCleanLegacy -Force -ErrorAction SilentlyContinue }
            if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        $form.Close()
    }
})

$form.ShowDialog() | Out-Null
