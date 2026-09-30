# release pack (Windows twin). and must call this script.
#
# Do not copy only hypa-runtime.
# That pattern drops libe_sqlite3 / e_sqlite3.dll and hypa-pty-host.
# Windows is not a Ghostty-only F1 mux RID. No libghostty-vt.dll.
# Unix F1 and F2 pack libghostty-vt. F1 must not write hypa.channel.
# accepted-digest lineage is an F2 gate. Unix F1 fails closed when
# the RID lib, sidecar, arch, or license notices are missing.
# Unix RID packs from Windows testhost still use pack-hypa-release.sh.
# remains the claim unlock.
#
# Usage:
#   scripts/pack-hypa-release.ps1 -Rid win-x64 -PublishDir artifacts/publish/win-x64 -Name hypa -OutDir artifacts/dist

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Rid,

    [Parameter(Mandatory = $true)]
    [string] $PublishDir,

    [Parameter(Mandatory = $false)]
    [ValidateSet("hypa", "hypa-runtime")]
    [string] $Name = "hypa",

    [Parameter(Mandatory = $true)]
    [string] $OutDir,

    [Parameter(Mandatory = $false)]
    [ValidateSet("tar.gz", "zip")]
    [string] $Archive = "",

    [Parameter(Mandatory = $false)]
    [ValidateSet("f1", "f2")]
    [string] $Channel = "f1",

    [Parameter(Mandatory = $false)]
    [ValidateSet("mux-release", "full")]
    [string] $Profile = "mux-release"
)

$ErrorActionPreference = "Stop"

function Assert-RegularFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        throw "$Label is missing: $Path"
    }
    if ($item.PSIsContainer) {
        throw "$Label must be a regular file: $Path"
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a regular file, not a symlink or reparse point: $Path"
    }
}

function Assert-GhosttyRidArch {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Rid
    )
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 20) {
        throw "$Rid Ghostty lib is too short for ELF/Mach-O ($($bytes.Length) bytes)"
    }
    $magic = ('{0:x2}{1:x2}{2:x2}{3:x2}' -f $bytes[0], $bytes[1], $bytes[2], $bytes[3])
    if ($Rid.StartsWith("linux-")) {
        if ($magic -ne "7f454c46") {
            throw "$Rid Ghostty lib is not ELF (magic=$magic)"
        }
        if ($bytes[4] -ne 2) {
            throw "$Rid Ghostty lib is not ELF64 (ei_class=$($bytes[4]))"
        }
        $machine = [BitConverter]::ToUInt16($bytes, 18)
        $expect = if ($Rid -eq "linux-arm64") { [uint16]183 } else { [uint16]62 }
        if ($machine -ne $expect) {
            throw "$Rid Ghostty lib ELF e_machine=$machine, expected $expect"
        }
    } elseif ($Rid.StartsWith("osx-")) {
        if ($magic -eq "feedfacf") {
            throw "$Rid Ghostty lib is big-endian Mach-O"
        }
        if ($magic -eq "cafebabe" -or $magic -eq "bebafeca" -or $magic -eq "cafebabf" -or $magic -eq "bfbafeca") {
            throw "$Rid Ghostty lib is a fat Mach-O; ship a thin RID slice"
        }
        if ($magic -ne "cffaedfe") {
            throw "$Rid Ghostty lib is not Mach-O (magic=$magic)"
        }
        $cputype = [BitConverter]::ToUInt32($bytes, 4)
        $expect = if ($Rid -eq "osx-arm64") { [uint32]16777228 } else { [uint32]16777223 }
        if ($cputype -ne $expect) {
            throw "$Rid Ghostty lib Mach-O cputype=$cputype, expected $expect"
        }
    } else {
        throw "Ghostty arch check has no CPU map for RID $Rid"
    }
    Write-Host "    ghostty_arch=$Rid"
}

if ($Rid.StartsWith("win-")) {
    throw "Windows is not a Ghostty-only F1 mux RID. No libghostty-vt.dll. Do not pack a mux archive that cannot mux."
}

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "Publish dir missing: $PublishDir"
}

if ([string]::IsNullOrWhiteSpace($Archive)) {
    if ($Rid.StartsWith("win-")) {
        $Archive = "zip"
    } else {
        $Archive = "tar.gz"
    }
}

