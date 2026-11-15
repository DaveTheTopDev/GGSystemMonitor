$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\Prerequisites.ps1")
$script:count = 0
function Assert($Value, $Message) {
    if (-not $Value) { throw $Message }
    $script:count++
    Write-Host "PASS $Message"
}
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert $threw $Message
}
$temp = Join-Path $env:TEMP ("GGSystemMonitor-prereq-test-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $packages = @(Get-PrerequisitePackages)
    Assert ($packages.Count -eq 2) "Both offline prerequisites are pinned"
    Assert ($packages[0].Arguments -match "norestart") ".NET cannot force restart"
    Assert ($packages[1].Arguments -eq "-install -silent") "PawnIO uses official silent switches"
    Assert-Throws { Assert-PrerequisitePayload $packages[0] $temp } "Missing prerequisite is rejected"
    [IO.File]::WriteAllText((Join-Path $temp $packages[0].File), "not an installer")
    Assert-Throws { Assert-PrerequisitePayload $packages[0] $temp } "Tampered prerequisite is rejected"

    function Get-SetupRegistryValue($SubKey, $Name) {
        if ($Name -eq "Release") { return $script:release }
        if ($Name -eq "DisplayVersion") { return $script:version }
        if ($Name -eq "ImagePath") { return $script:driver }
    }
    $script:release=461808; $script:version="2.2.0"; $script:driver="PawnIO.sys"
    Assert (Test-FrameworkReady) "Minimum supported framework is accepted"
    Assert (Test-PawnIOReady) "Installed PawnIO version and service are detected"
    $script:release=460798; $script:version="2.1.0"
    Assert (-not (Test-FrameworkReady)) "Older framework requires the bundled installer"
    Assert (-not (Test-PawnIOReady)) "Older PawnIO requires upgrade"
    $script:version="3.0.0"
    Assert (Test-PawnIOReady) "Newer PawnIO is retained rather than downgraded"
    $script:driver=$null
    Assert (-not (Test-PawnIOReady)) "Registry entry without driver service requires repair"

    # Only the following section mocks execution. No real installer is launched.
    function Test-FrameworkReady { return $script:framework }
    function Test-PawnIOReady { return $script:pawn }
    function Assert-PrerequisitePayload($Package, $Directory) { return (Join-Path $Directory $Package.File) }
    function Start-Process {
        param($FilePath, $ArgumentList, $WindowStyle, [switch]$Wait, [switch]$PassThru, $ErrorAction)
        $script:launched++
        if ($FilePath -like "*PawnIO_setup.exe") { $script:pawn = $script:verify }
        else { $script:framework = $script:verify }
        $result = [pscustomobject]@{ ExitCode = $script:code }
        $result | Add-Member -MemberType ScriptMethod -Name Dispose -Value {}
        $result | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($Milliseconds) return $true }
        return $result
    }
    $script:framework=$true; $script:pawn=$true; $script:launched=0; $script:code=0; $script:verify=$true
    $restart = Invoke-OfflinePrerequisites $temp
    Assert (-not $restart -and $script:launched -eq 0) "Existing prerequisites are skipped"
    $script:framework=$false; $script:pawn=$false
    $restart = Invoke-OfflinePrerequisites $temp
    Assert (-not $restart -and $script:launched -eq 2) "Fresh installation chains both prerequisites"
    $script:framework=$true; $script:pawn=$false; $script:code=3010
    Assert (Invoke-OfflinePrerequisites $temp) "Restart-required result is propagated"
    $script:pawn=$false; $script:code=1641
    Assert (Invoke-OfflinePrerequisites $temp) "Restart-initiated result is not mistaken for success without restart"
    $script:pawn=$false; $script:code=5
    Assert-Throws { Invoke-OfflinePrerequisites $temp } "Installer error aborts setup"
    $script:pawn=$false; $script:code=0; $script:verify=$false
    Assert-Throws { Invoke-OfflinePrerequisites $temp } "Unverified installation is not reported successful"
    Write-Host "PASS TOTAL $script:count prerequisite tests"
} finally {
    # Only remove our explicitly created, uniquely named temporary test folder.
    if ((Split-Path $temp -Leaf) -like "GGSystemMonitor-prereq-test-*") { Remove-Item -LiteralPath $temp -Recurse -Force }
}
