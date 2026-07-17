import test from "node:test";
import assert from "node:assert/strict";
import { win32 } from "node:path";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import {
  resolveHypaBinary,
  resolveNativeHypaBinary,
  resolveBundledHypaBinary,
  rewriteCommand,
  getExecArgs,
} from "../extensions/rewrite-client.js";
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
  // Prefer native platform package when installed; otherwise bin.js.
  assert.match(
    resolved,
    /@hypabolic\/hypa|node_modules\/\.pnpm\/.*@hypabolic\+hypa|hypa-(linux|darwin|win32)-(x64|arm64)/,
  );
});

test("resolveNativeHypaBinary returns platform package binary when present", () => {
  const resolved = resolveNativeHypaBinary();
  // Optional platform dep is usually installed via @hypabolic/hypa; skip if missing.
  if (resolved === undefined) return;
  assert.match(resolved, /hypa-(linux|darwin|win32)-(x64|arm64)/);
  assert.match(resolved, /[\\/]bin[\\/]hypa(\.exe)?$/);
});

test("resolveBundledHypaBinary prefers native over bin.js when both exist", () => {
  const nativePath = "/fake/node_modules/@hypabolic/hypa-linux-x64/bin/hypa";
  const jsPath = "/fake/node_modules/@hypabolic/hypa/bin.js";
  const exists = fakeExists([nativePath, jsPath]);
  const requireResolve = (id: string) => {
    if (id === "@hypabolic/hypa-linux-x64/package.json") {
      return "/fake/node_modules/@hypabolic/hypa-linux-x64/package.json";
    }
    if (id === "@hypabolic/hypa/package.json") {
      return "/fake/node_modules/@hypabolic/hypa/package.json";
    }
    throw new Error(`unexpected resolve: ${id}`);
  };

  const resolved = resolveBundledHypaBinary("hypa", exists, requireResolve, "linux");
  assert.equal(resolved, nativePath);
});

test("resolveBundledHypaBinary falls back to bin.js when native is missing", () => {
  const jsPath = "/fake/node_modules/@hypabolic/hypa/bin.js";
  const exists = fakeExists([jsPath]);
  const requireResolve = (id: string) => {
    if (id.startsWith("@hypabolic/hypa-")) {
      throw new Error("native package not installed");
    }
    if (id === "@hypabolic/hypa/package.json") {
      return "/fake/node_modules/@hypabolic/hypa/package.json";
    }
    throw new Error(`unexpected resolve: ${id}`);
  };

  const resolved = resolveBundledHypaBinary("hypa", exists, requireResolve, "linux");
  assert.equal(resolved, jsPath);
});

test("resolveHypaBinary prefers real PATH native binary over bundled native", () => {
  const binDir = "/usr/local/bin";
  const pathHypa = "/usr/local/bin/hypa";
  const nativePath = "/fake/node_modules/@hypabolic/hypa-linux-x64/bin/hypa";
  const exists = fakeExists([pathHypa, nativePath]);
  const requireResolve = (id: string) => {
    if (id === "@hypabolic/hypa-linux-x64/package.json") {
      return "/fake/node_modules/@hypabolic/hypa-linux-x64/package.json";
    }
    throw new Error(`unexpected resolve: ${id}`);
  };

  const resolved = resolveHypaBinary("hypa", { PATH: binDir }, "linux", exists, requireResolve);
  assert.equal(resolved, pathHypa);
});

test("resolveHypaBinary prefers native over PATH JS entry when PATH hits a .js launcher", () => {
  // PATH can surface a .js entry (e.g. hypa.js or a bin.js-style launcher name).
  // Prefer the platform-native binary so Bun hosts never need node.
  const pathJsDir = "/opt/x";
  const pathJs = "/opt/x/hypa.js";
  const nativePath = "/fake/node_modules/@hypabolic/hypa-linux-x64/bin/hypa";
  const exists = fakeExists([pathJs, nativePath]);
  const requireResolve = (id: string) => {
    if (id === "@hypabolic/hypa-linux-x64/package.json") {
      return "/fake/node_modules/@hypabolic/hypa-linux-x64/package.json";
    }
    throw new Error(`unexpected resolve: ${id}`);
  };

  const resolved = resolveHypaBinary("hypa.js", { PATH: pathJsDir }, "linux", exists, requireResolve);
  assert.equal(resolved, nativePath);
});

