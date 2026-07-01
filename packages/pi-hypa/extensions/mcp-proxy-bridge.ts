import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import type { HypaPiConfig } from "./types.js";
import { getExecArgs } from "./rewrite-client.js";

type PiToolParams = Record<string, any>;
type HypaExecResult = { stdout: string; stderr: string; code: number; killed?: boolean };

type PiToolExecute = (
  toolCallId: string,
  params: PiToolParams,
  signal?: AbortSignal,
  onUpdate?: unknown,
  ctx?: unknown,
) => Promise<{ content: Array<{ type: "text"; text: string }>; details?: unknown }>;

type PiApi = {
  exec(command: string, args: string[], options?: Record<string, unknown>): Promise<HypaExecResult>;
  registerTool(definition: Record<string, unknown> & { execute: PiToolExecute }): void;
};

export type McpProxyAction = "list" | "search" | "schema" | "invoke" | "auth_check";

interface McpServerListItem {
  name: string;
  transport?: string;
  endpoint?: string | null;
  auth?: string;
  hasTls?: boolean;
}

interface McpToolSearchResult {
  serverName: string;
  toolName: string;
  description: string;
  score?: number;
}

interface McpSchemaManifest {
  servers?: Array<{ serverName: string; tools: Array<{ name: string; description: string; inputSchema?: unknown }> }>;
  errors?: Array<{ serverName: string; code: string; message: string }> | null;
}

interface McpInvokeResult {
  serverName: string;
  toolName: string;
  compressedResponse?: string;
  isError?: boolean;
  error?: { code?: string; message?: string } | null;
  latency?: unknown;
}

const bridgeSchema = {
  type: "object",
  properties: {
    action: {
      type: "string",
      enum: ["list", "search", "schema", "invoke", "auth_check"],
      description: "Proxy operation over upstream MCP servers configured in Hypa.",
    },
    query: { type: "string", description: "Search query for action=search." },
    server: { type: "string", description: "Hypa upstream MCP server name for schema/invoke/auth_check." },
    tool: { type: "string", description: "Upstream tool name for action=invoke." },
    arguments: { description: "JSON object or JSON string of arguments for action=invoke." },
    hint: { type: "string", enum: ["raw", "summary", "structured"], description: "Compression hint for action=invoke." },
    includeDuplicates: {
      type: "boolean",
      description: "Include Hypa upstream servers that appear to be configured directly in Pi. Default false.",
    },
    timeoutMs: { type: "number", description: "Per-call timeout in milliseconds." },
  },
  required: ["action"],
  additionalProperties: false,
} as const;

function jsonText(value: unknown): string {
  return JSON.stringify(value, null, 2);
}

function asJsonArgument(value: unknown): string {
  if (value === undefined || value === null) return "{}";
  if (typeof value === "string") return value.trim() || "{}";
  return JSON.stringify(value);
}

function parseJson<T>(result: HypaExecResult, operation: string): T {
  if (result.killed) throw new Error(`${operation} timed out or was killed`);
  if (!result.stdout?.trim()) {
    const detail = result.stderr?.trim() || `exit code ${result.code}`;
    throw new Error(`${operation} produced no JSON (${detail})`);
  }
  return JSON.parse(result.stdout) as T;
}

function defaultPiMcpConfigPath(): string {
  return join(homedir(), ".pi", "agent", "mcp.json");
}

function collectNamesFromObject(value: unknown, names: Set<string>) {
  if (!value || typeof value !== "object") return;
  for (const key of Object.keys(value as Record<string, unknown>)) {
    if (key.trim()) names.add(key.trim().toLowerCase());
  }
}

function collectNamesFromArray(value: unknown, names: Set<string>) {
  if (!Array.isArray(value)) return;
  for (const item of value) {
    if (!item || typeof item !== "object") continue;
    const name = (item as Record<string, unknown>).name;
    if (typeof name === "string" && name.trim()) names.add(name.trim().toLowerCase());
  }
}

