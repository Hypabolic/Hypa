import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const manifest = JSON.parse(
  readFileSync(new URL("../package.json", import.meta.url), "utf8"),
);

for (const name of [
  "@earendil-works/pi-coding-agent",
  "@earendil-works/pi-tui",
]) {
  test(`${name} is supplied by the Pi host, not installed as a runtime dependency`, () => {
    assert.equal(manifest.peerDependencies?.[name], "*");
    assert.equal(manifest.dependencies?.[name], undefined);
  });
}