test("resolveHypaBinary returns PATH JS entry when native is unavailable", () => {
  const pathJsDir = "/opt/x";
  const pathJs = "/opt/x/hypa.js";
  const exists = fakeExists([pathJs]);
  const requireResolve = (_id: string) => {
    throw new Error("native package not installed");
  };

  const resolved = resolveHypaBinary("hypa.js", { PATH: pathJsDir }, "linux", exists, requireResolve);
  assert.equal(resolved, pathJs);
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

test("resolveHypaBinary on Windows prefers bundled native binary over PATH .cmd shim", () => {
  const binDir = "C:\\Program Files\\nodejs";
  const cmd = win32.resolve(binDir, "hypa.cmd");
  const nativePath = "/fake/node_modules/@hypabolic/hypa-win32-x64/bin/hypa.exe";
  const exists = fakeExists([cmd, nativePath]);
  const requireResolve = (id: string) => {
    if (id === "@hypabolic/hypa-win32-x64/package.json") {
      return "/fake/node_modules/@hypabolic/hypa-win32-x64/package.json";
    }
    throw new Error(`unexpected resolve: ${id}`);
  };

  const resolved = resolveHypaBinary("hypa", { PATH: binDir }, "win32", exists, requireResolve);

  assert.equal(resolved.toLowerCase(), nativePath.toLowerCase());
  assert.notEqual(resolved.toLowerCase(), cmd.toLowerCase());
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

test("getExecArgs wraps .js binaries with process.execPath on win32", () => {
  assert.deepEqual(getExecArgs("/path/to/bin.js", ["-c", "echo hi"], "win32", process.execPath), [
    process.execPath,
    ["/path/to/bin.js", "-c", "echo hi"],
  ]);
});

test("getExecArgs wraps .js binaries with injected bun runtime on win32", () => {
  assert.deepEqual(getExecArgs("/path/to/bin.js", ["arg"], "win32", "/usr/local/bin/bun"), [
    "/usr/local/bin/bun",
    ["/path/to/bin.js", "arg"],
  ]);
});

test("getExecArgs wraps .js binaries with injected bun runtime on linux", () => {
  assert.deepEqual(getExecArgs("/path/bin.js", ["arg"], "linux", "/usr/local/bin/bun"), [
    "/usr/local/bin/bun",
    ["/path/bin.js", "arg"],
  ]);
});

test("getExecArgs wraps Windows .cmd binaries with cmd", () => {
  assert.deepEqual(getExecArgs("C:\\hypa.cmd", ["arg"], "win32"), ["cmd", ["/c", "C:\\hypa.cmd", "arg"]]);
});

test("getExecArgs passes through Windows .exe binaries", () => {
  assert.deepEqual(getExecArgs("hypa.exe", ["arg"], "win32"), ["hypa.exe", ["arg"]]);
});

test("getExecArgs wraps non-Windows .js binaries with jsRuntime", () => {
  assert.deepEqual(getExecArgs("/path/bin.js", ["arg"], "linux", process.execPath), [
    process.execPath,
    ["/path/bin.js", "arg"],
  ]);
});

test("getExecArgs wraps Windows .bat binaries with cmd", () => {
  assert.deepEqual(getExecArgs("C:\\hypa.bat", ["arg"], "win32"), ["cmd", ["/c", "C:\\hypa.bat", "arg"]]);
});

test("getExecArgs wraps Windows uppercase .JS binaries with jsRuntime", () => {
  assert.deepEqual(getExecArgs("/path/to/bin.JS", ["arg"], "win32", process.execPath), [
    process.execPath,
    ["/path/to/bin.JS", "arg"],
  ]);
});

test("getExecArgs wraps Windows uppercase .CMD binaries with cmd", () => {
  assert.deepEqual(getExecArgs("C:\\hypa.CMD", ["arg"], "win32"), ["cmd", ["/c", "C:\\hypa.CMD", "arg"]]);
});
