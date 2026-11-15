$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
function Import-TestFunction($File, $Name) {
    $tokens=$null; $errors=$null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root $File),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
    $function = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name }, $true)
    if (-not $function) { throw "Missing function: $Name" }
    return [scriptblock]::Create($function.Extent.Text.Replace(".AddSeconds(15)", ".AddSeconds(1)"))
}
. (Import-TestFunction "Install.ps1" "Get-ExistingInstallDir")
. (Import-TestFunction "UninstallWorker.ps1" "Remove-GGInstallation")
$count = 0
function Assert($Value, $Message) { if (-not $Value) { throw $Message }; $script:count++; Write-Host "PASS $Message" }
$temp = Join-Path $env:TEMP ("GGSystemMonitor-lifecycle-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null
$oldLocal = $env:LOCALAPPDATA
$AppName="GGSystemMonitor"; $ExeName="GGSystemMonitor.exe"; $TaskName=$AppName
$UninstallRegKey=Join-Path $temp "registration"
$regKey=$UninstallRegKey
$install=Join-Path $temp "GGSystemMonitor"
$script:taskPresent=$false
function Get-ScheduledTask { param($TaskName,$ErrorAction) if ($script:taskPresent) { return [pscustomobject]@{TaskName=$TaskName} } }
function Unregister-ScheduledTask { param($TaskName,$Confirm,$ErrorAction) $script:taskPresent=$false }
function Stop-HardwareMonitorDrivers { param($TargetDir) }
function Remove-SysFilesWithRetry { param($TargetDir) }
function Get-Process { param($Name,$ErrorAction) @() }
function Set-Progress { param($Value) }
function Get-UninstallShortcuts { @(Join-Path $temp "shortcut.lnk") }
try {
    $env:LOCALAPPDATA=$temp
    Assert ((Get-ExistingInstallDir) -eq "") "Missing installation is fresh install"
    New-Item -ItemType Directory -Path $install | Out-Null
    [IO.File]::WriteAllText((Join-Path $install $ExeName), "fixture")
    Assert ((Get-ExistingInstallDir) -eq "") "Stray executable alone does not trigger update"
    [IO.File]::WriteAllText((Join-Path $install "Uninstall.bat"), "fixture")
    Assert ((Get-ExistingInstallDir) -eq $install) "Existing application with uninstall marker triggers update"
    New-Item -ItemType Directory -Path $regKey | Out-Null
    [IO.File]::WriteAllText((Join-Path $temp "shortcut.lnk"), "fixture")
    $script:taskPresent=$true
    Remove-GGInstallation $install
    Assert (-not (Test-Path $install)) "Uninstall removes application folder before success"
    Assert (-not (Test-Path $regKey)) "Uninstall removes registration"
    Assert (-not $script:taskPresent) "Uninstall removes startup task"
    Assert (-not (Test-Path (Join-Path $temp "shortcut.lnk"))) "Uninstall removes shortcuts"
    Assert ((Get-ExistingInstallDir) -eq "") "Install after successful uninstall is fresh install"

    New-Item -ItemType Directory -Path $install,$regKey | Out-Null
    [IO.File]::WriteAllText((Join-Path $install $ExeName), "fixture")
    $lock = [IO.File]::Open((Join-Path $install $ExeName), "Open", "Read", "None")
    $failed=$false
    try { Remove-GGInstallation $install } catch { $failed=$true } finally { $lock.Dispose() }
    Assert $failed "Locked executable produces failure, not false success"
    Assert (Test-Path $regKey) "Failed deletion retains registration for recovery"

    $bat=Get-Content (Join-Path $root "Install.bat") -Raw
    Assert ($bat -match 'start "" powershell.exe.*-WindowStyle Hidden') "Batch detaches hidden setup"
    $worker=Get-Content (Join-Path $root "UninstallWorker.ps1") -Raw
    Assert ($worker -notmatch 'cmd.exe|rmdir') "No delayed cross-shell cleanup"
    Write-Host "PASS TOTAL $count installer lifecycle tests"
} finally {
    $env:LOCALAPPDATA=$oldLocal
    if ((Split-Path $temp -Leaf) -like "GGSystemMonitor-lifecycle-*") {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