export function loadPiMcpServerNames(configPath?: string): Set<string> {
  const path = configPath || defaultPiMcpConfigPath();
  if (!existsSync(path)) return new Set();

  try {
    const root = JSON.parse(readFileSync(path, "utf8")) as Record<string, unknown>;
    const names = new Set<string>();
    collectNamesFromObject(root.mcpServers, names);
    collectNamesFromObject(root.mcp_servers, names);
    collectNamesFromObject(root.servers, names);
    collectNamesFromArray(root.mcpServers, names);
    collectNamesFromArray(root.mcp_servers, names);
    collectNamesFromArray(root.servers, names);
    return names;
  } catch {
    return new Set();
  }
}

function isDuplicate(serverName: string, piServerNames: Set<string>): boolean {
  return piServerNames.has(serverName.trim().toLowerCase());
}

function filterDuplicateServers<T extends { name?: string; serverName?: string }>(
  items: T[],
  piServerNames: Set<string>,
  includeDuplicates: boolean,
): T[] {
  if (includeDuplicates) return items;
  return items.filter((item) => !isDuplicate(item.name ?? item.serverName ?? "", piServerNames));
}

async function runHypaMcpJson<T>(
  pi: PiApi,
  config: HypaPiConfig,
  args: string[],
  timeoutMs: number | undefined,
  signal?: AbortSignal,
): Promise<T> {
  const timeout = timeoutMs ?? config.mcpProxyTimeoutMs;
  const [execBin, execArgs] = getExecArgs(config.binary, ["mcp", ...args]);
  const result = await pi.exec(execBin, execArgs, { signal, timeout });
  return parseJson<T>(result, `hypa mcp ${args.join(" ")}`);
}

function formatList(items: McpServerListItem[], duplicateNames: Set<string>): string {
  if (items.length === 0) return "No Hypa upstream MCP servers available after Pi deduplication.";
  const lines = ["Hypa upstream MCP servers:"];
  for (const item of items) {
    const duplicate = isDuplicate(item.name, duplicateNames) ? " [also configured directly in Pi]" : "";
    lines.push(`- ${item.name}${duplicate}: ${item.transport ?? "unknown"} ${item.endpoint ?? "—"} auth=${item.auth ?? "unknown"}`);
  }
  return lines.join("\n");
}

function formatSearch(results: McpToolSearchResult[]): string {
  if (results.length === 0) return "No matching Hypa upstream MCP tools after Pi deduplication.";
  return [
    `Found ${results.length} Hypa upstream MCP tool(s):`,
    ...results.map((r) => `- ${r.serverName}/${r.toolName}${r.score === undefined ? "" : ` score=${r.score.toFixed(2)}`}: ${r.description}`),
  ].join("\n");
}

function formatSchema(manifest: McpSchemaManifest, duplicateNames: Set<string>, includeDuplicates: boolean): string {
  const servers = filterDuplicateServers(manifest.servers ?? [], duplicateNames, includeDuplicates);
  if (servers.length === 0) return "No Hypa upstream MCP schemas available after Pi deduplication.";

  const lines = ["Hypa upstream MCP schema:"];
  for (const server of servers) {
    const duplicate = isDuplicate(server.serverName, duplicateNames) ? " [also configured directly in Pi]" : "";
    lines.push(`- ${server.serverName}${duplicate} (${server.tools.length} tool(s))`);
    for (const tool of server.tools) {
      lines.push(`  - ${tool.name}: ${tool.description}`);
      if (tool.inputSchema !== undefined) lines.push(`    inputSchema: ${jsonText(tool.inputSchema)}`);
    }
  }

  if (manifest.errors?.length) {
    lines.push("Warnings:");
    for (const error of manifest.errors) lines.push(`- ${error.serverName} ${error.code}: ${error.message}`);
  }

  return lines.join("\n");
}

function formatInvoke(result: McpInvokeResult): string {
  if (result.isError) {
    return `Hypa MCP proxy error for ${result.serverName}/${result.toolName}: ${result.error?.code ?? "unknown"}: ${result.error?.message ?? "Tool invocation failed."}`;
  }
  return result.compressedResponse || `(Hypa MCP proxy invocation ${result.serverName}/${result.toolName} returned no content)`;
}