$packGhostty = $Rid.StartsWith("linux-") -or $Rid.StartsWith("osx-")
if ($Channel -eq "f2") {
    if (-not $Rid.StartsWith("linux-") -and -not $Rid.StartsWith("osx-")) {
        throw "F2 channel is Unix only (linux-* / osx-*). Got '$Rid'"
    }
    $packGhostty = $true
}

$ghosttyRequired = $null
if ($packGhostty) {
    if ($Rid.StartsWith("linux-")) {
        $ghosttyRequired = Join-Path $PublishDir "libghostty-vt.so"
        $ghosttyWrong = Join-Path $PublishDir "libghostty-vt.dylib"
    } elseif ($Rid.StartsWith("osx-")) {
        $ghosttyRequired = Join-Path $PublishDir "libghostty-vt.dylib"
        $ghosttyWrong = Join-Path $PublishDir "libghostty-vt.so"
    } else {
        throw "Ghostty pack is Unix only (linux-* / osx-*). Got '$Rid'"
    }
    $ghosttyItem = Get-Item -LiteralPath $ghosttyRequired -Force -ErrorAction SilentlyContinue
    if ($null -eq $ghosttyItem) {
        throw "pack for $Rid requires $(Split-Path -Leaf $ghosttyRequired) in the publish dir"
    }
    Assert-RegularFile -Path $ghosttyRequired -Label "publish Ghostty lib"
    $wrongItem = Get-Item -LiteralPath $ghosttyWrong -Force -ErrorAction SilentlyContinue
    if ($null -ne $wrongItem) {
        throw "pack for $Rid must not include $(Split-Path -Leaf $ghosttyWrong)"
    }
    Assert-GhosttyRidArch -Path $ghosttyRequired -Rid $Rid
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Channel -eq "f2") {
    $prereq = Join-Path $repoRoot "scripts/verify-h15-f2-prerequisite.sh"
    & bash $prereq
    if ($LASTEXITCODE -ne 0) {
        throw "H-15 F2 prerequisite failed (exit $LASTEXITCODE)"
    }
    $bundle = "$Name-f2-$Rid"
} elseif ($packGhostty) {
    $pin = Join-Path $repoRoot "native/ghostty/PIN.md"
    $loader = Join-Path $repoRoot "src/Hypa.Terminal/Vt/Ghostty/GhosttyLibraryLoader.cs"
    $pinSha = "c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3"
    if (-not (Test-Path -LiteralPath $pin)) {
        throw "missing PIN.md: $pin"
    }
    if ((Get-Content -LiteralPath $pin -Raw) -notmatch [regex]::Escape($pinSha)) {
        throw "PIN.md must contain pin $pinSha"
    }
    if (-not (Test-Path -LiteralPath $loader)) {
        throw "missing GhosttyLibraryLoader.cs: $loader"
    }
    if ((Get-Content -LiteralPath $loader -Raw) -notmatch [regex]::Escape($pinSha)) {
        throw "GhosttyLibraryLoader must pin $pinSha"
    }
    $bundle = "$Name-$Rid"
} else {
    $bundle = "$Name-$Rid"
}
$stageParent = Join-Path ([System.IO.Path]::GetTempPath()) ("hypa-pack-stage-" + [guid]::NewGuid().ToString("N"))
$stage = Join-Path $stageParent $bundle
New-Item -ItemType Directory -Force -Path $stage, $OutDir | Out-Null

