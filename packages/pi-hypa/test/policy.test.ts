import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { after } from "node:test";
import assert from "node:assert/strict";
import { isHypaCommand, loadConfig, loadConfigFile, mapRewriteResult, parseRewriteJson } from "../extensions/policy.js";

const tempRoot = mkdtempSync(join(tmpdir(), "pi-hypa-policy-"));

after(() => {
  rmSync(tempRoot, { recursive: true, force: true });
});

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
  const config = loadConfig({ HYPA_PI_CONFIG: "none" });
  assert.equal(config.mode, "additive");
  assert.equal(config.binary, "hypa");
  assert.equal(config.rewriteTimeoutMs, 5000);
  assert.equal(config.askNonInteractive, "deny");
  assert.equal(config.mcpProxyEnabled, false);
  assert.equal(config.mcpProxyTimeoutMs, 10000);
});

test("loadConfig enables MCP proxy discovery with preferred flag", () => {
  const config = loadConfig({
    HYPA_PI_CONFIG: "none",
    HYPA_PI_ENABLE_MCP_PROXY: "1",
    HYPA_PI_MCP_PROXY_TIMEOUT_MS: "2500",
    HYPA_PI_MCP_CONFIG: "/tmp/pi-mcp.json",
  });
  assert.equal(config.mcpProxyEnabled, true);
  assert.equal(config.mcpProxyTimeoutMs, 2500);
  assert.equal(config.piMcpConfigPath, "/tmp/pi-mcp.json");
});

test("loadConfig uses config file defaults with environment overrides", () => {
  const configPath = join(tempRoot, "config-with-overrides.json");
  writeFileSync(
    configPath,
    JSON.stringify({
      mode: "replace",
      binary: "/usr/local/bin/hypa-file",
      rewriteTimeoutMs: 7000,
      askNonInteractive: "allow",
      mcpProxyEnabled: true,
      mcpProxyTimeoutMs: 15000,
      piMcpConfigPath: "/tmp/file-mcp.json",
    }),
  );

  const config = loadConfig(
    {
      HYPA_PI_CONFIG: configPath,
      HYPA_PI_MODE: "additive",
      HYPA_BIN: "/usr/local/bin/hypa-env",
      HYPA_PI_REWRITE_TIMEOUT_MS: "9000",
      HYPA_PI_ENABLE_MCP_PROXY: "0",
      HYPA_PI_MCP_CONFIG: "/tmp/env-mcp.json",
    },
    configPath,
  );

  assert.equal(config.mode, "additive");
  assert.equal(config.binary, "/usr/local/bin/hypa-env");
  assert.equal(config.rewriteTimeoutMs, 9000);
  assert.equal(config.askNonInteractive, "allow");
  assert.equal(config.mcpProxyEnabled, false);
  assert.equal(config.mcpProxyTimeoutMs, 15000);
  assert.equal(config.piMcpConfigPath, "/tmp/env-mcp.json");
});

test("loadConfigFile parses valid JSON", () => {
  const configPath = join(tempRoot, "valid-config.json");
  writeFileSync(
    configPath,
    JSON.stringify({
      mode: "replace",
      binary: " /opt/hypa ",
      rewriteTimeoutMs: 6000,
      askNonInteractive: "allow",
      mcpProxyEnabled: true,
      mcpProxyTimeoutMs: 12000,
      piMcpConfigPath: " /tmp/pi-mcp.json ",
    }),
  );

  assert.deepEqual(loadConfigFile(configPath), {
    mode: "replace",
    binary: "/opt/hypa",
    rewriteTimeoutMs: 6000,
    askNonInteractive: "allow",
    mcpProxyEnabled: true,
    mcpProxyTimeoutMs: 12000,
    piMcpConfigPath: "/tmp/pi-mcp.json",
  });
});

test("loadConfigFile returns an empty object when file is missing", () => {
  assert.deepEqual(loadConfigFile(join(tempRoot, "missing.json")), {});
});

test("loadConfigFile throws a descriptive error on malformed JSON", () => {
  const configPath = join(tempRoot, "malformed.json");
  writeFileSync(configPath, '{ "mode": "replace", }');
  assert.throws(() => loadConfigFile(configPath), /Failed to parse config file.*malformed\.json/);
});
