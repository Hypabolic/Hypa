#!/usr/bin/env node
"use strict";

const { spawn } = require("child_process");
const fs = require("fs");
const path = require("path");

const PLATFORM_MAP = {
  linux:  { x64: "linux-x64",    arm64: "linux-arm64"  },
  darwin: { x64: "darwin-x64",   arm64: "darwin-arm64" },
};

// The release directory holds more than one executable. hypa starts the others
// (attach client, mux host, PTY helper) from beside itself. Some installers skip
// the platform package's postinstall script (pnpm 10 does by default), so make
// sure every one of them is executable before launching.
const EXECUTABLES = ["hypa", "hypa-attach", "hypa-annotate", "hypa-runtime", "hypa-pty-host"];

const archKey = PLATFORM_MAP[process.platform]?.[process.arch];
if (!archKey) {
  const hint = process.platform === "win32"
    ? "\n  The Hypa mux is not available on Windows. Build the CLI from source: https://github.com/Hypabolic/Hypa"
    : "";
  console.error(`[hypa] Unsupported platform/arch: ${process.platform}/${process.arch}${hint}`);
  process.exit(1);
}

const pkgName = `@hypabolic/hypa-${archKey}`;
let binDir;
try {
  binDir = path.join(path.dirname(require.resolve(`${pkgName}/package.json`)), "bin");
} catch {
  const libc = process.platform === "linux" ? "\n  Linux builds need glibc 2.34 or newer (musl, such as Alpine, is not supported)." : "";
  console.error(`[hypa] Could not find platform package ${pkgName}.${libc}\n  Try: npm install ${pkgName}`);
  process.exit(1);
}

for (const name of EXECUTABLES) {
  const file = path.join(binDir, name);
  try {
    if ((fs.statSync(file).mode & 0o111) === 0) fs.chmodSync(file, 0o755);
  } catch {
    // Missing or read-only: let the launch below report the real problem.
  }
}

const child = spawn(path.join(binDir, "hypa"), process.argv.slice(2), { stdio: "inherit" });

// A terminal delivers Ctrl-C to the whole foreground process group, so the child
// already has it. Forward the signals that arrive only at this process.
process.on("SIGINT", () => {});
for (const signal of ["SIGTERM", "SIGHUP"]) {
  process.on(signal, () => child.kill(signal));
}

child.on("error", (err) => {
  console.error(`[hypa] Failed to spawn binary: ${err.message}`);
  process.exit(1);
});

child.on("exit", (code, signal) => {
  if (signal) {
    // Report the death the way the child died, so shells see the right status.
    process.removeAllListeners(signal);
    process.kill(process.pid, signal);
    return;
  }
  process.exit(code ?? 1);
});
