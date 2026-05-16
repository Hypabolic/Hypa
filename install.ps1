[CmdletBinding()]
param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"

$Repo = "Hypabolic/Hypa"
$InstallDir = if ($env:LOCALAPPDATA) {
    Join-Path $env:LOCALAPPDATA "Hypa\bin"
} else {
    Join-Path $HOME ".hypa\bin"
}

$IsWindowsOs = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
if (-not $IsWindowsOs) {
    throw "install.ps1 only supports Windows. Use install.sh on Linux or macOS."
}

$Arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    "X64" { "x64" }
    "Arm64" { "arm64" }
    default { throw "Unsupported architecture '$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)'." }
}

$Rid = "win-$Arch"
$Asset = "hypa-$Rid.zip"

if ($Version -eq "latest") {
    $ReleaseUrl = "https://github.com/$Repo/releases/latest/download"
} else {
    $Tag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
    $ReleaseUrl = "https://github.com/$Repo/releases/download/$Tag"
}

$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null

try {
    $ChecksumFile = Join-Path $TempDir "SHA256SUMS"
    $ArchiveFile = Join-Path $TempDir $Asset

    Invoke-WebRequest -Uri "$ReleaseUrl/SHA256SUMS" -OutFile $ChecksumFile
    Invoke-WebRequest -Uri "$ReleaseUrl/$Asset" -OutFile $ArchiveFile

    $Expected = $null
    foreach ($Line in Get-Content $ChecksumFile) {
        if ($Line -match "^\s*([a-fA-F0-9]{64})\s+\*?(.+?)\s*$") {
            $FileName = [System.IO.Path]::GetFileName($Matches[2])
            if ($FileName -eq $Asset) {
                $Expected = $Matches[1].ToLowerInvariant()
                break
            }
        }
    }

    if (-not $Expected) {
        throw "Checksum for $Asset was not found in SHA256SUMS."
    }

    $Actual = (Get-FileHash -Algorithm SHA256 $ArchiveFile).Hash.ToLowerInvariant()
    if ($Expected -ne $Actual) {
        throw "Checksum verification failed for $Asset."
    }

    $ExtractDir = Join-Path $TempDir "extract"
    Expand-Archive -Path $ArchiveFile -DestinationPath $ExtractDir -Force

    $HypaExe = Get-ChildItem -Path $ExtractDir -Filter "hypa.exe" -Recurse -File | Select-Object -First 1
    if (-not $HypaExe) {
        throw "hypa.exe was not found in $Asset."
    }
    $PackageDir = $HypaExe.Directory.FullName

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item (Join-Path $PackageDir "*") -Destination $InstallDir -Recurse -Force

    $HypaDataDir = Join-Path $HOME ".hypa"
    New-Item -ItemType Directory -Force -Path $HypaDataDir | Out-Null
    $InstalledAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    [PSCustomObject]@{
        source             = "script"
        runtime_identifier = $Rid
        install_directory  = $InstallDir
        bin_link_path      = $null
        executable_path    = (Join-Path $InstallDir "hypa.exe")
        installed_version  = $null
        installed_at       = $InstalledAt
    } | ConvertTo-Json -Depth 1 | Set-Content -Path (Join-Path $HypaDataDir "install.json") -Encoding UTF8

    Write-Host "installed hypa to $(Join-Path $InstallDir "hypa.exe")"

    $PathEntries = ($env:PATH -split ";") | Where-Object { $_ }
    if ($PathEntries -notcontains $InstallDir) {
        Write-Warning "$InstallDir is not on PATH"
    }
} finally {
    Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
}
