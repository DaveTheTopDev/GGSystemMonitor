param([Parameter(Mandatory=$true)][string]$TargetDir,
      [Parameter(Mandatory=$true)][string]$FunctionsFile)
$ErrorActionPreference = "Stop"
. $FunctionsFile
$AppName = "GGSystemMonitor"
$ExeName = "GGSystemMonitor.exe"
$TaskName = $AppName
$regKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object Windows.Forms.Form
$form.Text = "Uninstall GGSystemMonitor"
$form.ClientSize = New-Object Drawing.Size(430,130)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.ControlBox = $false
$form.BackColor = [Drawing.Color]::FromArgb(245,245,248)
$status = New-Object Windows.Forms.Label
$status.Text = "Removing GGSystemMonitor. Please wait..."
$status.Location = New-Object Drawing.Point(18,20)
$status.Size = New-Object Drawing.Size(394,35)
$bar = New-Object Windows.Forms.ProgressBar
$bar.Location = New-Object Drawing.Point(18,70)
$bar.Size = New-Object Drawing.Size(394,20)
$form.Controls.AddRange(@($status,$bar))
$form.Show()
function Set-Progress([int]$Value) {
    $bar.Value = $Value
    $form.Refresh()
    [Windows.Forms.Application]::DoEvents()
}
function Get-UninstallShortcuts {
    @(
        (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"),
        (Join-Path ([Environment]::GetFolderPath("Desktop")) "GG System Monitor.lnk"),
        (Join-Path ([Environment]::GetFolderPath("Desktop")) "$AppName.lnk")
    )
}
function Remove-GGInstallation([string]$TargetDir) {

    $target = [IO.Path]::GetFullPath($TargetDir).TrimEnd('\')
    $exe = Join-Path $target $ExeName
    $forbidden = @([IO.Path]::GetPathRoot($target).TrimEnd('\'), $env:WINDIR,
        $env:USERPROFILE, $env:LOCALAPPDATA, $env:ProgramFiles)
    if (-not (Test-Path -LiteralPath $exe) -or $forbidden -contains $target) {
        throw "The target is not a verified GGSystemMonitor application folder."
    }
    Set-Progress 5
    $deadline = (Get-Date).AddSeconds(12)
    do {
        $active = @(Get-Process -Name GGSystemMonitor -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path -eq $exe } catch { $false }
        })
        foreach ($process in $active) {
            try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } finally { $process.Dispose() }
        }
        if ($active.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    Set-Progress 20
    Stop-HardwareMonitorDrivers $target
    Remove-SysFilesWithRetry $target
    Set-Progress 45
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
    }
    $shortcuts = @(Get-UninstallShortcuts)
    foreach ($shortcut in $shortcuts) {
        if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force -ErrorAction Stop }
    }
    Set-Progress 65
    $deadline = (Get-Date).AddSeconds(15)
    do {
        try {
            if (Test-Path -LiteralPath $target) {
                Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
            }
        } catch {
            if ((Get-Date) -ge $deadline) { throw }
            Start-Sleep -Milliseconds 300
        }
    } while ((Test-Path -LiteralPath $target) -and (Get-Date) -lt $deadline)
    if (Test-Path -LiteralPath $target) { throw "The application folder could not be completely removed." }
    if (Test-Path $regKey) { Remove-Item -LiteralPath $regKey -Recurse -Force -ErrorAction Stop }
    Set-Progress 90
    if ((Test-Path $regKey) -or (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        throw "Application registration or startup task remains."
    }
    foreach ($shortcut in $shortcuts) { if (Test-Path -LiteralPath $shortcut) { throw "A shortcut remains." } }
    Set-Progress 100
}
$failure = $null
try { Remove-GGInstallation $TargetDir
} catch { $failure = "$_" }
finally { $form.Close(); $form.Dispose() }
if ($failure) {
    [Windows.Forms.MessageBox]::Show("Uninstall did not complete: " + $failure,
        "Uninstall Failed", "OK", "Error") | Out-Null
} else {
    [Windows.Forms.MessageBox]::Show("GGSystemMonitor has been successfully uninstalled.",
        "Uninstall Complete", "OK", "Information") | Out-Null
}
# These are the two temporary worker files, never paths derived from the install target.
foreach ($path in @($FunctionsFile, $PSCommandPath)) {
    try { Remove-Item -LiteralPath $path -Force -ErrorAction Stop } catch {}
}
if ($failure) { exit 1 }
exit 0
