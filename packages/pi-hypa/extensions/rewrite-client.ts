import { existsSync } from "node:fs";
import { delimiter, dirname, join, resolve } from "node:path";
import { platform } from "node:os";
import { createRequire } from "node:module";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import type { HypaPiConfig, RewriteStatus } from "./types.js";
import { isHypaCommand, mapRewriteResult, parseRewriteJson } from "./policy.js";

/**
 * Normalises a resolved binary path + its arguments for the current platform.
 *
 * On Windows, Node.js cannot `spawn` a `.js` file directly (no shebang support)
 * or a `.cmd` shell script without a shell — both produce `EFTYPE`. Wrap them
 * with the appropriate interpreter so `pi.exec` always receives a native binary.
 */
export function getExecArgs(
  binary: string,
  args: string[],
  platformName: string = platform(),
): [string, string[]] {
  if (platformName !== "win32") return [binary, args];
  const lower = binary.toLowerCase();
  if (lower.endsWith(".js")) return ["node", [binary, ...args]];
  if (lower.endsWith(".cmd") || lower.endsWith(".bat")) return ["cmd", ["/c", binary, ...args]];
  return [binary, args];
}

const require = createRequire(import.meta.url);

export function resolveHypaBinary(binary: string, env: NodeJS.ProcessEnv = process.env): string {
  if (binary.includes("/") || binary.includes("\\")) return binary;

  const pathBinary = resolvePathBinary(binary, env);
  if (pathBinary) return pathBinary;

  const bundledBinary = resolveBundledHypaBinary(binary);
  if (bundledBinary) return bundledBinary;

  return binary;
}

function resolvePathBinary(binary: string, env: NodeJS.ProcessEnv): string | undefined {
  const path = env.PATH;
  if (!path) return undefined;

  const isWindows = platform() === "win32";
  for (const dir of path.split(delimiter)) {
    if (!dir) continue;
    const candidate = resolve(dir, binary);
    if (existsSync(candidate)) return candidate;
    if (isWindows && existsSync(`${candidate}.exe`)) return `${candidate}.exe`;
    if (isWindows && existsSync(`${candidate}.cmd`)) return `${candidate}.cmd`;
  }

  return undefined;
}

function resolveBundledHypaBinary(binary: string): string | undefined {
  if (binary !== "hypa") return undefined;

  try {
    const packageJson = require.resolve("@hypabolic/hypa/package.json");
    const packageRoot = dirname(packageJson);
    const bin = join(packageRoot, "bin.js");
    return existsSync(bin) ? bin : undefined;
  } catch {
    return undefined;
  }
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
    const [execBin, execArgs] = getExecArgs(config.binary, ["rewrite", "--json", command]);
    const result = await pi.exec(execBin, execArgs, {
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
