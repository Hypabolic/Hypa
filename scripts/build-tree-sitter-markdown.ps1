# Builds tree-sitter-markdown.dll for Windows (x64 or arm64).
# Requires either MSVC (cl.exe via Developer Command Prompt) or mingw-w64.
# Outputs to native/runtimes/<RID>/native/ relative to the repo root.
param([switch]$Force)

$ErrorActionPreference = "Stop"

$RepoRoot    = Split-Path $PSScriptRoot -Parent
$GrammarRepo = "https://github.com/tree-sitter-grammars/tree-sitter-markdown.git"
$GrammarCache = Join-Path $env:TEMP "tree-sitter-markdown-src"

$Arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
$RID  = if ($Arch -eq "Arm64") { "win-arm64" } else { "win-x64" }

$OutputDir  = Join-Path $RepoRoot "native" "runtimes" $RID "native"
$OutputPath = Join-Path $OutputDir "tree-sitter-markdown.dll"

if ((Test-Path $OutputPath) -and -not $Force) {
    Write-Host "Already built: $OutputPath (use -Force to rebuild)"
    exit 0
}

Write-Host "Building tree-sitter-markdown for $RID..."

if (-not (Test-Path $GrammarCache)) {
    git clone --depth 1 $GrammarRepo $GrammarCache
}

$SrcDir = Join-Path $GrammarCache "tree-sitter-markdown" "src"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# Try cl.exe (MSVC) first, fall back to gcc (mingw-w64)
if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
    cl.exe /LD /O2 /Fe:"$OutputPath" `
        "$SrcDir\parser.c" "$SrcDir\scanner.c" `
        /I"$SrcDir"
} elseif (Get-Command gcc -ErrorAction SilentlyContinue) {
    gcc -shared -O2 -o "$OutputPath" `
        "$SrcDir\parser.c" "$SrcDir\scanner.c" `
        -I"$SrcDir"
} else {
    Write-Error "No C compiler found. Install Visual Studio Build Tools or mingw-w64."
    exit 1
}

Write-Host "Built: $OutputPath"
