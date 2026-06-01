import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { executeMcpProxyAction, loadPiMcpServerNames } from "../extensions/mcp-proxy-bridge.js";
import type { HypaPiConfig } from "../extensions/types.js";

function config(piMcpConfigPath?: string): HypaPiConfig {
  return {
    mode: "additive",
    binary: "hypa",
    rewriteTimeoutMs: 5000,
    askNonInteractive: "deny",
    mcpProxyEnabled: true,
    mcpProxyTimeoutMs: 10000,
    piMcpConfigPath,
  };
}

function mockPi(stdout: unknown) {
  const calls: Array<{ command: string; args: string[]; options?: Record<string, unknown> }> = [];
  return {
    calls,
    async exec(command: string, args: string[], options?: Record<string, unknown>) {
      calls.push({ command, args, options });
      return { stdout: JSON.stringify(stdout), stderr: "", code: 0 };
    },
    registerTool() {},
  };
}

test("loadPiMcpServerNames supports Pi-style object and array config shapes", async () => {
  const dir = await mkdtemp(join(tmpdir(), "pi-hypa-mcp-test-"));
  const path = join(dir, "mcp.json");
  await writeFile(path, JSON.stringify({ mcpServers: { github: {} }, servers: [{ name: "linear" }] }), "utf8");

  const names = loadPiMcpServerNames(path);
  assert.equal(names.has("github"), true);
  assert.equal(names.has("linear"), true);
});

test("list action filters servers already configured directly in Pi", async () => {
  const dir = await mkdtemp(join(tmpdir(), "pi-hypa-mcp-test-"));
  const path = join(dir, "mcp.json");
  await writeFile(path, JSON.stringify({ mcpServers: { github: {} } }), "utf8");
  const pi = mockPi([
    { name: "github", transport: "stdio", endpoint: null, auth: "None", hasTls: false },
    { name: "notion", transport: "sse", endpoint: "https://example.test", auth: "Bearer", hasTls: true },
  ]);

  const result = await executeMcpProxyAction(pi, config(path), { action: "list" });

  assert.match(result.text, /notion/);
  assert.doesNotMatch(result.text, /github/);
  assert.deepEqual(pi.calls[0].args, ["mcp", "list", "--json"]);
});

test("search action uses compact Hypa mcp search json and filters duplicates", async () => {
  const dir = await mkdtemp(join(tmpdir(), "pi-hypa-mcp-test-"));
  const path = join(dir, "mcp.json");
  await writeFile(path, JSON.stringify({ mcpServers: { github: {} } }), "utf8");
  const pi = mockPi([
    { serverName: "github", toolName: "search", description: "Search GitHub", score: 2 },
    { serverName: "linear", toolName: "issues", description: "Search issues", score: 1.5 },
  ]);

  const result = await executeMcpProxyAction(pi, config(path), { action: "search", query: "issues" });

  assert.match(result.text, /linear\/issues/);
  assert.doesNotMatch(result.text, /github\/search/);
  assert.deepEqual(pi.calls[0].args, ["mcp", "search", "--query", "issues", "--json"]);
});

test("schema action requests schema lazily only when action=schema", async () => {
  const pi = mockPi({
    servers: [{ serverName: "linear", tools: [{ name: "issues", description: "List issues", inputSchema: { type: "object" } }] }],
    errors: null,
  });

  const result = await executeMcpProxyAction(pi, config(), { action: "schema", server: "linear" });

  assert.match(result.text, /linear/);
  assert.match(result.text, /inputSchema/);
  assert.deepEqual(pi.calls[0].args, ["mcp", "schema", "--server", "linear", "--json"]);
});

test("invoke action refuses duplicate server unless explicitly included", async () => {
  const dir = await mkdtemp(join(tmpdir(), "pi-hypa-mcp-test-"));
  const path = join(dir, "mcp.json");
  await writeFile(path, JSON.stringify({ mcpServers: { github: {} } }), "utf8");
  const pi = mockPi({ serverName: "github", toolName: "search", compressedResponse: "ok", isError: false });

  const result = await executeMcpProxyAction(pi, config(path), { action: "invoke", server: "github", tool: "search" });

  assert.match(result.text, /configured directly in Pi/);
  assert.equal(pi.calls.length, 0);
});

test("invoke action maps arguments and hint to hypa mcp invoke", async () => {
  const pi = mockPi({ serverName: "linear", toolName: "issues", compressedResponse: "ok", isError: false });

  const result = await executeMcpProxyAction(pi, config(), {
    action: "invoke",
    server: "linear",
    tool: "issues",
    arguments: { assignee: "me" },
    hint: "summary",
  });

  assert.equal(result.text, "ok");
  assert.deepEqual(pi.calls[0].args, [
    "mcp",
    "invoke",
    "--server",
    "linear",
    "--tool",
    "issues",
    "--arguments",
    JSON.stringify({ assignee: "me" }),
    "--json",
    "--hint",
    "summary",
  ]);
});
