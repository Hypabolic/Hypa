import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import type { AskNonInteractivePolicy, HypaPiConfig, HypaPiMode, RewriteResultV1, RewriteStatus } from "./types.js";

const VALID_OUTCOMES = new Set(["Rewritten", "GenericWrapper", "Passthrough", "Deny", "Ask"]);

export function parseMode(value: string | undefined): HypaPiMode {
  return value?.trim().toLowerCase() === "replace" ? "replace" : "additive";
}

export function parseAskNonInteractive(value: string | undefined): AskNonInteractivePolicy {
  return value?.trim().toLowerCase() === "allow" ? "allow" : "deny";
}

export function parsePositiveInteger(value: string | undefined, fallback: number): number {
  if (!value) return fallback;
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : fallback;
}

export function parseBooleanFlag(value: string | undefined): boolean {
  const normalized = value?.trim().toLowerCase();
  return normalized === "1" || normalized === "true" || normalized === "yes" || normalized === "on";
}

export function resolveConfigFilePath(env: NodeJS.ProcessEnv): string | undefined {
  const fromEnv = env.HYPA_PI_CONFIG?.trim();
  if (fromEnv !== undefined) {
    if (fromEnv === "" || fromEnv.toLowerCase() === "none") return undefined;
    return fromEnv;
  }
  return path.join(os.homedir(), ".hypa-pi", "config.json");
}

export function loadConfigFile(filePath: string): Partial<HypaPiConfig> {
  let raw: string;
  try {
    raw = fs.readFileSync(filePath, "utf8");
  } catch (err) {
    if ((err as NodeJS.ErrnoException).code === "ENOENT") return {};
    throw err;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (err) {
    throw new Error(`Failed to parse config file ${filePath}: ${(err as Error).message}`);
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return {};

  const config = parsed as Record<string, unknown>;
  const result: Partial<HypaPiConfig> = {};
  if (typeof config.mode === "string") result.mode = parseMode(config.mode);
  if (typeof config.binary === "string" && config.binary.trim()) result.binary = config.binary.trim();
  if (typeof config.rewriteTimeoutMs === "number") {
    const value = parsePositiveInteger(String(config.rewriteTimeoutMs), 0);
    if (value > 0) result.rewriteTimeoutMs = value;
  }
  if (typeof config.askNonInteractive === "string") result.askNonInteractive = parseAskNonInteractive(config.askNonInteractive);
  if (typeof config.mcpProxyEnabled === "boolean") result.mcpProxyEnabled = config.mcpProxyEnabled;
  if (typeof config.mcpProxyTimeoutMs === "number") {
    const value = parsePositiveInteger(String(config.mcpProxyTimeoutMs), 0);
    if (value > 0) result.mcpProxyTimeoutMs = value;
  }
  if (typeof config.piMcpConfigPath === "string" && config.piMcpConfigPath.trim()) {
    result.piMcpConfigPath = config.piMcpConfigPath.trim();
  }
  return result;
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env, configFilePath?: string): HypaPiConfig {
  const resolvedConfigPath = configFilePath ?? resolveConfigFilePath(env);
  const fileConfig = resolvedConfigPath ? loadConfigFile(resolvedConfigPath) : {};
  const mcpProxyFlag = env.HYPA_PI_ENABLE_MCP_PROXY ?? env.HYPA_PI_ENABLE_MCP;
  return {
    mode: env.HYPA_PI_MODE !== undefined ? parseMode(env.HYPA_PI_MODE) : (fileConfig.mode ?? "additive"),
    binary: (env.HYPA_BIN?.trim() || undefined) ?? fileConfig.binary ?? "hypa",
    rewriteTimeoutMs:
      env.HYPA_PI_REWRITE_TIMEOUT_MS !== undefined
        ? parsePositiveInteger(env.HYPA_PI_REWRITE_TIMEOUT_MS, 5000)
        : (fileConfig.rewriteTimeoutMs ?? 5000),
    askNonInteractive:
      env.HYPA_PI_ASK_NON_INTERACTIVE !== undefined
        ? parseAskNonInteractive(env.HYPA_PI_ASK_NON_INTERACTIVE)
        : (fileConfig.askNonInteractive ?? "deny"),
    mcpProxyEnabled: mcpProxyFlag !== undefined ? parseBooleanFlag(mcpProxyFlag) : (fileConfig.mcpProxyEnabled ?? false),
    mcpProxyTimeoutMs:
      env.HYPA_PI_MCP_PROXY_TIMEOUT_MS !== undefined
        ? parsePositiveInteger(env.HYPA_PI_MCP_PROXY_TIMEOUT_MS, 10000)
        : (fileConfig.mcpProxyTimeoutMs ?? 10000),
    piMcpConfigPath: (env.HYPA_PI_MCP_CONFIG?.trim() || undefined) ?? fileConfig.piMcpConfigPath,
  };
}

export function isHypaCommand(command: string): boolean {
  const trimmed = command.trimStart();
  return trimmed === "hypa" || trimmed.startsWith("hypa ");
}

export function parseRewriteJson(stdout: string): RewriteResultV1 {
  const payload = JSON.parse(stdout.trim()) as Partial<RewriteResultV1>;
  if (typeof payload.input !== "string") {
    throw new Error("rewrite result missing string field: input");
  }
  if (typeof payload.outcome !== "string" || !VALID_OUTCOMES.has(payload.outcome)) {
    throw new Error(`rewrite result has unknown outcome: ${String(payload.outcome)}`);
  }
  if (typeof payload.command !== "string") {
    throw new Error("rewrite result missing string field: command");
  }
  return payload as RewriteResultV1;
}

export function mapRewriteResult(result: RewriteResultV1): RewriteStatus {
  switch (result.outcome) {
    case "Rewritten":
    case "GenericWrapper":
      return { kind: "rewritten", outcome: result.outcome, input: result.input, command: result.command };
    case "Passthrough":
      return { kind: "passthrough", outcome: result.outcome, input: result.input, command: result.command };
    case "Deny":
      return {
        kind: "deny",
        input: result.input,
        command: result.command,
        reason: `Command blocked by Hypa policy: ${result.input}`,
      };
    case "Ask":
      return {
        kind: "ask",
        input: result.input,
        command: result.command,
        reason: `Hypa requests confirmation before running: ${result.command || result.input}`,
      };
  }
}

export function formatStatus(status: RewriteStatus | undefined): string {
  if (!status) return "none";
  switch (status.kind) {
    case "rewritten":
      return `${status.outcome}: ${status.input} => ${status.command}`;
    case "passthrough":
      return `Passthrough: ${status.input}`;
    case "deny":
      return `Deny: ${status.reason}`;
    case "ask":
      return `Ask: ${status.reason}`;
    case "skipped":
      return `Skipped: ${status.reason}`;
    case "error":
      return `Error: ${status.error}`;
  }
}
