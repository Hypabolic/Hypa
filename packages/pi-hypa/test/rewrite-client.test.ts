import test from "node:test";
import assert from "node:assert/strict";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { resolveHypaBinary, rewriteCommand } from "../extensions/rewrite-client.js";
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

test("resolveHypaBinary falls back to bundled dependency when PATH is empty", () => {
  const resolved = resolveHypaBinary("hypa", { PATH: "" });
  assert.match(resolved, /@hypabolic\/hypa|node_modules\/\.pnpm\/.*@hypabolic\+hypa/);
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
