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
    Assert ($bat -match 'start "" .*powershell\.exe.*-WindowStyle Hidden') "Batch detaches hidden setup"
    $worker=Get-Content (Join-Path $root "UninstallWorker.ps1") -Raw
    Assert ($worker -notmatch 'cmd.exe|rmdir') "No delayed cross-shell cleanup"

    # Exercise the actual setup pipeline in a separate runspace. Fake system
    # registration/driver operations; use real files/locks in temporary folders.
    $tokens=$null; $errors=$null
    $setupAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root "Install.ps1"), [ref]$tokens, [ref]$errors)
    $addScript = $setupAst.Find({ param($n)
        $n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Member.Value -eq "AddScript"
    }, $true)
    $workerScript = $addScript.Arguments[0].ScriptBlock.GetScriptBlock().ToString()
    $source = Join-Path $temp "offline bundle"
    $target = Join-Path $temp "test install"
    New-Item -ItemType Directory -Path $source,$target | Out-Null
    Copy-Item (Join-Path $root "SetupOperations.ps1") $source
    Set-Content -LiteralPath (Join-Path $source $ExeName) "new version"
    Set-Content -LiteralPath (Join-Path $target $ExeName) "old version"
    Set-Content -LiteralPath (Join-Path $source "settings.json") "defaults"
    Set-Content -LiteralPath (Join-Path $target "settings.json") "user settings"
    @'
function Invoke-OfflinePrerequisites($Path,$OnStatus) {
    & $OnStatus "Mock PawnIO is running"
    if ($Options.FailPrerequisite) { throw "Simulated prerequisite timeout" }
    Start-Sleep -Milliseconds 500
    [pscustomobject]@{ RestartRequired=$false; Notice="Simulated PawnIO conflict" }
}
function Get-Process { param($Name,$ErrorAction) @() }
function Test-Path {
    param($Path,$LiteralPath,$PathType)
    if ($LiteralPath -like '*.lnk' -or $LiteralPath -like 'HKLM:*') { return $false }
    Microsoft.PowerShell.Management\Test-Path @PSBoundParameters
}
function New-Item {
    param($Path,$ItemType,[switch]$Force)
    if ($Path -like 'HKCU:*') { return }
    Microsoft.PowerShell.Management\New-Item @PSBoundParameters
}
function Set-ItemProperty { param($Path,$Name,$Value,$Type) }
'@ | Set-Content -LiteralPath (Join-Path $source "Prerequisites.ps1")

    function Invoke-TestSetup([bool]$FailPrerequisite) {
        $options=@{ SourceDir=$source; InstallDir=$target; IsUpdate=$true; IsSameVersion=$false; FailPrerequisite=$FailPrerequisite }
        $messages = New-Object 'Collections.Concurrent.ConcurrentQueue[object]'
        $runspace = [RunspaceFactory]::CreateRunspace()
        $runspace.ApartmentState = "STA"
        $runspace.ThreadOptions = "ReuseThread"
        $runspace.Open()
        $pipeline = [PowerShell]::Create()
        $pipeline.Runspace = $runspace
        try {
            [void]$pipeline.AddScript($workerScript).AddArgument($options).AddArgument($messages)
            $pending = $pipeline.BeginInvoke()
            if (-not $pending.AsyncWaitHandle.WaitOne(10000)) { throw "Test worker took too long" }
            $result = @($pipeline.EndInvoke($pending))
            if ($pipeline.Streams.Error.Count) { throw $pipeline.Streams.Error[0] }
            if ($result.Count -ne 1) { throw "Expected one result from setup" }
            return @{ Result=$result[0]; StageCount=$messages.Count }
        } finally { $pipeline.Dispose(); $runspace.Dispose() }
    }
    $workerFailed=$false
    try { Invoke-TestSetup $true | Out-Null } catch { $workerFailed = $_.Exception.Message -match "Simulated prerequisite timeout" }
    Assert $workerFailed "Prerequisite failure reaches setup as a terminating error"
    Assert ((Get-Content -LiteralPath (Join-Path $target $ExeName) -Raw).Trim() -eq "old version") "Prerequisite failure does not modify the installed app"
    $locked = [IO.File]::Open((Join-Path $target $ExeName), "Open", "Read", "None")
    $workerFailed=$false
    try { Invoke-TestSetup $false | Out-Null } catch { $workerFailed = $_.Exception.Message -match 'Cannot replace' }
    finally { $locked.Dispose() }
    Assert $workerFailed "Update detects a locked EXE before copy"
    $completed = Invoke-TestSetup $false
    Assert ($completed.StageCount -ge 7 -and $completed.Result.Notice -eq "Simulated PawnIO conflict") "Background setup returns stage progress and preserves the nonfatal notice"
    Assert ((Get-Content -LiteralPath (Join-Path $target $ExeName) -Raw).Trim() -eq "new version") "Background update actually replaces the payload"
    Assert ((Get-Content -LiteralPath (Join-Path $target "settings.json") -Raw).Trim() -eq "user settings") "Background update preserves settings"
    Write-Host "PASS TOTAL $count installer lifecycle tests"
} finally {
    $env:LOCALAPPDATA=$oldLocal
    if ((Split-Path $temp -Leaf) -like "GGSystemMonitor-lifecycle-*") {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
