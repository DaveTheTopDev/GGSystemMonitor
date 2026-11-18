$ErrorActionPreference="Stop"
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot "..\SetupOperations.ps1"),[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors|Out-String)}
$parts=@()
foreach($name in @("refreshStart","refreshDesktop")){
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq ('$'+$name)},$true)
    if(-not $node){throw "Missing shortcut policy"}
    $parts+=$node.Extent.Text
}
$loop=$ast.Find({param($n) $n -is [Management.Automation.Language.ForEachStatementAst] -and $n.Variable.VariablePath.UserPath -eq "shortcutPath"},$true)
$parts+=$loop.Extent.Text
$parts+='$notice'
$refresh=[scriptblock]::Create($parts -join [Environment]::NewLine)
$temp=Join-Path $env:TEMP ("GGSystemMonitor-shortcut-test-"+[guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null
try{
    $ExePath=Join-Path $temp "GGSystemMonitor.exe"
    $InstallDir=$temp; $shortcutIcon="$ExePath,0"
    $lnkPath=Join-Path $temp "StartMenu.lnk"
    $deskLnkPath=Join-Path $temp "Desktop.lnk"
    $Options=@{StartMenu=$false;Desktop=$false}
    $isUpdate=$false; $isSameVersion=$false
    & $refresh
    if((Test-Path $lnkPath) -or (Test-Path $deskLnkPath)){throw "Opt-out not preserved"}
    "PASS fresh install preserves shortcut opt-out"
    $isUpdate=$true; $isSameVersion=$true
    & $refresh
    if(-not(Test-Path $lnkPath) -or (Test-Path $deskLnkPath)){throw "Repair policy failed"}
    "PASS Repair restores Start Menu without adding Desktop shortcut"
    $shell=New-Object -ComObject WScript.Shell
    $shortcut=$shell.CreateShortcut($lnkPath)
    if($shortcut.Arguments -ne "--settings"){throw "Wrong launch mode"}
    "PASS Settings shortcut uses non-elevating settings launch mode"
    $shortcut.Arguments=""; $shortcut.Save()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)|Out-Null
    $isSameVersion=$false
    & $refresh
    $shortcut=$shell.CreateShortcut($lnkPath)
    if($shortcut.Arguments -ne "--settings"){throw "Update failed to repair arguments"}
    $bytes=[IO.File]::ReadAllBytes($lnkPath)
    if(([BitConverter]::ToUInt32($bytes,20) -band 0x2000) -ne 0){throw "Shortcut requests administrator"}
    "PASS update repairs arguments and does not set Run as administrator"
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)|Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)|Out-Null
    $lock=[IO.File]::Open($lnkPath,"Open","Read","Read")
    try { $warning=& $refresh } finally { $lock.Dispose() }
    if($warning -notmatch 'Could not update the shortcut' -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($lnkPath)) -ne [Convert]::ToBase64String($bytes)) {
        throw "A locked shortcut was treated as fatal or overwritten"
    }
    "PASS a locked shortcut is preserved and reported without failing setup"
}finally{
    if((Split-Path $temp -Leaf) -like "GGSystemMonitor-shortcut-test-*"){Remove-Item -LiteralPath $temp -Recurse -Force}
}
