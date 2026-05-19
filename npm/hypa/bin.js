#!/usr/bin/env node
"use strict";

const { spawnSync } = require("child_process");
const path = require("path");

const PLATFORM_MAP = {
  linux:  { x64: "linux-x64",    arm64: "linux-arm64"  },
  darwin: { x64: "darwin-x64",   arm64: "darwin-arm64" },
  win32:  { x64: "win32-x64",    arm64: "win32-arm64"  },
};

const archKey = PLATFORM_MAP[process.platform]?.[process.arch];
if (!archKey) {
  console.error(`[hypa] Unsupported platform/arch: ${process.platform}/${process.arch}`);
  process.exit(1);
}

const pkgName = `@hypabolic/hypa-${archKey}`;
let binaryPath;
try {
  const pkgDir = path.dirname(require.resolve(`${pkgName}/package.json`));
  binaryPath = path.join(pkgDir, "bin", process.platform === "win32" ? "hypa.exe" : "hypa");
} catch {
  console.error(`[hypa] Could not find platform package ${pkgName}.\n  Try: npm install ${pkgName}`);
  process.exit(1);
}

const result = spawnSync(binaryPath, process.argv.slice(2), {
  stdio: "inherit",
  env: { ...process.env, HYPA_INSTALL_SOURCE: "npm" },
});
if (result.error) {
  console.error(`[hypa] Failed to spawn binary: ${result.error.message}`);
  process.exit(1);
}
process.exit(result.status ?? 1);
