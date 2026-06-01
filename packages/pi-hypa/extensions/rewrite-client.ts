import { existsSync } from "node:fs";
import { delimiter, resolve } from "node:path";
import { platform } from "node:os";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import type { HypaPiConfig, RewriteStatus } from "./types.js";
import { isHypaCommand, mapRewriteResult, parseRewriteJson } from "./policy.js";

export function resolveHypaBinary(binary: string, env: NodeJS.ProcessEnv = process.env): string {
  if (binary.includes("/") || binary.includes("\\")) return binary;

  const path = env.PATH;
  if (!path) return binary;

  const isWindows = platform() === "win32";
  for (const dir of path.split(delimiter)) {
    if (!dir) continue;
    const candidate = resolve(dir, binary);
    if (existsSync(candidate)) return candidate;
    if (isWindows && existsSync(`${candidate}.exe`)) return `${candidate}.exe`;
  }

  return binary;
}

export async function rewriteCommand(
  pi: ExtensionAPI,
  config: HypaPiConfig,
  command: string,
  signal?: AbortSignal,
): Promise<RewriteStatus> {
  if (isHypaCommand(command)) {
    return { kind: "skipped", input: command, reason: "command already starts with hypa" };
  }

  try {
    const result = await pi.exec(config.binary, ["rewrite", "--json", command], {
      signal,
      timeout: config.rewriteTimeoutMs,
    });

    if (result.killed) {
      return { kind: "error", input: command, error: `hypa rewrite timed out after ${config.rewriteTimeoutMs}ms` };
    }

    if (!result.stdout?.trim()) {
      const detail = result.stderr?.trim() || `exit code ${result.code}`;
      return { kind: "error", input: command, error: `hypa rewrite produced no JSON (${detail})` };
    }

    return mapRewriteResult(parseRewriteJson(result.stdout));
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return { kind: "error", input: command, error: message };
  }
}
