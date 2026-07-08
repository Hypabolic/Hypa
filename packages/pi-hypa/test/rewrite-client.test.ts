import test from "node:test";
import assert from "node:assert/strict";
import { win32 } from "node:path";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { resolveHypaBinary, rewriteCommand, getExecArgs } from "../extensions/rewrite-client.js";
import type { HypaPiConfig } from "../extensions/types.js";

const config: HypaPiConfig = {
  mode: "additive",
  binary: "hypa",
  rewriteTimeoutMs: 5000,
  askNonInteractive: "deny",
  mcpProxyEnabled: false,
  mcpProxyTimeoutMs: 10000,
};

function fakePi(stdout: string, overrides: Partial<{ code: number; stderr: string; killed: boolean }> = {}) {
  return {
    exec: async (_command: string, _args: string[]) => ({
      stdout,
      stderr: overrides.stderr ?? "",
      code: overrides.code ?? 0,
      killed: overrides.killed ?? false,
    }),
  } as unknown as ExtensionAPI;
}

function fakeExists(paths: string[]) {
  const existing = new Set(paths.map((path) => path.toLowerCase()));
  return (path: string) => existing.has(path.toLowerCase());
}

test("resolveHypaBinary falls back to bundled dependency when PATH is empty", () => {
  const resolved = resolveHypaBinary("hypa", { PATH: "" });
  assert.match(resolved, /@hypabolic\/hypa|node_modules\/\.pnpm\/.*@hypabolic\+hypa/);
});

test("resolveHypaBinary prefers Windows .cmd over extension-less npm shim", () => {
  const binDir = "C:\\Users\\test\\AppData\\Roaming\\npm";
  const shim = win32.resolve(binDir, "hypa");
  const cmd = win32.resolve(binDir, "hypa.cmd");

  const resolved = resolveHypaBinary("hypa", { PATH: binDir }, "win32", fakeExists([shim, cmd]));

  assert.equal(resolved.toLowerCase(), cmd.toLowerCase());
  assert.notEqual(resolved.toLowerCase(), shim.toLowerCase());
});

test("resolveHypaBinary prefers Windows .exe over .cmd in the same PATH directory", () => {
  const binDir = "C:\\hypa-bin";
  const exe = win32.resolve(binDir, "hypa.exe");
  const cmd = win32.resolve(binDir, "hypa.cmd");

  const resolved = resolveHypaBinary("hypa", { PATH: binDir }, "win32", fakeExists([exe, cmd]));

  assert.equal(resolved.toLowerCase(), exe.toLowerCase());
});

test("resolveHypaBinary does not return extension-less Windows shim without executable extension match", () => {
  const binDir = "C:\\hypa-bin";
  const shim = win32.resolve(binDir, "hypa");

  const resolved = resolveHypaBinary("hypa", { PATH: binDir }, "win32", fakeExists([shim]));

  assert.notEqual(resolved.toLowerCase(), shim.toLowerCase());
});

test("resolveHypaBinary returns explicit Windows .exe binary when it exists on PATH", () => {
  const binDir = "C:\\hypa-bin";
  const exe = win32.resolve(binDir, "hypa.exe");

  const resolved = resolveHypaBinary("hypa.exe", { PATH: binDir }, "win32", fakeExists([exe]));

  assert.equal(resolved.toLowerCase(), exe.toLowerCase());
});

test("resolveHypaBinary returns extension-less binary on non-Windows PATH", () => {
  const binDir = "/usr/local/bin";
  const shim = "/usr/local/bin/hypa";

  const resolved = resolveHypaBinary("hypa", { PATH: binDir }, "linux", fakeExists([shim]));

  assert.equal(resolved, shim);
});

test("resolveHypaBinary honours Windows PATHEXT priority case-insensitively", () => {
  const binDir = "C:\\hypa-bin";
  const foo = win32.resolve(binDir, "hypa.foo");
  const cmd = win32.resolve(binDir, "hypa.cmd");

  const resolved = resolveHypaBinary(
    "hypa",
    { PATH: binDir, PATHEXT: ".FOO;.CMD" },
    "win32",
    fakeExists([foo, cmd]),
  );

  assert.equal(resolved.toLowerCase(), foo.toLowerCase());
});

test("rewriteCommand skips commands already starting with hypa", async () => {
  const status = await rewriteCommand(fakePi(""), config, "hypa git status");
  assert.equal(status.kind, "skipped");
});

test("rewriteCommand parses stdout for non-zero expected outcomes", async () => {
  const status = await rewriteCommand(
    fakePi(JSON.stringify({ input: "echo ok", outcome: "Passthrough", command: "echo ok" }), { code: 1 }),
    config,
    "echo ok",
  );
  assert.equal(status.kind, "passthrough");
});

test("rewriteCommand fails safe on malformed JSON", async () => {
  const status = await rewriteCommand(fakePi("not-json"), config, "git status");
  assert.equal(status.kind, "error");
});

test("rewriteCommand fails safe on timeout", async () => {
  const status = await rewriteCommand(fakePi("", { killed: true }), config, "git status");
  assert.equal(status.kind, "error");
  assert.match(status.kind === "error" ? status.error : "", /timed out/);
});

test("getExecArgs wraps Windows .js binaries with node", () => {
  assert.deepEqual(getExecArgs("/path/to/bin.js", ["-c", "echo hi"], "win32"), [
    "node",
    ["/path/to/bin.js", "-c", "echo hi"],
  ]);
});

test("getExecArgs wraps Windows .cmd binaries with cmd", () => {
  assert.deepEqual(getExecArgs("C:\\hypa.cmd", ["arg"], "win32"), ["cmd", ["/c", "C:\\hypa.cmd", "arg"]]);
});

test("getExecArgs passes through Windows .exe binaries", () => {
  assert.deepEqual(getExecArgs("hypa.exe", ["arg"], "win32"), ["hypa.exe", ["arg"]]);
});

test("getExecArgs passes through non-Windows .js binaries", () => {
  assert.deepEqual(getExecArgs("/path/bin.js", ["arg"], "linux"), ["/path/bin.js", ["arg"]]);
});

test("getExecArgs wraps Windows .bat binaries with cmd", () => {
  assert.deepEqual(getExecArgs("C:\\hypa.bat", ["arg"], "win32"), ["cmd", ["/c", "C:\\hypa.bat", "arg"]]);
});

test("getExecArgs wraps Windows uppercase .JS binaries with node", () => {
  assert.deepEqual(getExecArgs("/path/to/bin.JS", ["arg"], "win32"), ["node", ["/path/to/bin.JS", "arg"]]);
});

test("getExecArgs wraps Windows uppercase .CMD binaries with cmd", () => {
  assert.deepEqual(getExecArgs("C:\\hypa.CMD", ["arg"], "win32"), ["cmd", ["/c", "C:\\hypa.CMD", "arg"]]);
});