try {
    Write-Host "==> H-20 pack $bundle (channel=$Channel)"
    Write-Host "    publish=$PublishDir"
    Write-Host "    profile=$Profile"

    Get-ChildItem -LiteralPath $PublishDir -Force | ForEach-Object {
        $base = $_.Name
        if ($base -like "*.pdb" -or $base -like "*.dbg" -or $base -eq "Fixtures" -or $base -like "*.dSYM") {
            return
        }
        # Packed Ghostty is staged as regular-file bytes below. Recurse copy of a
        # reparse point would archive the link and leave the pack not self-contained.
        if ($packGhostty -and (
                $base -eq "libghostty-vt.so" -or
                $base -eq "libghostty-vt.dylib" -or
                $base -eq "libghostty-vt.dll")) {
            return
        }
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage $base) -Recurse -Force
    }

    if ($Profile -eq "mux-release") {
        $relayHits = Get-ChildItem -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq "hypa-relay" -or $_.Name -eq "hypa-relay.exe" }
        if ($null -ne $relayHits -and @($relayHits).Count -gt 0) {
            $hit = @($relayHits)[0].FullName
            throw "mux-release pack must not include $($hit)"
        }
    } else {
        Write-Host "    relay_guard=skipped (full profile)"
    }

    if ($Channel -eq "f1") {
        # Unix F1 keeps libghostty-vt. Windows never reaches this stage.
        $channelMarker = Join-Path $stage "hypa.channel"
        if (Test-Path -LiteralPath $channelMarker) {
            Remove-Item -LiteralPath $channelMarker -Force
        }
        if (-not $packGhostty) {
            foreach ($ghosttyName in @("libghostty-vt.so", "libghostty-vt.dylib", "libghostty-vt.dll")) {
                $stale = Join-Path $stage $ghosttyName
                if (Test-Path -LiteralPath $stale) {
                    Remove-Item -LiteralPath $stale -Force
                }
            }
        }
    }

    if ($packGhostty) {
        $ghosttyDocs = Join-Path $repoRoot "native/ghostty"
        foreach ($doc in @("NOTICE", "LICENSE.Ghostty", "PIN.md", "abi-manifest.json", "sbom.cdx.json")) {
            $src = Join-Path $ghosttyDocs $doc
            if (-not (Test-Path -LiteralPath $src)) {
                throw "pack missing $src"
            }
            Copy-Item -LiteralPath $src -Destination (Join-Path $stage $doc) -Force
        }
        $sidecarName = "libghostty-vt.$Rid.sha256"
        $sidecarPublish = Join-Path $PublishDir $sidecarName
        $sidecarRepo = Join-Path $repoRoot "native/runtimes/$Rid/native/$sidecarName"
        $sidecar = $null
        foreach ($candidate in @($sidecarPublish, $sidecarRepo)) {
            $sidecarItem = Get-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
            if ($null -eq $sidecarItem) {
                continue
            }
            if (($sidecarItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                continue
            }
            $sidecar = $candidate
            break
        }
        if ([string]::IsNullOrWhiteSpace($sidecar)) {
            throw "pack requires $sidecarName next to the Ghostty lib"
        }
        Assert-RegularFile -Path $sidecar -Label "Ghostty sidecar"
        $stagedSidecar = Join-Path $stage $sidecarName
        Copy-Item -LiteralPath $sidecar -Destination $stagedSidecar -Force
        Assert-RegularFile -Path $stagedSidecar -Label "staged Ghostty sidecar"

        if ($Rid.StartsWith("linux-")) {
            $stagedLib = Join-Path $stage "libghostty-vt.so"
        } else {
            $stagedLib = Join-Path $stage "libghostty-vt.dylib"
        }
        if (Test-Path -LiteralPath $stagedLib) {
            Remove-Item -LiteralPath $stagedLib -Force
        }
        Assert-RegularFile -Path $ghosttyRequired -Label "publish Ghostty lib"
        Copy-Item -LiteralPath $ghosttyRequired -Destination $stagedLib -Force
        Assert-RegularFile -Path $stagedLib -Label "staged Ghostty lib"
        Assert-GhosttyRidArch -Path $stagedLib -Rid $Rid
        $expectedHash = ((Get-Content -LiteralPath $stagedSidecar -Raw) -split '\s+')[0]
        $actualHash = (Get-FileHash -LiteralPath $stagedLib -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($expectedHash)) {
            throw "$sidecarName is empty"
        }
        if ($expectedHash.ToLowerInvariant() -ne $actualHash) {
            throw "packed $(Split-Path -Leaf $stagedLib) sha256 $actualHash does not match sidecar $expectedHash"
        }

        if ($Channel -eq "f2") {
            # PIN-SHA equality is not lineage. F2 packed lib must be an accepted hash.
            # Unix F1 ships the RID lib without this golden-lineage gate.
            $h15Digest = ""
            $h15Source = "HYPA_F2_H15_DIGEST"
            if (-not [string]::IsNullOrWhiteSpace($env:HYPA_F2_H15_DIGEST)) {
                $h15Digest = $env:HYPA_F2_H15_DIGEST.Trim().ToLowerInvariant()
            } else {
                $manifest = Join-Path $repoRoot "native/ghostty/h15-accepted-digests"
                if (Test-Path -LiteralPath $manifest) {
                    foreach ($line in Get-Content -LiteralPath $manifest) {
                        $trim = $line.Trim()
                        if ([string]::IsNullOrWhiteSpace($trim) -or $trim.StartsWith("#")) {
                            continue
                        }
                        $parts = $trim -split '\s+'
                        if ($parts.Length -ge 2 -and $parts[0] -eq $Rid) {
                            $h15Digest = $parts[1].Trim().ToLowerInvariant()
                            $h15Source = "native/ghostty/h15-accepted-digests"
                            break
                        }
                    }
                }
            }
            if ([string]::IsNullOrWhiteSpace($h15Digest)) {
                throw "F2 pack for $Rid has no H-15 accepted digest. Run scripts/verify-h15-rc-replay.sh on this exact lib. Then set HYPA_F2_H15_DIGEST or record the sha256 in native/ghostty/h15-accepted-digests."
            }
            if ($h15Digest -notmatch '^[0-9a-f]{64}$') {
                throw "H-15 digest from $h15Source is not a 64-char sha256"
            }
            if ($actualHash -ne $h15Digest) {
                throw "packed $(Split-Path -Leaf $stagedLib) sha256 $actualHash is not the H-15 accepted digest $h15Digest ($h15Source)"
            }
            Write-Host "    h15_digest=$actualHash ($h15Source)"
            Set-Content -LiteralPath (Join-Path $stage "hypa.channel") -Value "f2" -NoNewline
            Add-Content -LiteralPath (Join-Path $stage "hypa.channel") -Value ""
        }
    }

    if ($Name -eq "hypa") {
        $productUnix = Join-Path $stage "hypa"
        $productWin = Join-Path $stage "hypa.exe"
        if (-not (Test-Path -LiteralPath $productUnix) -and -not (Test-Path -LiteralPath $productWin)) {
            throw "--name hypa requires the hypa product binary in the publish dir"
        }
        $attachUnix = Join-Path $stage "hypa-attach"
        $attachWin = Join-Path $stage "hypa-attach.exe"
        if (-not (Test-Path -LiteralPath $attachUnix) -and -not (Test-Path -LiteralPath $attachWin)) {
            throw "--name hypa requires hypa-attach beside hypa (lean attach sibling, not a product brand)"
        }
        $muxUnix = Join-Path $stage "hypa-runtime"
        $muxWin = Join-Path $stage "hypa-runtime.exe"
        if (-not (Test-Path -LiteralPath $muxUnix) -and -not (Test-Path -LiteralPath $muxWin)) {
            throw "--name hypa requires hypa-runtime beside hypa (lean mux sibling, not a product brand)"
        }
    } else {
        $aliasUnix = Join-Path $stage "hypa-runtime"
        $aliasWin = Join-Path $stage "hypa-runtime.exe"
        if (-not (Test-Path -LiteralPath $aliasUnix) -and -not (Test-Path -LiteralPath $aliasWin)) {
            throw "--name hypa-runtime requires the hypa-runtime debug-alias binary"
        }
        $productUnix = Join-Path $stage "hypa"
        $productWin = Join-Path $stage "hypa.exe"
        if ((Test-Path -LiteralPath $productUnix) -or (Test-Path -LiteralPath $productWin)) {
            throw "--name hypa-runtime refuses a stage that holds hypa (debug-alias pack is not the product pack)"
        }
    }

    if ($Archive -eq "zip") {
        $artifact = Join-Path $OutDir "$bundle.zip"
        if (Test-Path -LiteralPath $artifact) {
            Remove-Item -LiteralPath $artifact -Force
        }
        Compress-Archive -Path $stage -DestinationPath $artifact -Force
    } elseif ($Archive -eq "tar.gz") {
        $artifact = Join-Path $OutDir "$bundle.tar.gz"
        tar -czf $artifact -C $stageParent $bundle
    } else {
        throw "Archive must be tar.gz or zip"
    }

    Write-Host "    artifact=$artifact"

    $smoke = Join-Path $repoRoot "scripts/verify-f1-pack-smoke.sh"
    $smokeArgs = @($smoke, "--rid", $Rid, "--channel", $Channel, "--name", $Name, "--profile", $Profile)
    $smokeArgs += $artifact
    & bash @smokeArgs
    if ($LASTEXITCODE -ne 0) {
        throw "F1 pack smoke failed for $artifact (exit $LASTEXITCODE)"
    }

    Write-Host "PASS: packed $artifact"
} finally {
    if (Test-Path -LiteralPath $stageParent) {
        Remove-Item -LiteralPath $stageParent -Recurse -Force
    }
}
