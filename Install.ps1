<#
.SYNOPSIS
    GGSystemMonitor Installer, Updater, and Uninstaller.
.PARAMETER Uninstall
    Removes GGSystemMonitor from the system completely (silent - called by Uninstall.bat).
#>
param([switch]$Uninstall, [string]$DoneFile = "", [int]$ExcludePid = 0, [string]$ExpectedUserSid = "")

$setupLog = Join-Path $env:TEMP "GGSystemMonitor-install.log"
function Write-SetupLog([string]$Message) {
    try { Add-Content -LiteralPath $setupLog -Value ("[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message) -Encoding UTF8 -ErrorAction Stop } catch {}
}

$AppName         = "GGSystemMonitor"
$ExeName         = "GGSystemMonitor.exe"
$InstallDir      = Join-Path $env:LOCALAPPDATA $AppName
$ExePath         = Join-Path $InstallDir $ExeName
$UninstallRegKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
$windowsPowerShell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"

if ($Uninstall -and $PSCommandPath) {
    $InstallDir = Split-Path -Parent $PSCommandPath
}

# -- Self-elevate silently (no console window) --
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $psArgs = "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`""
    if ($Uninstall)    { $psArgs += " -Uninstall" }
    if ($DoneFile)     { $psArgs += " -DoneFile `"$DoneFile`"" }
    if ($ExcludePid)   { $psArgs += " -ExcludePid $ExcludePid" }
    if (-not $Uninstall) { $psArgs += " -ExpectedUserSid $([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)" }
    try {
        Start-Process -FilePath $windowsPowerShell -ArgumentList $psArgs -Verb RunAs -ErrorAction Stop
        exit 0
    } catch {
        Write-SetupLog "Elevation failed: $_"
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
        Copy-Item -LiteralPath (Join-Path $InstallDir "SetupOperations.ps1") -Destination $helpers -ErrorAction Stop
        Copy-Item -LiteralPath $workerSource -Destination $worker -ErrorAction Stop
        $workerArgs = "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File " +
            [char]34 + $worker + [char]34 + " -TargetDir " + [char]34 + $InstallDir +
            [char]34 + " -FunctionsFile " + [char]34 + $helpers + [char]34
        Start-Process -FilePath $windowsPowerShell -ArgumentList $workerArgs -WindowStyle Hidden -ErrorAction Stop
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
$SourceDir = $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# Prevent simultaneous update/repair windows from touching the same files.
$setupMutex = New-Object Threading.Mutex($false, "Local\GGSystemMonitor.Setup")
$ownsSetupMutex = $false
try {
    try { $ownsSetupMutex = $setupMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsSetupMutex = $true }
    if (-not $ownsSetupMutex) { throw "GGSystemMonitor Setup is already running. Use the existing window." }
    if ($ExpectedUserSid -and $ExpectedUserSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) {
        throw "Setup was elevated as a different Windows user. GGSystemMonitor installs per user. Sign in to the administrator account that will run the app and start setup there."
    }
    Write-SetupLog "Starting setup from $SourceDir (Windows $([Environment]::OSVersion.Version), PowerShell $($PSVersionTable.PSVersion))."
    foreach ($file in @($ExeName, "$ExeName.config", "LibreHardwareMonitorLib.dll", "Newtonsoft.Json.dll", "SetupOperations.ps1", "Prerequisites.ps1", "UninstallWorker.ps1")) {
        if (-not (Test-Path -LiteralPath (Join-Path $SourceDir $file) -PathType Leaf)) {
            throw "The release is missing '$file'. Extract the full ZIP to a local folder and run Install.bat there."
        }
    }
    . (Join-Path $SourceDir "SetupOperations.ps1")

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
            if ((Test-Path -LiteralPath (Join-Path $dir $ExeName) -PathType Leaf) -and
                ((Test-Path $UninstallRegKey) -or (Test-Path -LiteralPath (Join-Path $dir "Uninstall.bat") -PathType Leaf))) {
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
    try {
        $appIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($srcExePath)
        if ($appIcon) { try { $picBannerIcon.Image = $appIcon.ToBitmap() } finally { $appIcon.Dispose() } }
    } catch { Write-SetupLog "Banner icon could not be loaded: $_" }
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
    "GGSystemMonitor will be installed to your user profile folder.`nMissing system prerequisites are installed from this offline bundle."
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
$script:installBusy = $false
$script:installResult = $null
$script:worker = $null
$script:asyncResult = $null
$script:stage = ""
$script:stageClock = $null
$messages = New-Object 'System.Collections.Concurrent.ConcurrentQueue[object]'
$poll = New-Object System.Windows.Forms.Timer
$poll.Interval = 160
$closeTimer = New-Object System.Windows.Forms.Timer
$closeTimer.Interval = 2000
$closeTimer.Add_Tick({ $closeTimer.Stop(); $form.Close() })

function Set-SetupControls([bool]$Busy) {
    $script:installBusy = $Busy
    $form.ControlBox = -not $Busy
    $btnInstall.Enabled = -not $Busy
    $btnCancel.Enabled = -not $Busy -and -not $script:installDone
    $txtInstallPath.Enabled = -not $Busy -and -not $script:isUpdate -and -not $script:installDone
    $btnBrowseInstall.Enabled = $txtInstallPath.Enabled
    foreach ($option in @($chkAutoStart, $chkShortcut, $chkDesktop)) { $option.Enabled = $txtInstallPath.Enabled }
}

function Show-SetupFailure([string]$Message) {
    Write-SetupLog "Failed during '$script:stage': $Message"
    Set-SetupControls $false
    $btnInstall.Text = "Retry"
    $progressBar.Style = "Continuous"
    $progressBar.Value = 0
    $lblBannerText.Text = "Setup did not finish. See the error and retry when ready."
    [Windows.Forms.MessageBox]::Show($form, "Setup could not finish. $Message" + [Environment]::NewLine +
        "Log: $setupLog", "Installation Error", "OK", "Error") | Out-Null
}

$btnBrowseInstall.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    try {
        $dlg.Description = "Select installation folder"
        $dlg.SelectedPath = $txtInstallPath.Text
        if ($dlg.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) { $txtInstallPath.Text = $dlg.SelectedPath }
    } finally { $dlg.Dispose() }
})
$btnCancel.Add_Click({ $form.Close() })
$form.Add_FormClosing({ param($sender, $eventArgs) if ($script:installBusy) { $eventArgs.Cancel = $true } })

$btnInstall.Add_Click({
    if ($script:installDone) { $form.Close(); return }
    if ($script:installBusy) { return }
    Set-SetupControls $true
    $btnInstall.Text = if ($script:isUpdate) { "Updating..." } else { "Installing..." }
    $progressBar.Style = "Marquee"
    $script:stage = "Preparing setup..."
    $script:stageClock = [Diagnostics.Stopwatch]::StartNew()
    $lblBannerText.Text = $script:stage
    $options = @{
        SourceDir = $SourceDir; InstallDir = $txtInstallPath.Text; IsUpdate = $script:isUpdate
        IsSameVersion = $script:isSameVersion; AutoStart = $chkAutoStart.Checked
        StartMenu = $chkShortcut.Checked; Desktop = $chkDesktop.Checked
    }
    try {
        $runspace = [RunspaceFactory]::CreateRunspace()
        $script:workerRunspace = $runspace
        $runspace.ApartmentState = "STA"
        $runspace.ThreadOptions = "ReuseThread"
        $runspace.Open()
        $script:worker = [PowerShell]::Create()
        $script:worker.Runspace = $runspace
        [void]$script:worker.AddScript({
            param($Options, $Messages)
            $ErrorActionPreference = "Stop"
            . (Join-Path $Options.SourceDir "Prerequisites.ps1")
            . (Join-Path $Options.SourceDir "SetupOperations.ps1")
            Invoke-AppInstallation $Options $Messages
        }).AddArgument($options).AddArgument($messages)
        $script:asyncResult = $script:worker.BeginInvoke()
        $poll.Start()
    } catch {
        if ($script:worker) { $script:worker.Dispose(); $script:worker = $null }
        if ($script:workerRunspace) { $script:workerRunspace.Dispose(); $script:workerRunspace = $null }
        Show-SetupFailure $_.Exception.Message
    }
})

$poll.Add_Tick({
    $item = $null
    while ($messages.TryDequeue([ref]$item)) {
        $script:stage = [string]$item.Message
        $script:stageClock.Restart()
        Write-SetupLog $script:stage
        if ($item.Percent -ge 18) { $progressBar.Style = "Continuous"; $progressBar.Value = $item.Percent }
    }
    $lblBannerText.Text = "{0}{1}Elapsed: {2:mm\:ss}" -f $script:stage, [Environment]::NewLine, $script:stageClock.Elapsed
    if (-not $script:asyncResult.IsCompleted) { return }
    $poll.Stop()
    $errorMessage = $null
    try {
        $output = @($script:worker.EndInvoke($script:asyncResult))
        if ($script:worker.Streams.Error.Count) { throw $script:worker.Streams.Error[0] }
        if ($output.Count -ne 1) { throw "Setup did not return a valid completion result." }
        $script:installResult = $output[0]
    } catch { $errorMessage = $_.Exception.Message }
    finally {
        $script:worker.Dispose(); $script:worker = $null
        $script:workerRunspace.Dispose(); $script:workerRunspace = $null
    }
    if ($errorMessage) { Show-SetupFailure $errorMessage; return }

    $script:installDone = $true
    Set-SetupControls $false
    $btnInstall.Text = "Close"
    $btnInstall.BackColor = $clrGreen
    $progressBar.Style = "Continuous"
    $progressBar.Value = 100
    $banner.BackColor = [Drawing.Color]::FromArgb(220, 246, 228)
    $lblBannerText.Text = "GGSystemMonitor $($script:installResult.Version) is installed."
    Write-SetupLog $lblBannerText.Text
    if ($script:installResult.Notice -or $script:installResult.RestartRequired) {
        $notice = $script:installResult.Notice
        if ($script:installResult.RestartRequired) { $notice += " Restart Windows to finish installing prerequisites. Setup will not force a restart." }
        Write-SetupLog $notice
        $lblBannerText.Text += if ($script:installResult.RestartRequired) { " Restart required." } else { " See the prerequisite notice." }
        [Windows.Forms.MessageBox]::Show($form, $notice.Trim(), "Setup Complete", "OK", "Information") | Out-Null
    } else { $closeTimer.Start() }
})

$form.ShowDialog() | Out-Null
if ($script:installDone -and -not $script:installResult.RestartRequired) {
    $target = $script:installResult.InstallDir
    $installedExe = Join-Path $target $ExeName
    try {
        # Launch after the modal closes for installs, updates and repairs.
        Start-Process -FilePath $installedExe -WorkingDirectory $target -ErrorAction Stop
        Start-Process -FilePath $installedExe -ArgumentList "--settings" -WorkingDirectory $target -ErrorAction Stop
    } catch {
        Write-SetupLog "Application launch failed: $_"
        [Windows.Forms.MessageBox]::Show("Setup completed, but the app could not open. Try the Start Menu shortcut. $_", "GGSystemMonitor Setup") | Out-Null
    }
}
} catch {
    Write-SetupLog "Setup startup failed: $_"
    [Windows.Forms.MessageBox]::Show("Setup could not start. $_" + [Environment]::NewLine + "Log: $setupLog", "GGSystemMonitor Setup", "OK", "Error") | Out-Null
    exit 1
} finally {
    if ($poll) { $poll.Dispose() }
    if ($closeTimer) { $closeTimer.Dispose() }
    if ($form) { $form.Dispose() }
    if ($ownsSetupMutex) { $setupMutex.ReleaseMutex() }
    $setupMutex.Dispose()
}
