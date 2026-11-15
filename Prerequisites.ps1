# Shared by release packaging and setup. Setup never downloads executable code.
function Get-PrerequisitePackages {
    @(
        [pscustomobject]@{
            Name = "Microsoft .NET Framework 4.8"; File = "ndp48-x86-x64-allos-enu.exe"
            Url = "https://download.microsoft.com/download/f/3/a/f3a6af84-da23-40a5-8d1c-49cc10c8e76f/NDP48-x86-x64-AllOS-ENU.exe"
            Hash = "0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40"
            Signer = "Microsoft Corporation"; Arguments = "/q /norestart"
        }
        [pscustomobject]@{
            Name = "PawnIO 2.2.0"; File = "PawnIO_setup.exe"
            Url = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe"
            Hash = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032"
            Signer = "namazso"; Arguments = "-install -silent"
        }
    )
}

function Assert-PrerequisitePayload($Package, [string]$Directory) {
    $path = Join-Path $Directory $Package.File
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The offline installer is incomplete: missing $($Package.File). Extract the complete release ZIP and run Install.bat again."
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash -ne $Package.Hash) {
        throw "Integrity verification failed for $($Package.File). Setup will not execute a damaged or modified prerequisite."
    }
    return $path
}

function Get-SetupRegistryValue([string]$SubKey, [string]$Name) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $base.OpenSubKey($SubKey)
        if ($key) { try { return $key.GetValue($Name) } finally { $key.Dispose() } }
    } finally { $base.Dispose() }
    return $null
}

function Test-FrameworkReady {
    $release = Get-SetupRegistryValue "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
    return $null -ne $release -and [int]$release -ge 461808
}

function Test-PawnIOReady {
    $raw = Get-SetupRegistryValue "SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO" "DisplayVersion"
    $driver = Get-SetupRegistryValue "SYSTEM\CurrentControlSet\Services\PawnIO" "ImagePath"
    $version = $null
    return $driver -and [version]::TryParse([string]$raw, [ref]$version) -and $version -ge [version]"2.2.0"
}

function Invoke-OfflinePrerequisites([string]$Directory, [scriptblock]$OnStatus = {}) {
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw "CPU sensor access requires the official signed PawnIO driver, which supports 64-bit Windows only."
    }
    $restart = $false
    foreach ($package in Get-PrerequisitePackages) {
        $ready = if ($package.File -eq "PawnIO_setup.exe") { Test-PawnIOReady } else { Test-FrameworkReady }
        if ($ready) { & $OnStatus "$($package.Name) is already available."; continue }
        $path = Assert-PrerequisitePayload $package $Directory
        & $OnStatus "Installing $($package.Name)..."
        $process = Start-Process -FilePath $path -ArgumentList $package.Arguments -WindowStyle Hidden -PassThru -ErrorAction Stop
        try {
            while (-not $process.WaitForExit(250)) {
                # Keep the setup window responsive during long offline runtime installations.
                [System.Windows.Forms.Application]::DoEvents()
            }
            $code = $process.ExitCode
        } finally { $process.Dispose() }
        if ($code -eq 3010 -or $code -eq 1641) {
            $restart = $true
        } elseif ($code -ne 0) {
            throw "$($package.Name) installation failed (exit code $code). See the setup log. No security protections have been disabled."
        } elseif ($package.File -eq "PawnIO_setup.exe") {
            if (-not (Test-PawnIOReady)) { throw "PawnIO setup returned success, but the required driver could not be verified." }
        } elseif (-not (Test-FrameworkReady)) {
            throw ".NET setup returned success, but the required framework could not be verified."
        }
    }
    return $restart
}
