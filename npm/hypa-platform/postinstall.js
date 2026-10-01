"use strict";

const fs = require("fs");
const path = require("path");

// The release archive carries more than one executable. hypa starts the others
// (attach client, mux host, PTY helper) from the same directory, and a package
// manager may unpack them without the execute bit.
const EXECUTABLES = ["hypa", "hypa-attach", "hypa-annotate", "hypa-runtime", "hypa-pty-host"];

if (process.platform === "win32") process.exit(0);

for (const name of EXECUTABLES) {
  const file = path.join(__dirname, "bin", name);
  try {
    fs.chmodSync(file, 0o755);
  } catch (err) {
    if (err.code !== "ENOENT") {
      console.error(`[hypa postinstall] chmod ${name} failed: ${err.message}`);
    }
  }
}
