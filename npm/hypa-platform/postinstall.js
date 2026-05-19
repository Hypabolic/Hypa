"use strict";

const fs = require("fs");
const path = require("path");

if (process.platform === "win32") process.exit(0);

const binPath = path.join(__dirname, "bin", "hypa");
try {
  fs.chmodSync(binPath, 0o755);
} catch (err) {
  if (err.code !== "ENOENT") {
    console.error(`[hypa postinstall] chmod failed: ${err.message}`);
  }
}
