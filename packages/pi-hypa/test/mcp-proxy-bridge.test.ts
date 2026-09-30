import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { executeMcpProxyAction, loadPiMcpServerNames } from "../extensions/mcp-proxy-bridge.js";
import { resolveHypaBinary } from "../extensions/rewrite-client.js";
import type { HypaPiConfig } from "../extensions/types.js";
import extension from "../extensions/index.js";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

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

test("executeMcpProxyAction receives resolved binary in bundled-fallback case", async () => {
  const resolved = resolveHypaBinary("hypa", { PATH: "" });
  const pi = mockPi([]);
  const cfg: HypaPiConfig = { ...config(), binary: resolved };

  await executeMcpProxyAction(pi, cfg, { action: "list" });

  assert.equal(pi.calls[0].command, resolved);
  assert.deepEqual(pi.calls[0].args, ["mcp", "list", "--json"]);
});

test("extension factory wires hypa_mcp_proxy via effectiveConfig (resolved binary, not 'hypa')", async () => {
  const originalPath = process.env.PATH;
  const originalMcp = process.env.HYPA_PI_ENABLE_MCP_PROXY;
  process.env.PATH = "";
  process.env.HYPA_PI_ENABLE_MCP_PROXY = "1";

  const tools: Array<{ name: string; execute: (...args: any[]) => Promise<any> }> = [];
  const calls: Array<{ command: string; args: string[]; options?: Record<string, unknown> }> = [];

  const pi: ExtensionAPI = {
    registerTool(t: any) {
      tools.push(t);
    },
    async exec(command: string, args: string[], options?: Record<string, unknown>) {
      calls.push({ command, args, options });
      return { stdout: "[]", stderr: "", code: 0 };
    },
    on() {},
    registerCommand() {},
    getActiveTools() {
      return [];
    },
    setActiveTools() {},
  } as unknown as ExtensionAPI;

  try {
    extension(pi);

    const proxy = tools.find((t) => t.name === "hypa_mcp_proxy");
    assert.ok(proxy, "hypa_mcp_proxy tool must be registered when HYPA_PI_ENABLE_MCP_PROXY=1");

    await proxy.execute("id", { action: "list" });

    const resolved = resolveHypaBinary("hypa", { PATH: "" });

    assert.equal(calls.length, 1, "exec must be invoked once for list action");
    assert.equal(calls[0].command, resolved, "must use resolved bundled binary path");
    assert.notEqual(calls[0].command, "hypa", "must not fall back to literal 'hypa' (would indicate config vs effectiveConfig bug)");
    assert.deepEqual(calls[0].args, ["mcp", "list", "--json"]);
  } finally {
    if (originalPath === undefined) {
      delete process.env.PATH;
    } else {
      process.env.PATH = originalPath;
    }
    if (originalMcp === undefined) {
      delete process.env.HYPA_PI_ENABLE_MCP_PROXY;
    } else {
      process.env.HYPA_PI_ENABLE_MCP_PROXY = originalMcp;
    }
  }
});
