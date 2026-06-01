#!/usr/bin/env node
import { existsSync, mkdirSync, realpathSync, symlinkSync, writeFileSync } from "node:fs";
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

function installUnixShim(target) {
  const binDir = join(homedir(), ".local", "bin");
  const shim = join(binDir, "hypa");
  mkdirSync(binDir, { recursive: true });

  if (existsSync(shim)) {
    console.error(`[pi-hypa] Hypa shim already exists at ${shim}; leaving it unchanged.`);
    return;
  }

  try {
    symlinkSync(target, shim);
  } catch {
    writeFileSync(shim, `#!/usr/bin/env sh\nexec node ${JSON.stringify(target)} "$@"\n`, { mode: 0o755 });
  }

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

  writeFileSync(shim, `@echo off\r\nnode "${target}" %*\r\n`);
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
