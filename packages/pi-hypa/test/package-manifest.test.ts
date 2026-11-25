import test from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";

const manifest = JSON.parse(
  readFileSync(new URL("../package.json", import.meta.url), "utf8"),
);

test("@earendil-works/pi-coding-agent is supplied by the Pi host, not installed as a runtime dependency", () => {
  assert.equal(manifest.peerDependencies?.["@earendil-works/pi-coding-agent"], "*");
  assert.equal(manifest.dependencies?.["@earendil-works/pi-coding-agent"], undefined);
});

// omp 19.2 refuses to load an extension that imports @earendil-works/pi-tui, so nothing may depend on it.
test("@earendil-works/pi-tui is not a dependency of any kind", () => {
  for (const field of ["dependencies", "peerDependencies", "devDependencies"]) {
    assert.equal(manifest[field]?.["@earendil-works/pi-tui"], undefined, field);
  }
});

test("no extension module imports @earendil-works/pi-tui", () => {
  for (const file of readdirSync(new URL("../extensions/", import.meta.url))) {
    const source = readFileSync(new URL(`../extensions/${file}`, import.meta.url), "utf8");
    assert.doesNotMatch(source, /["']@earendil-works\/pi-tui/, file);
  }
});
