param(
    [ValidateSet("win-x64", "win-x86")][string]$Runtime = "win-x64",
    [switch]$SkipZip
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
. (Join-Path $root "Prerequisites.ps1")
$staging = [guid]::NewGuid().ToString("N")
$output = Join-Path $root "artifacts\$Runtime\$staging\GGSystemMonitor"
if (Test-Path $output) { throw "Output already exists: $output. Choose a clean workspace for release packaging." }
dotnet restore (Join-Path $root "GGSystemMonitor.csproj") --locked-mode
if ($LASTEXITCODE) { throw "Restore failed" }
dotnet build (Join-Path $root "GGSystemMonitor.csproj") -c Release -r $Runtime --no-restore -o $output
if ($LASTEXITCODE) { throw "Build failed" }
# Never ship local settings, cached hardware, status files, or logs.
Get-ChildItem -LiteralPath $output -File | Where-Object { $_.Extension -notin @(".exe", ".dll", ".config", ".ico", ".ps1") } | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $root "Install.bat") -Destination (Split-Path $output)
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $output
Copy-Item -LiteralPath (Join-Path $root "THIRD-PARTY-NOTICES.md") -Destination $output
# Download only at build time, then ship all prerequisites inside the ZIP.
$cache = Join-Path $root "obj\prerequisites"
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$prereqs = Join-Path $output "Prerequisites"
New-Item -ItemType Directory -Force -Path $prereqs | Out-Null
foreach ($package in Get-PrerequisitePackages) {
    $cached = Join-Path $cache $package.File
    if (-not (Test-Path -LiteralPath $cached)) {
        & curl.exe --fail --location --retry 3 --output $cached $package.Url
        if ($LASTEXITCODE) { throw "Cannot download $($package.Name)" }
    }
    Assert-PrerequisitePayload $package $cache | Out-Null
    $signature = Get-AuthenticodeSignature -LiteralPath $cached
    if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notlike "*$($package.Signer)*") {
        throw "Invalid publisher signature on $($package.File). Refusing to package it."
    }
    Copy-Item -LiteralPath $cached -Destination $prereqs
}
Copy-Item -LiteralPath (Join-Path $root "Prerequisites.ps1") -Destination $output
# Ship the corresponding driver source and its pinned interpreter submodule.
$sourcePackages = @(
    [pscustomobject]@{ File = "PawnIO-2.2.0-source.zip"; Hash = "638D685555DB311EF9F946284DC4CD5720D11C6CB9C5EC5140EBB7F60373440F"; Url = "https://codeload.github.com/namazso/PawnIO/zip/5cdf470831fdfff3f7f1d06363ca6b230f3bf35a" },
    [pscustomobject]@{ File = "PawnPP-source.zip"; Hash = "20AA0638B95C90D296935310F3F28CC22DE1FB24855C5BAA6721E2AE95EB6042"; Url = "https://codeload.github.com/namazso/PawnPP/zip/e64e4c37b2d8ba0d8ee57205faf8183aee12c438" }
)
foreach ($source in $sourcePackages) {
    $cached = Join-Path $cache $source.File
    if (-not (Test-Path -LiteralPath $cached)) {
        & curl.exe --fail --location --retry 3 --output $cached $source.Url
        if ($LASTEXITCODE) { throw "Cannot download driver source" }
    }
    Assert-PrerequisitePayload $source $cache | Out-Null
    Copy-Item -LiteralPath $cached -Destination $prereqs
}

$folder = Split-Path $output
if ($SkipZip) {
    Write-Output $output
    return
}
$zip = Join-Path $root "artifacts\GGSystemMonitor-3.2.0-$Runtime.zip"
Compress-Archive -Path (Join-Path $folder "*") -DestinationPath $zip -Force
Get-FileHash -Algorithm SHA256 -LiteralPath $zip | Format-List
