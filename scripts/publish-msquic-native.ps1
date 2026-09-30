# Publish msquic.dll and digest sidecar in one process.
# Rejects reparse points, hard links, and paths outside PublishDir.
# A writable shared publish directory remains equivalent to code execution for this spike.
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDir,

    [Parameter(Mandatory = $true)]
    [string] $SourcePath,

    [Parameter(Mandatory = $false)]
    [string] $DestFileName = 'msquic.dll'
)

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class NativePublishPathGuard
{
    internal const int FileStandardInfo = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStandardInfoData
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileStandardInfoData fileInformation,
        uint dwBufferSize);

    internal static bool IsHardLink(string path)
    {
        const uint genericRead = 0x80000000;
        const uint fileShareRead = 0x00000001;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;

        using SafeFileHandle handle = CreateFileW(
            path,
            genericRead,
            fileShareRead,
            IntPtr.Zero,
            openExisting,
            fileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"MsQuic publish hard-link check could not open '{path}' (win32={error}).");
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileStandardInfo,
                out var info,
                (uint)Marshal.SizeOf<FileStandardInfoData>()))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"MsQuic publish hard-link check failed for '{path}' (win32={error}).");
        }

        return info.NumberOfLinks > 1;
    }
}
'@

function Test-IsWindowsVolumeRoot([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    $trimmed = $Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    if ($trimmed.StartsWith('\\')) {
        $rest = $trimmed.Substring(2)
        $segments = $rest.Split('\', [System.StringSplitOptions]::RemoveEmptyEntries)
        return ($segments.Length -eq 2)
    }

    if ($trimmed.Length -eq 2 -and $trimmed[1] -eq ':') {
        return $true
    }

    return ($trimmed.Length -eq 3 -and $trimmed[1] -eq ':' -and $trimmed[2] -eq '\')
}

function Assert-DirectoryChainSafe([string] $DirectoryPath) {
    $current = [System.IO.Path]::GetFullPath($DirectoryPath)
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "MsQuic publish directory chain contains a reparse point or symbolic link: $current"
            }
        }

        if (Test-IsWindowsVolumeRoot -Path $current) {
            break
        }

        $parent = [System.IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) {
            break
        }

        $current = $parent
    }
}

function Assert-LeafPathSafe([string] $Path, [string] $PublishRoot) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $full = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $PublishRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not ($full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase))) {
        throw "MsQuic publish path resolves outside PublishDir: $Path"
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "MsQuic publish path is a reparse point or symbolic link: $Path"
    }

    if (-not $item.PSIsContainer -and [NativePublishPathGuard]::IsHardLink($Path)) {
        throw "MsQuic publish path is a hard link: $Path"
    }
}

function Write-DigestSidecar([string] $DigestPath, [string] $Digest) {
    $tempPath = "$DigestPath.tmp"
    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force
    }

    Set-Content -LiteralPath $tempPath -Value $Digest -NoNewline -Encoding ascii
    Assert-LeafPathSafe -Path $tempPath -PublishRoot $publishRoot
    Move-Item -LiteralPath $tempPath -Destination $DigestPath -Force
    Assert-LeafPathSafe -Path $DigestPath -PublishRoot $publishRoot
}

$publishRoot = [System.IO.Path]::GetFullPath($PublishDir)
if (-not (Test-Path -LiteralPath $publishRoot)) {
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
}

Assert-DirectoryChainSafe -DirectoryPath $publishRoot

$destPath = Join-Path $publishRoot $DestFileName
$digestPath = "$destPath.sha256"

Assert-LeafPathSafe -Path $destPath -PublishRoot $publishRoot
Assert-LeafPathSafe -Path $digestPath -PublishRoot $publishRoot

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "MsQuic native library source is missing: $SourcePath"
}

$sourceHash = (Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash

$tempDest = "$destPath.tmp"
if (Test-Path -LiteralPath $tempDest) {
    Remove-Item -LiteralPath $tempDest -Force
}

Copy-Item -LiteralPath $SourcePath -Destination $tempDest -Force
Assert-LeafPathSafe -Path $tempDest -PublishRoot $publishRoot
Move-Item -LiteralPath $tempDest -Destination $destPath -Force
Assert-LeafPathSafe -Path $destPath -PublishRoot $publishRoot

$destHash = (Get-FileHash -LiteralPath $destPath -Algorithm SHA256).Hash
if ($sourceHash -ne $destHash) {
    throw "Published msquic.dll does not match the package source digest."
}

Write-DigestSidecar -DigestPath $digestPath -Digest $sourceHash

if (-not (Test-Path -LiteralPath $digestPath)) {
    throw "MsQuic digest sidecar was not written: $digestPath"
}

exit 0
