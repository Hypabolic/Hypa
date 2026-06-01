#!/usr/bin/env node
import { existsSync, mkdirSync, realpathSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { homedir, platform } from "node:os";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

function isLocalDevelopmentInstall() {
  if (process.env.CI) return true;
  if (!process.env.INIT_CWD) return false;
  try {
    return realpathSync(process.env.INIT_CWD) === realpathSync(packageRoot);
  } catch {
    return false;
  }
}

function commandExists(command) {
  const path = process.env.PATH;
  if (!path) return false;
  const isWindows = platform() === "win32";
  for (const dir of path.split(isWindows ? ";" : ":")) {
    if (!dir) continue;
    if (existsSync(join(dir, command))) return true;
    if (isWindows && existsSync(join(dir, `${command}.cmd`))) return true;
    if (isWindows && existsSync(join(dir, `${command}.exe`))) return true;
  }
  return false;
}

function resolveBundledHypaBin() {
  const hypaPackageJson = require.resolve("@hypabolic/hypa/package.json");
  const hypaPackageRoot = dirname(hypaPackageJson);
  return join(hypaPackageRoot, "bin.js");
}

function quoteSh(value) {
  return `'${value.replace(/'/g, `'"'"'`)}'`;
}

function installUnixShim(target) {
  const binDir = join(homedir(), ".local", "bin");
  const shim = join(binDir, "hypa");
  mkdirSync(binDir, { recursive: true });

  if (existsSync(shim)) {
    console.error(`[pi-hypa] Hypa shim already exists at ${shim}; leaving it unchanged.`);
    return;
  }

  const script = `#!/usr/bin/env sh
set -eu
SELF="$(realpath "$0" 2>/dev/null || printf '%s' "$0")"
OLD_IFS="$IFS"
IFS=:
for dir in $PATH; do
  [ -n "$dir" ] || continue
  candidate="$dir/hypa"
  [ -x "$candidate" ] || continue
  real_candidate="$(realpath "$candidate" 2>/dev/null || printf '%s' "$candidate")"
  if [ "$real_candidate" != "$SELF" ]; then
    IFS="$OLD_IFS"
    exec "$candidate" "$@"
  fi
done
IFS="$OLD_IFS"
exec node ${quoteSh(target)} "$@"
`;

  writeFileSync(shim, script, { mode: 0o755 });

  if (!process.env.PATH?.split(":").includes(binDir)) {
    console.error(`[pi-hypa] Installed Hypa CLI shim at ${shim}. Add ${binDir} to PATH to run 'hypa' outside Pi.`);
  } else {
    console.error(`[pi-hypa] Installed Hypa CLI shim at ${shim}.`);
  }
}

function installWindowsShim(target) {
  const binDir = join(process.env.LOCALAPPDATA ?? homedir(), "Hypa", "bin");
  const shim = join(binDir, "hypa.cmd");
  mkdirSync(binDir, { recursive: true });

  if (existsSync(shim)) {
    console.error(`[pi-hypa] Hypa shim already exists at ${shim}; leaving it unchanged.`);
    return;
  }

  const script = `@echo off
setlocal enabledelayedexpansion
set "SELF=%~f0"
for %%D in ("%PATH:;=" "%") do (
  if exist "%%~D\hypa.exe" (
    if /I not "%%~fD\hypa.exe"=="!SELF!" "%%~D\hypa.exe" %*
    if not errorlevel 9009 exit /b !errorlevel!
  )
  if exist "%%~D\hypa.cmd" (
    if /I not "%%~fD\hypa.cmd"=="!SELF!" call "%%~D\hypa.cmd" %*
    if not errorlevel 9009 exit /b !errorlevel!
  )
)
node "${target}" %*
`;

  writeFileSync(shim, script);
  console.error(`[pi-hypa] Installed Hypa CLI shim at ${shim}. Add ${binDir} to PATH to run 'hypa' outside Pi.`);
}

try {
  if (isLocalDevelopmentInstall()) process.exit(0);
  if (process.env.HYPA_PI_SKIP_CLI_INSTALL === "1") process.exit(0);
  if (commandExists("hypa")) {
    console.error("[pi-hypa] Hypa CLI already found on PATH; skipping user-level shim install.");
    process.exit(0);
  }

  const bundledHypa = resolveBundledHypaBin();
  if (platform() === "win32") installWindowsShim(bundledHypa);
  else installUnixShim(bundledHypa);
} catch (error) {
  const message = error instanceof Error ? error.message : String(error);
  console.error(`[pi-hypa] Could not install Hypa CLI shim: ${message}`);
  console.error("[pi-hypa] The Pi extension can still use its bundled Hypa dependency.");
}
