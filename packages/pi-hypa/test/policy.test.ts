import test from "node:test";
import assert from "node:assert/strict";
import { isHypaCommand, loadConfig, mapRewriteResult, parseRewriteJson } from "../extensions/policy.js";

test("parseRewriteJson accepts v1 rewrite result", () => {
  const parsed = parseRewriteJson(JSON.stringify({ input: "git status", outcome: "Rewritten", command: "hypa git status" }));
  assert.equal(parsed.outcome, "Rewritten");
  assert.equal(parsed.command, "hypa git status");
});

test("parseRewriteJson rejects unknown outcomes", () => {
  assert.throws(
    () => parseRewriteJson(JSON.stringify({ input: "x", outcome: "Maybe", command: "x" })),
    /unknown outcome/,
  );
});

test("mapRewriteResult maps rewrite and passthrough outcomes", () => {
  assert.deepEqual(
    mapRewriteResult({ input: "git status", outcome: "GenericWrapper", command: "hypa git status" }),
    { kind: "rewritten", outcome: "GenericWrapper", input: "git status", command: "hypa git status" },
  );
  assert.equal(
    mapRewriteResult({ input: "echo ok", outcome: "Passthrough", command: "echo ok" }).kind,
    "passthrough",
  );
});

test("mapRewriteResult maps deny and ask outcomes", () => {
  assert.equal(mapRewriteResult({ input: "rm -rf /", outcome: "Deny", command: "rm -rf /" }).kind, "deny");
  assert.equal(mapRewriteResult({ input: "sudo reboot", outcome: "Ask", command: "sudo reboot" }).kind, "ask");
});

test("isHypaCommand prevents direct hypa rewrite loops", () => {
  assert.equal(isHypaCommand("hypa git status"), true);
  assert.equal(isHypaCommand("   hypa"), true);
  assert.equal(isHypaCommand("echo hypa"), false);
});

test("loadConfig applies deterministic defaults", () => {
  const config = loadConfig({});
  assert.equal(config.mode, "additive");
  assert.equal(config.binary, "hypa");
  assert.equal(config.rewriteTimeoutMs, 5000);
  assert.equal(config.askNonInteractive, "deny");
  assert.equal(config.mcpProxyEnabled, false);
  assert.equal(config.mcpProxyTimeoutMs, 10000);
});

test("loadConfig enables MCP proxy discovery with preferred flag", () => {
  const config = loadConfig({ HYPA_PI_ENABLE_MCP_PROXY: "1", HYPA_PI_MCP_PROXY_TIMEOUT_MS: "2500", HYPA_PI_MCP_CONFIG: "/tmp/pi-mcp.json" });
  assert.equal(config.mcpProxyEnabled, true);
  assert.equal(config.mcpProxyTimeoutMs, 2500);
  assert.equal(config.piMcpConfigPath, "/tmp/pi-mcp.json");
});
