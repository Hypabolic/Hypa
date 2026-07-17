import { existsSync } from "node:fs";
import { dirname, join, posix, win32 } from "node:path";
import { platform } from "node:os";
import { createRequire } from "node:module";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import type { HypaPiConfig, RewriteStatus } from "./types.js";
import { isHypaCommand, mapRewriteResult, parseRewriteJson } from "./policy.js";

/**
 * Normalises a resolved binary path + its arguments for the current platform.
 *
 * Node.js cannot `spawn` a `.js` file without an interpreter on Windows (no
 * shebang support). On Unix the shebang is typically `#!/usr/bin/env node`,
 * which fails when only Bun is installed. Always wrap `.js` entrypoints with
 * the current host runtime (`process.execPath`) so Pi under Bun uses bun and
 * Node hosts use node.
 *
 * On Windows, `.cmd` / `.bat` shims also need `cmd /c` to avoid `EFTYPE`.
 */
export function getExecArgs(
  binary: string,
  args: string[],
  platformName: string = platform(),
  jsRuntime: string = process.execPath,
): [string, string[]] {
  const lower = binary.toLowerCase();
  if (lower.endsWith(".js")) return [jsRuntime, [binary, ...args]];
  if (platformName === "win32" && (lower.endsWith(".cmd") || lower.endsWith(".bat"))) {
    return ["cmd", ["/c", binary, ...args]];
  }
  return [binary, args];
}

const require = createRequire(import.meta.url);

/** Platform package keys matching npm/hypa/bin.js PLATFORM_MAP. */
const PLATFORM_MAP: Record<string, Record<string, string>> = {
  linux: { x64: "linux-x64", arm64: "linux-arm64" },
  darwin: { x64: "darwin-x64", arm64: "darwin-arm64" },
  win32: { x64: "win32-x64", arm64: "win32-arm64" },
};

type RequireResolve = (id: string) => string;

function isJsEntry(path: string): boolean {
  return /\.js$/i.test(path);
}

/**
 * Resolve the platform-native hypa binary from optional deps
 * (`@hypabolic/hypa-{linux,darwin,win32}-{x64,arm64}`).
 */
export function resolveNativeHypaBinary(
  exists: (p: string) => boolean = existsSync,
  requireResolve: RequireResolve = require.resolve.bind(require),
  platformName: string = platform(),
  archName: string = process.arch,
): string | undefined {
  const archKey = PLATFORM_MAP[platformName]?.[archName];
  if (!archKey) return undefined;

  const pkgName = `@hypabolic/hypa-${archKey}`;
  try {
    const packageJson = requireResolve(`${pkgName}/package.json`);
    const packageRoot = dirname(packageJson);
    const binaryName = platformName === "win32" ? "hypa.exe" : "hypa";
    const binaryPath = join(packageRoot, "bin", binaryName);
    return exists(binaryPath) ? binaryPath : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Resolve order for bare names like `hypa`:
 * 1. Absolute/relative path in binary name → return as-is (caller intent).
 * 2. On Windows, native bundled binary if present.
 * 3. PATH candidate that is not a JS entry → return it (real native/shell shim).
 * 4. Native bundled binary if present.
 * 5. PATH JS candidate if any.
 * 6. bin.js fallback.
 * 7. bare name.
 */
export function resolveHypaBinary(
  binary: string,
  env: NodeJS.ProcessEnv = process.env,
  platformName: string = platform(),
  exists: (p: string) => boolean = existsSync,
  requireResolve: RequireResolve = require.resolve.bind(require),
): string {
  if (binary.includes("/") || binary.includes("\\")) return binary;

  if (platformName === "win32") {
    const nativeBinary = resolveNativeHypaBinary(exists, requireResolve, platformName);
    if (nativeBinary) return nativeBinary;
  }

  const pathBinary = resolvePathBinary(binary, env, platformName, exists);
  if (pathBinary && !isJsEntry(pathBinary)) return pathBinary;

  const nativeBinary = resolveNativeHypaBinary(exists, requireResolve, platformName);
  if (nativeBinary) return nativeBinary;

  if (pathBinary) return pathBinary;

  const jsBundled = resolveBundledJsHypaBinary(binary, exists, requireResolve);
  if (jsBundled) return jsBundled;

  return binary;
}

function resolvePathBinary(
  binary: string,
  env: NodeJS.ProcessEnv,
  platformName: string,
  exists: (p: string) => boolean,
): string | undefined {
  const path = env.PATH;
  if (!path) return undefined;

  const isWindows = platformName === "win32";
  const pathDelimiter = isWindows ? ";" : ":";
  const resolvePath = isWindows ? win32.resolve : posix.resolve;
  const executableExtensions = isWindows ? getWindowsExecutableExtensions(env) : [];
  const binaryLower = binary.toLowerCase();
  const hasExecutableExtension =
    isWindows && executableExtensions.some((extension) => binaryLower.endsWith(extension.toLowerCase()));

  for (const dir of path.split(pathDelimiter)) {
    if (!dir) continue;
    const candidate = resolvePath(dir, binary);

    if (!isWindows) {
      if (exists(candidate)) return candidate;
      continue;
    }

    if (hasExecutableExtension) {
      if (exists(candidate)) return candidate;
      continue;
    }

    for (const extension of executableExtensions) {
      const executableCandidate = `${candidate}${extension}`;
      if (exists(executableCandidate)) return executableCandidate;
    }
  }

  return undefined;
}

function getWindowsExecutableExtensions(env: NodeJS.ProcessEnv): string[] {
  const rawExtensions = env.PATHEXT?.trim() ? env.PATHEXT : ".COM;.EXE;.BAT;.CMD";
  const extensions: string[] = [];
  const seen = new Set<string>();

  for (const extension of rawExtensions.split(";")) {
    const trimmed = extension.trim();
    if (!trimmed) continue;
    const normalized = trimmed.startsWith(".") ? trimmed : `.${trimmed}`;
    const key = normalized.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    extensions.push(normalized);
  }

  for (const extension of [".exe", ".cmd"]) {
    const key = extension.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    extensions.push(extension);
  }

  return extensions;
}

/** Prefer native optional-dep binary; fall back to @hypabolic/hypa/bin.js. */
export function resolveBundledHypaBinary(
  binary: string,
  exists: (p: string) => boolean = existsSync,
  requireResolve: RequireResolve = require.resolve.bind(require),
  platformName: string = platform(),
): string | undefined {
  if (binary !== "hypa") return undefined;

  const native = resolveNativeHypaBinary(exists, requireResolve, platformName);
  if (native) return native;

  return resolveBundledJsHypaBinary(binary, exists, requireResolve);
}

function resolveBundledJsHypaBinary(
  binary: string,
  exists: (p: string) => boolean,
  requireResolve: RequireResolve,
): string | undefined {
  if (binary !== "hypa") return undefined;

  try {
    const packageJson = requireResolve("@hypabolic/hypa/package.json");
    const packageRoot = dirname(packageJson);
    const bin = join(packageRoot, "bin.js");
    return exists(bin) ? bin : undefined;
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
