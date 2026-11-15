<#
.SYNOPSIS
    GGSystemMonitor Installer, Updater, and Uninstaller.
.PARAMETER Uninstall
    Removes GGSystemMonitor from the system completely (silent - called by Uninstall.bat).
#>
param([switch]$Uninstall, [string]$DoneFile = "", [int]$ExcludePid = 0)

$setupLog = Join-Path $env:TEMP "GGSystemMonitor-install.log"
try { Start-Transcript -Path $setupLog -Append | Out-Null } catch {}

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
    $script:ExePath    = Join-Path $InstallDir $ExeName
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
    Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue | ForEach-Object {
        $driverPath = Get-NormalizedDriverPath $_.PathName
        $isAppDriver =
            $_.Name -eq $AppName -or
            $_.Name -match "GGSystemMonitor" -or
            $_.DisplayName -match "GGSystemMonitor"
        $isInInstallDir =
            $targetFullPath -and
            $driverPath -and
            $driverPath.StartsWith($targetFullPath + '\', [System.StringComparison]::OrdinalIgnoreCase)

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
    $psArgs = "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`""
    if ($Uninstall)    { $psArgs += " -Uninstall" }
    if ($DoneFile)     { $psArgs += " -DoneFile `"$DoneFile`"" }
    if ($ExcludePid)   { $psArgs += " -ExcludePid $ExcludePid" }
    try {
        $child = Start-Process powershell.exe $psArgs -Verb RunAs -Wait -PassThru
        exit $child.ExitCode
    } catch {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show("Setup could not start or elevation was cancelled. See $setupLog. $_", "GGSystemMonitor Setup") | Out-Null
        exit 1
    }
}

# ===========================================================================
#  UNINSTALL  (silent -- confirmation was already shown by SettingsForm)
# ===========================================================================
if ($Uninstall) {
    try {
        $workerSource = Join-Path $InstallDir "UninstallWorker.ps1"
        if (-not (Test-Path -LiteralPath $workerSource)) { throw "UninstallWorker.ps1 is missing. Repair the installation before uninstalling." }
        $base = Join-Path $env:TEMP ("GGSystemMonitor-uninstall-" + [guid]::NewGuid().ToString("N"))
        $worker = $base + ".ps1"
        $helpers = $base + ".functions.ps1"
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($PSCommandPath, [ref]$tokens, [ref]$errors)
        $names = @("Get-NormalizedDriverPath", "Get-HardwareMonitorDriverServices", "Stop-HardwareMonitorDrivers", "Remove-SysFilesWithRetry")
        $functions = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $names }, $true)
        [IO.File]::WriteAllText($helpers, (($functions | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine))
        Copy-Item -LiteralPath $workerSource -Destination $worker -ErrorAction Stop
        $workerArgs = "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File " +
            [char]34 + $worker + [char]34 + " -TargetDir " + [char]34 + $InstallDir +
            [char]34 + " -FunctionsFile " + [char]34 + $helpers + [char]34
        Start-Process -FilePath "powershell.exe" -ArgumentList $workerArgs -WindowStyle Hidden -ErrorAction Stop
    } catch {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show("Uninstall could not start: $_", "Uninstall Failed") | Out-Null
        exit 1
    }
    exit 0
}

# ===========================================================================
#  INSTALL / UPDATE  -- GUI
# ===========================================================================
$SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $SourceDir "Prerequisites.ps1")

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

function Get-ExistingInstallDir {
    $candidates = @()

    if (Test-Path $UninstallRegKey) {
        try {
            $reg = Get-ItemProperty -Path $UninstallRegKey -ErrorAction Stop

            if ($reg.InstallLocation) {
                $candidates += [string]$reg.InstallLocation
            }

            if ($reg.DisplayIcon) {
                $iconPath = ([string]$reg.DisplayIcon).Trim().Trim('"') -replace ',\d+$', ''
                if ($iconPath) {
                    try { $candidates += Split-Path -Parent $iconPath } catch {}
                }
            }

            if ($reg.UninstallString) {
                $quotedPaths = [regex]::Matches([string]$reg.UninstallString, '"([^"]+\.(?:bat|ps1|exe))"')
                foreach ($match in $quotedPaths) {
                    try { $candidates += Split-Path -Parent $match.Groups[1].Value } catch {}
                }
            }
        } catch {}
    }

    $candidates += (Join-Path $env:LOCALAPPDATA $AppName)

    foreach ($candidate in ($candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        try {
            $dir = [System.IO.Path]::GetFullPath($candidate).TrimEnd('\')
            if ((Test-Path (Join-Path $dir $ExeName)) -and
                ((Test-Path $UninstallRegKey) -or (Test-Path (Join-Path $dir "Uninstall.bat")))) {
                return $dir
            }
        } catch {}
    }

    return ""
}

$existingInstallDir = Get-ExistingInstallDir
if (-not [string]::IsNullOrWhiteSpace($existingInstallDir)) {
    $InstallDir = $existingInstallDir
    $script:ExePath    = Join-Path $InstallDir $ExeName
    $IconPath   = Join-Path $InstallDir $IconName
}

$script:isUpdate      = -not [string]::IsNullOrWhiteSpace($existingInstallDir)
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
$form.ClientSize      = if ($script:isUpdate) {
    New-Object System.Drawing.Size(480, 320)
} else {
    New-Object System.Drawing.Size(480, 430)
}
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
    "GGSystemMonitor v$curVer is already installed.`nClick Repair to refresh this version while preserving your settings."
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
$optGroup.Visible   = -not $script:isUpdate
if (-not $script:isUpdate) {
    $form.Controls.Add($optGroup)
}

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
$progressBar.Location = if ($script:isUpdate) {
    New-Object System.Drawing.Point(16, 226)
} else {
    New-Object System.Drawing.Point(16, 346)
}
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
$btnInstall.Text      = if ($script:isSameVersion) { "Repair" } elseif ($script:isUpdate) { "Update" } else { "Install" }
$btnInstall.Size      = New-Object System.Drawing.Size(100, 32)
$btnInstall.Location  = New-Object System.Drawing.Point(366, 11)
$btnInstall.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$btnInstall.BackColor = $clrBlue
$btnInstall.ForeColor = [System.Drawing.Color]::White
$btnInstall.Enabled   = $true
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
$script:restartRequired = $false
$script:openSettingsAfterClose = $false
$script:settingsExePath = ""
$script:settingsWorkingDirectory = ""

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

    try {
        $script:InstallDir = [System.IO.Path]::GetFullPath($txtInstallPath.Text.Trim())
        $root = [System.IO.Path]::GetPathRoot($InstallDir).TrimEnd('\')
        $blocked = @($root, $env:WINDIR, $env:USERPROFILE, $env:LOCALAPPDATA, $env:ProgramFiles)
        if ($blocked | Where-Object { $_ -and $_.TrimEnd('\') -eq $InstallDir.TrimEnd('\') }) {
            throw "Choose a dedicated application folder, not a system or profile root."
        }
        $existing = @(Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue)
        if ($existing.Count -gt 0 -and -not (Test-Path (Join-Path $InstallDir $ExeName))) {
            throw "Choose an empty folder or an existing GGSystemMonitor installation."
        }
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Invalid installation folder: $_", "GGSystemMonitor Setup") | Out-Null
        return
    }
    $script:ExePath    = Join-Path $InstallDir $ExeName

    $btnInstall.Text     = if ($script:isUpdate) { "Updating..." } else { "Installing..." }
    $form.ControlBox = $false
    $btnInstall.Enabled  = $false
    $btnCancel.Enabled   = $false
    $chkAutoStart.Enabled  = $false
    $chkShortcut.Enabled   = $false
    $txtInstallPath.Enabled   = $false
    $btnBrowseInstall.Enabled = $false
    $chkDesktop.Enabled      = $false
    $form.Refresh()

    try {
        # Verify/install bundled prerequisites before modifying the application.
        $script:restartRequired = Invoke-OfflinePrerequisites (Join-Path $SourceDir "Prerequisites") {
            param($message)
            $lblBannerText.Text = $message
            $form.Refresh()
        }
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

        # Scheduled task is an install-time preference. Updates preserve the
        # user's existing startup choice instead of recreating it implicitly.
        if (-not $script:isUpdate) {
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
        }
        $progressBar.Value = 74; $form.Refresh()

        # Add / Remove Programs registry entry
        $script:installedVer = Get-AppVersion $ExePath
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

        # Refresh existing shortcuts on updates. Repair also restores the canonical
        # Settings entry if it was lost; a normal update does not add opted-out links.
        $lnkPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
        $deskLnkPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "GG System Monitor.lnk"
        $refreshStart = (-not $script:isUpdate -and $chkShortcut.Checked) -or
            ($script:isUpdate -and ((Test-Path -LiteralPath $lnkPath) -or $script:isSameVersion))
        $refreshDesktop = (-not $script:isUpdate -and $chkDesktop.Checked) -or
            ($script:isUpdate -and (Test-Path -LiteralPath $deskLnkPath))
        foreach ($shortcutPath in @(
            $(if ($refreshStart) { $lnkPath }),
            $(if ($refreshDesktop) { $deskLnkPath })
        )) {
            if (-not $shortcutPath) { continue }
            New-Item -ItemType Directory -Path (Split-Path -Parent $shortcutPath) -Force | Out-Null
            # Recreate our shortcut so stale Run as administrator flags are not retained.
            if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction Stop }
            $shell = New-Object -ComObject WScript.Shell
            $lnk = $shell.CreateShortcut($shortcutPath)
            $lnk.TargetPath = $ExePath
            $lnk.Arguments = "--settings"
            $lnk.WorkingDirectory = $InstallDir
            $lnk.Description = "GGSystemMonitor Settings"
            $lnk.IconLocation = $shortcutIcon
            $lnk.Save()
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($lnk) | Out-Null
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
        }
        $progressBar.Value = 92; $form.Refresh()

        $progressBar.Value = 96; $form.Refresh()

        # -- Success state --
        $script:installDone = $true
        $progressBar.Value  = 100
        if ($script:isUpdate) {
            $script:openSettingsAfterClose = $true
            $script:settingsExePath = $ExePath
            $script:settingsWorkingDirectory = $InstallDir
        }
        $form.Refresh()

        # Use a timer so the UI thread stays free and the progress bar animation
        # can finish painting before the banner and close button appear.
        $script:completeTimer = New-Object System.Windows.Forms.Timer
        $completeTimer.Interval = 600
        $completeTimer.Add_Tick({
            param($s, $e)
            $s.Stop()
            $s.Dispose()

            $banner.BackColor   = [System.Drawing.Color]::FromArgb(220, 246, 228)
            $lblBannerText.Text = if ($script:isUpdate) {
                "GGSystemMonitor has been updated to v$installedVer successfully."
            } else {
                "GGSystemMonitor and its required components have been installed successfully."
            }
            $form.ControlBox = $true
            $btnInstall.Text      = "Close"
            $btnInstall.Enabled   = $true
            $btnInstall.BackColor = $clrGreen
            $form.Refresh()

            # A prerequisite may need a reboot. Never force restart or launch an unusable runtime.
            if ($script:restartRequired) {
                $script:openSettingsAfterClose = $false
                $lblBannerText.Text = "Setup completed. Restart Windows to finish installing prerequisites."
                $btnInstall.Text = "Close"
                [System.Windows.Forms.MessageBox]::Show(
                    "GGSystemMonitor and its prerequisites have been installed. Windows needs a restart before CPU sensors can be used. No restart will be forced.",
                    "Setup Complete - Restart Required",
                    [System.Windows.Forms.MessageBoxButtons]::OK,
                    [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
                return
            }
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

            $script:closeTimer = New-Object System.Windows.Forms.Timer
            $closeTimer.Interval = 2000
            $closeTimer.Add_Tick({ param($s2, $e2) $s2.Stop(); $s2.Dispose(); $form.Close() })
            $closeTimer.Start()
        })
        $completeTimer.Start()

    } catch {
        $script:openSettingsAfterClose = $false
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
if ($script:openSettingsAfterClose -and -not $script:restartRequired) {
    try {
        Start-Process -FilePath $script:settingsExePath -ArgumentList "--settings" -WorkingDirectory $script:settingsWorkingDirectory -ErrorAction Stop
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Update completed, but Settings could not open. Use the Start menu shortcut. $_", "GGSystemMonitor") | Out-Null
    }
}
try { Stop-Transcript | Out-Null } catch {}
