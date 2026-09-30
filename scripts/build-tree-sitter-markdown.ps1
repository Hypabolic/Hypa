# Builds tree-sitter-markdown.dll for Windows (x64 or arm64).
# Uses MSVC (cl.exe) or mingw-w64 (gcc). If neither is on PATH, the MSVC
# environment is bootstrapped from a Visual Studio install via vswhere/vcvarsall,
# so this works on bare GitHub-hosted runners including windows-11-arm.
# Outputs to native/runtimes/<RID>/native/ relative to the repo root.
param([switch]$Force)

$ExportSymbol = "tree_sitter_markdown"

$ErrorActionPreference = "Stop"

$RepoRoot    = Split-Path $PSScriptRoot -Parent
$GrammarRepo = "https://github.com/tree-sitter-grammars/tree-sitter-markdown.git"
# Pin to a released tag for reproducible, reviewable builds (cache keyed by ref).
$GrammarRef  = "v0.5.3"
$GrammarCache = Join-Path $env:TEMP "tree-sitter-markdown-src-$GrammarRef"

$Arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
$RID  = if ($Arch -eq "Arm64") { "win-arm64" } else { "win-x64" }

$OutputDir  = Join-Path $RepoRoot "native" "runtimes" $RID "native"
$OutputPath = Join-Path $OutputDir "tree-sitter-markdown.dll"

if ((Test-Path $OutputPath) -and -not $Force) {
    Write-Host "Already built: $OutputPath (use -Force to rebuild)"
    exit 0
}

# Multiple MSBuild projects in the same solution build can independently decide
# the grammar is missing and invoke this script at the same time (e.g. `dotnet
# build` on the solution parallelizes across projects). Serialize with a
# mkdir-style lock — New-Item -ItemType Directory is an atomic test-and-set, so
# exactly one concurrent invocation wins the race — then re-check under the
# lock in case a sibling invocation just finished the build while we waited.
$LockDir = Join-Path $env:TEMP "tree-sitter-markdown-build-lock-$RID"
$attempts = 0
while ($true) {
    try {
        New-Item -ItemType Directory -Path $LockDir -ErrorAction Stop | Out-Null
        break
    } catch {
        $attempts++
        if ($attempts -gt 600) {
            Write-Error "Timed out waiting for build lock $LockDir (held by a concurrent build?)"
            exit 1
        }
        Start-Sleep -Milliseconds 200
    }
}
try {
    if ((Test-Path $OutputPath) -and -not $Force) {
        Write-Host "Already built (by a concurrent invocation): $OutputPath"
        exit 0
    }

    Write-Host "Building tree-sitter-markdown for $RID..."

if (-not (Test-Path $GrammarCache)) {
    git clone --depth 1 --branch $GrammarRef $GrammarRepo $GrammarCache
}

$SrcDir = Join-Path $GrammarCache "tree-sitter-markdown" "src"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

function Invoke-Cl {
    # MSVC does not export DLL symbols by default, so request the grammar entry
    # point explicitly via the linker (mingw gcc, by contrast, exports everything).
    Write-Host "Compiling with MSVC cl.exe..."
    cl.exe /nologo /LD /O2 /Fe:"$OutputPath" `
        "$SrcDir\parser.c" "$SrcDir\scanner.c" `
        /I"$SrcDir" `
        /link /EXPORT:$ExportSymbol
    if ($LASTEXITCODE -ne 0) { throw "cl.exe failed with exit code $LASTEXITCODE" }
}

function Invoke-Gcc {
    Write-Host "Compiling with gcc (mingw-w64)..."
    gcc -shared -O2 -o "$OutputPath" `
        "$SrcDir\parser.c" "$SrcDir\scanner.c" `
        -I"$SrcDir"
    if ($LASTEXITCODE -ne 0) { throw "gcc failed with exit code $LASTEXITCODE" }
}

function Import-MsvcEnvironment {
    # Locate a Visual Studio install via vswhere (preinstalled on all GitHub-hosted
    # Windows runners) and import its developer environment for the native arch, so
    # cl.exe becomes available even when it is not on PATH (e.g. windows-11-arm).
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { return $false }
    $vsPath = (& $vswhere -latest -products * -property installationPath) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($vsPath)) { return $false }
    $vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvarsall.bat"
    if (-not (Test-Path $vcvars)) { return $false }
    $vcArch = if ($Arch -eq "Arm64") { "arm64" } else { "x64" }
    Write-Host "Bootstrapping MSVC environment via vcvarsall.bat $vcArch..."
    cmd /c "`"$vcvars`" $vcArch && set" | ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') {
            Set-Item -Path ("Env:\" + $matches[1]) -Value $matches[2]
        }
    }
    return [bool](Get-Command cl.exe -ErrorAction SilentlyContinue)
}

# Prefer MSVC: vcvarsall targets the native arch, so it produces a correctly-arch'd
# DLL with a verified export. mingw gcc is demoted to a last resort because the
# mingw toolchain preinstalled on arm64 runners is x64 and silently produces a
# wrong-arch DLL that the arm64 runtime cannot load (regex fallback at runtime).
if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
    Invoke-Cl
} elseif (Import-MsvcEnvironment) {
    Invoke-Cl
} elseif (($Arch -ne "Arm64") -and (Get-Command gcc -ErrorAction SilentlyContinue)) {
    # gcc is only trusted on x64: the mingw toolchain preinstalled on arm64 runners
    # is x64 and would silently produce a wrong-arch DLL. On arm64 we require MSVC.
    Invoke-Gcc
} else {
    Write-Error "No usable C compiler found for $RID. Install Visual Studio Build Tools (C++ workload; required on arm64) or mingw-w64 (x64 only)."
    exit 1
}

# Verify the required symbol is exported. dumpbin is available once MSVC is on PATH;
# the mingw gcc path auto-exports, so skip gracefully when dumpbin is absent.
$dumpbin = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
if ($dumpbin) {
    # Note: `$array -match` returns the MATCHING elements (empty if none), so test
    # for "no line matched" with -not (...). `$array -notmatch` would instead return
    # the non-matching lines (almost always non-empty) and falsely report success.
    if (-not ((& dumpbin.exe /EXPORTS "$OutputPath") -match $ExportSymbol)) {
        Write-Error "Symbol '$ExportSymbol' not exported from $OutputPath"
        exit 1
    }
    # Verify the DLL machine type matches the target arch, so a mis-targeted
    # toolchain cannot silently package a wrong-arch binary (dumpbin reports e.g.
    # "machine (x64)" / "machine (ARM64)").
    $expectedMachine = if ($Arch -eq "Arm64") { "ARM64" } else { "x64" }
    if (-not ((& dumpbin.exe /HEADERS "$OutputPath") -match "machine \($expectedMachine\)")) {
        Write-Error "$OutputPath architecture does not match $RID (expected machine $expectedMachine)"
        exit 1
    }
    Write-Host "Symbol '$ExportSymbol' and $expectedMachine architecture verified in $OutputPath"
} else {
    Write-Host "dumpbin not available; skipping symbol verification (gcc auto-exports)."
}

    Write-Host "Built: $OutputPath"
} finally {
    Remove-Item -Path $LockDir -Recurse -Force -ErrorAction SilentlyContinue
}