export async function executeMcpProxyAction(
  pi: PiApi,
  config: HypaPiConfig,
  params: PiToolParams,
  signal?: AbortSignal,
) {
  const action = params.action as McpProxyAction;
  const includeDuplicates = params.includeDuplicates === true;
  const duplicateNames = loadPiMcpServerNames(config.piMcpConfigPath);
  const timeoutMs = typeof params.timeoutMs === "number" ? Math.max(1, Math.floor(params.timeoutMs)) : undefined;

  switch (action) {
    case "list": {
      const items = await runHypaMcpJson<McpServerListItem[]>(pi, config, ["list", "--json"], timeoutMs, signal);
      const visible = filterDuplicateServers(items, duplicateNames, includeDuplicates);
      return { text: formatList(visible, duplicateNames), data: visible };
    }
    case "search": {
      if (typeof params.query !== "string" || !params.query.trim()) throw new Error("action=search requires query");
      const results = await runHypaMcpJson<McpToolSearchResult[]>(pi, config, ["search", "--query", params.query, "--json"], timeoutMs, signal);
      const visible = filterDuplicateServers(results, duplicateNames, includeDuplicates);
      return { text: formatSearch(visible), data: visible };
    }
    case "schema": {
      const args = ["schema"];
      if (typeof params.server === "string" && params.server.trim()) args.push("--server", params.server);
      args.push("--json");
      const manifest = await runHypaMcpJson<McpSchemaManifest>(pi, config, args, timeoutMs, signal);
      return { text: formatSchema(manifest, duplicateNames, includeDuplicates), data: manifest };
    }
    case "invoke": {
      if (typeof params.server !== "string" || !params.server.trim()) throw new Error("action=invoke requires server");
      if (typeof params.tool !== "string" || !params.tool.trim()) throw new Error("action=invoke requires tool");
      if (!includeDuplicates && isDuplicate(params.server, duplicateNames)) {
        return {
          text: `Server '${params.server}' appears to be configured directly in Pi. Use Pi's direct MCP tool or set includeDuplicates=true to invoke through Hypa anyway.`,
          data: { duplicate: true, serverName: params.server },
        };
      }
      const args = ["invoke", "--server", params.server, "--tool", params.tool, "--arguments", asJsonArgument(params.arguments), "--json"];
      if (typeof params.hint === "string" && params.hint.trim()) args.push("--hint", params.hint);
      const result = await runHypaMcpJson<McpInvokeResult>(pi, config, args, timeoutMs, signal);
      return { text: formatInvoke(result), data: result };
    }
    case "auth_check": {
      if (typeof params.server !== "string" || !params.server.trim()) throw new Error("action=auth_check requires server");
      const result = await runHypaMcpJson<unknown>(pi, config, ["auth", "check", "--server", params.server, "--json"], timeoutMs, signal);
      return { text: jsonText(result), data: result };
    }
    default:
      throw new Error(`Unknown Hypa MCP proxy action: ${String(params.action)}`);
  }
}

export function registerHypaMcpProxyBridge(pi: PiApi, config: HypaPiConfig) {
  if (!config.mcpProxyEnabled) return;

  pi.registerTool({
    name: "hypa_mcp_proxy",
    label: "hypa_mcp_proxy",
    description:
      "Discover and invoke upstream MCP servers configured in Hypa via Hypa's proxy/passthrough service. This lazy bridge does not register every upstream MCP tool in Pi context.",
    promptSnippet: "Discover Hypa-configured upstream MCP connections and invoke selected tools on demand.",
    promptGuidelines: [
      "Use action=list or action=search first to discover Hypa upstream MCP connections compactly.",
      "Use action=schema only for a selected server/tool when details are needed.",
      "Prefer Pi's directly configured MCP tools when the server is marked as already configured in Pi.",
    ],
    parameters: bridgeSchema,
    async execute(_toolCallId, params, signal) {
      try {
        const result = await executeMcpProxyAction(pi, config, params, signal);
        return {
          content: [{ type: "text" as const, text: result.text }],
          details: { source: "hypa-mcp-proxy", action: params.action, data: result.data },
        };
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        return {
          content: [{ type: "text" as const, text: `Hypa MCP proxy bridge error: ${message}` }],
          details: { source: "hypa-mcp-proxy", action: params.action, error: message },
        };
      }
    },
  });
}
