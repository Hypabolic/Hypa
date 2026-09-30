[CmdletBinding()]
param(
    [string]$Version = "latest",
    [string]$FromArchive = ""
)

$ErrorActionPreference = "Stop"

# Windows is not a Ghostty-only F1 mux RID. No libghostty-vt.dll.
# pack-hypa-release.ps1 and release.yml refuse win-* mux archives.
# Do not download a Windows mux zip. Unix install.sh is the F1 ship path.
# Compression CLI on Windows is build-from-source, not this installer.
throw "Windows is not a Ghostty-only F1 mux RID. No libghostty-vt.dll. Mux cannot start. Do not install a mux archive that cannot mux. Use install.sh on Linux or macOS."
