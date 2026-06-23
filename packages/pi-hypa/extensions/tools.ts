import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Text } from "@earendil-works/pi-tui";
import type { HypaPiConfig } from "./types.js";

const DEFAULT_MAX_BYTES = 50 * 1024;
const DEFAULT_MAX_LINES = 2000;

type PiToolParams = Record<string, any>;
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

interface TruncationResult {
  content: string;
  truncated: boolean;
  totalLines: number;
  outputLines: number;
  totalBytes: number;
  outputBytes: number;
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes}B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)}KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)}MB`;
}

function byteLength(text: string): number {
  return Buffer.byteLength(text, "utf8");
}

function truncateText(text: string, preferTail: boolean): TruncationResult {
  const lines = text.split("\n");
  const selected = preferTail ? lines.slice(-DEFAULT_MAX_LINES) : lines.slice(0, DEFAULT_MAX_LINES);
  let content = selected.join("\n");

  while (byteLength(content) > DEFAULT_MAX_BYTES && content.length > 0) {
    content = preferTail ? content.slice(Math.ceil(content.length * 0.1)) : content.slice(0, Math.floor(content.length * 0.9));
  }

  return {
    content,
    truncated: selected.length < lines.length || byteLength(text) > DEFAULT_MAX_BYTES,
    totalLines: lines.length,
    outputLines: content.length === 0 ? 0 : content.split("\n").length,
    totalBytes: byteLength(text),
    outputBytes: byteLength(content),
  };
}

const textParameter = (description: string) => ({ type: "string", description } as const);
const numberParameter = (description: string) => ({ type: "number", description } as const);
const booleanParameter = (description: string) => ({ type: "boolean", description } as const);

const shellSchema = {
  type: "object",
  properties: {
    command: textParameter("Shell command to execute through Hypa compression"),
    timeoutMs: numberParameter("Timeout in milliseconds (default: Hypa CLI default)"),
    raw: booleanParameter("Run with hypa raw instead of compressed hypa -c"),
  },
  required: ["command"],
  additionalProperties: false,
} as const;

const readSchema = {
  type: "object",
  properties: {
    path: textParameter("Path to read, relative to Pi cwd or absolute"),
    offset: numberParameter("Line number to start reading from (1-indexed)"),
    limit: numberParameter("Maximum number of lines to read"),
    maxTokens: numberParameter("Approximate maximum tokens to return after Hypa compression"),
  },
  required: ["path"],
  additionalProperties: false,
} as const;

const grepSchema = {
  type: "object",
  properties: {
    pattern: textParameter("Search pattern"),
    path: textParameter("Directory or file to search (default: current directory)"),
    glob: textParameter("File glob filter, e.g. *.ts"),
    ignoreCase: booleanParameter("Case-insensitive search"),
    literal: booleanParameter("Treat pattern as a literal string"),
    context: numberParameter("Lines of context around each match"),
    limit: numberParameter("Maximum matches"),
    timeoutMs: numberParameter("Timeout in milliseconds (default: Hypa CLI default)"),
  },
  required: ["pattern"],
  additionalProperties: false,
} as const;

const findSchema = {
  type: "object",
  properties: {
    pattern: textParameter("File name/glob pattern (default: *)"),
    path: textParameter("Directory to search (default: current directory)"),
    limit: numberParameter("Maximum paths to return"),
    timeoutMs: numberParameter("Timeout in milliseconds (default: Hypa CLI default)"),
  },
  additionalProperties: false,
} as const;

const lsSchema = {
  type: "object",
  properties: {
    path: textParameter("Directory to list (default: current directory)"),
    all: booleanParameter("Include dotfiles"),
    long: booleanParameter("Use long listing"),
    timeoutMs: numberParameter("Timeout in milliseconds (default: Hypa CLI default)"),
  },
  additionalProperties: false,
} as const;

interface HypaExecResult {
  stdout: string;
  stderr: string;
  code: number;
  killed?: boolean;
}

interface ToolTextDetails {
  source: "hypa-cli";
  command: string;
  exitCode: number;
  truncation?: unknown;
  fullOutputPath?: string;
}

export function shellQuote(value: string): string {
  if (value.length === 0) return "''";
  if (/^[A-Za-z0-9_./:=@,+%^-]+$/.test(value)) return value;
  return `'${value.replace(/'/g, `'"'"'`)}'`;
}

function normalizePathArg(path: string): string {
  return path.startsWith("@") ? path.slice(1) : path;
}

export function buildReadCommand(path: string, offset?: number, limit?: number): string {
  const quotedPath = shellQuote(normalizePathArg(path));
  if (offset !== undefined || limit !== undefined) {
    const start = Math.max(1, Math.floor(offset ?? 1));
    const end = limit !== undefined ? start + Math.max(1, Math.floor(limit)) - 1 : "$";
    return `sed -n ${shellQuote(`${start},${end}p`)} -- ${quotedPath}`;
  }
  return `cat -- ${quotedPath}`;
}

export function buildGrepCommand(params: {
  pattern: string;
  path?: string;
  glob?: string;
  ignoreCase?: boolean;
  literal?: boolean;
  context?: number;
  limit?: number;
}): string {
  const args = ["rg", "--line-number", "--color=never"];
  if (params.ignoreCase) args.push("--ignore-case");
  if (params.literal) args.push("--fixed-strings");
  if (params.context !== undefined) args.push("--context", String(Math.max(0, Math.floor(params.context))));
  if (params.limit !== undefined) args.push("--max-count", String(Math.max(1, Math.floor(params.limit))));
  if (params.glob) args.push("--glob", params.glob);
  args.push(params.pattern, normalizePathArg(params.path ?? "."));
  return args.map(shellQuote).join(" ");
}

export function buildFindCommand(params: { pattern?: string; path?: string; limit?: number }): string {
  const path = shellQuote(normalizePathArg(params.path ?? "."));
  const pattern = shellQuote(params.pattern ?? "*");
  const base = `find ${path} -type f -name ${pattern}`;
  if (params.limit === undefined) return base;
  return `${base} | head -n ${shellQuote(String(Math.max(1, Math.floor(params.limit))))}`;
}

export function buildLsCommand(params: { path?: string; all?: boolean; long?: boolean }): string {
  const flags = `${params.long === false ? "" : "l"}${params.all ? "a" : ""}`;
  return ["ls", flags ? `-${flags}` : undefined, "--", normalizePathArg(params.path ?? ".")]
    .filter((value): value is string => typeof value === "string" && value.length > 0)
    .map(shellQuote)
    .join(" ");
}

async function runHypaCommand(
  pi: PiApi,
  config: HypaPiConfig,
  command: string,
  timeoutMs: number | undefined,
  raw: boolean | undefined,
  signal?: AbortSignal,
): Promise<HypaExecResult> {
  const args: string[] = [];
  if (timeoutMs !== undefined) args.push("--timeout-ms", String(Math.max(1, Math.floor(timeoutMs))));
  if (raw) {
    args.push("raw", ...splitRawCommand(command));
  } else {
    args.push("-c", command);
  }
  return pi.exec(config.binary, args, { signal, timeout: timeoutMs });
}

function splitRawCommand(command: string): string[] {
  // Raw mode is intentionally conservative: pass through simple whitespace-tokenized commands only.
  // Complex shell syntax should use compressed mode, where Hypa owns shell parsing.
  return command.trim().split(/\s+/).filter(Boolean);
}

function hasOwn(obj: unknown, key: string): boolean {
  return !!obj && typeof obj === "object" && Object.prototype.hasOwnProperty.call(obj, key);
}

function pushParam(parts: string[], args: Record<string, unknown>, key: string) {
  if (!hasOwn(args, key)) return;
  const value = args[key];
  if (value === true) {
    parts.push(key);
  } else if (value === false) {
    parts.push(`${key}=false`);
  } else if (typeof value === "string" && value.length > 0) {
    parts.push(`${key}=${value}`);
  } else if (typeof value === "number") {
    parts.push(`${key}=${value}`);
  }
}

function renderCallLine(title: string, main: string[], extras: string[], theme: any) {
  const body = main.filter((part) => part.length > 0).join(" ");
  const meta = extras.length > 0 ? ` ${theme.fg("muted", `(${extras.join(", ")})`)}` : "";
  return new Text(`${theme.fg("toolTitle", theme.bold(title))}${body}${meta}`, 0, 0);
}

function renderHypaShellCall(args: Record<string, unknown>, theme: any) {
  const main = [typeof args.command === "string" ? args.command : "..."];
  const extras: string[] = [];
  pushParam(extras, args, "raw");
  pushParam(extras, args, "timeoutMs");
  return renderCallLine("hypa_shell $ ", main, extras, theme);
}

function renderHypaReadCall(args: Record<string, unknown>, theme: any) {
  const main = [typeof args.path === "string" ? args.path : "..."];
  const extras: string[] = [];
  pushParam(extras, args, "offset");
  pushParam(extras, args, "limit");
  pushParam(extras, args, "maxTokens");
  return renderCallLine("hypa_read ", main, extras, theme);
}

function renderHypaGrepCall(args: Record<string, unknown>, theme: any) {
  const main = [typeof args.pattern === "string" ? args.pattern : "...", typeof args.path === "string" ? args.path : ""];
  const extras: string[] = [];
  pushParam(extras, args, "glob");
  pushParam(extras, args, "ignoreCase");
  pushParam(extras, args, "literal");
  pushParam(extras, args, "context");
  pushParam(extras, args, "limit");
  pushParam(extras, args, "timeoutMs");
  return renderCallLine("hypa_grep ", main, extras, theme);
}

function renderHypaFindCall(args: Record<string, unknown>, theme: any) {
  const main = [typeof args.pattern === "string" ? args.pattern : "", typeof args.path === "string" ? args.path : ""];
  const extras: string[] = [];
  pushParam(extras, args, "limit");
  pushParam(extras, args, "timeoutMs");
  return renderCallLine("hypa_find ", main, extras, theme);
}

function renderHypaLsCall(args: Record<string, unknown>, theme: any) {
  const main = [typeof args.path === "string" ? args.path : ""];
  const extras: string[] = [];
  pushParam(extras, args, "all");
  pushParam(extras, args, "long");
  pushParam(extras, args, "timeoutMs");
  return renderCallLine("hypa_ls ", main, extras, theme);
}

function previewResultText(result: any, options: { expanded?: boolean; isPartial?: boolean }, theme: any, pendingText: string) {
  if (options?.isPartial) {
    return new Text(theme.fg("muted", pendingText), 0, 0);
  }

  const output = Array.isArray(result?.content)
    ? result.content.filter((part: any) => part?.type === "text").map((part: any) => part.text).join("\n")
    : "";

  if (!output) {
    return new Text(theme.fg("muted", "(no output)"), 0, 0);
  }

  const styleOutput = (text: string) => text.split("\n").map((line: string) => theme.fg("toolOutput", line)).join("\n");

  if (options?.expanded) {
    return new Text(styleOutput(output), 0, 0);
  }

  const lines = output.split("\n");
  if (lines.length <= 12) {
    return new Text(styleOutput(output), 0, 0);
  }

  const preview = styleOutput(lines.slice(0, 12).join("\n"));
  const hint = `\n${theme.fg("muted", `... (${lines.length - 12} more lines, Ctrl+O to expand)`)}`;
  return new Text(`${preview}${hint}`, 0, 0);
}

async function toToolText(result: HypaExecResult, command: string, preferTail = false) {
  const combined = [result.stdout, result.stderr].filter((part) => part?.length > 0).join(result.stdout && result.stderr ? "\n" : "");
  const truncation = preferTail
    ? truncateText(combined, true)
    : truncateText(combined, false);

  let text = truncation.content;
  const details: ToolTextDetails = {
    source: "hypa-cli",
    command,
    exitCode: result.code,
  };

  if (truncation.truncated) {
    const tempDir = await mkdtemp(join(tmpdir(), "pi-hypa-"));
    const tempFile = join(tempDir, "output.txt");
    await writeFile(tempFile, combined, "utf8");
    details.truncation = truncation;
    details.fullOutputPath = tempFile;
    text += `\n\n[Output truncated: showing ${truncation.outputLines} of ${truncation.totalLines} lines (${formatSize(truncation.outputBytes)} of ${formatSize(truncation.totalBytes)}). Full output saved to: ${tempFile}]`;
  }

  if (result.killed) {
    text += `\n\n[Hypa command timed out or was killed]`;
  }

  return {
    content: [{ type: "text" as const, text: text || `(exit ${result.code}, no output)` }],
    details,
  };
}

export function registerHypaTools(pi: PiApi, config: HypaPiConfig) {
  pi.registerTool({
    name: "hypa_shell",
    label: "hypa_shell",
    description: `Run shell commands through Hypa compression. Output is truncated to ${DEFAULT_MAX_LINES} lines or ${formatSize(DEFAULT_MAX_BYTES)} with full output saved when needed.`,
    promptSnippet: "Run shell commands through Hypa compression",
    promptGuidelines: [
      "Use hypa_shell for shell commands when compressed output is preferred.",
      "Do not use hypa_shell to read files; use hypa_read instead.",
    ],
    parameters: shellSchema,
    async execute(_toolCallId, params, signal, _onUpdate, _ctx) {
      const result = await runHypaCommand(pi, config, params.command, params.timeoutMs, params.raw, signal);
      return toToolText(result, params.command, true);
    },
    renderCall(args: any, theme: any) {
      return renderHypaShellCall(args ?? {}, theme);
    },
    renderResult(result: any, options: any, theme: any) {
      return previewResultText(result, options ?? {}, theme, "Running Hypa shell command...");
    },
  });

  pi.registerTool({
    name: "hypa_read",
    label: "hypa_read",
    description: `Read a file through Hypa compression. Supports offset/limit line slices. Output is truncated to ${DEFAULT_MAX_LINES} lines or ${formatSize(DEFAULT_MAX_BYTES)} with full output saved when needed.`,
    promptSnippet: "Read file contents through Hypa compression",
    promptGuidelines: ["Use hypa_read to inspect file contents instead of cat/head/tail via shell."],
    parameters: readSchema,
    async execute(_toolCallId, params, signal, _onUpdate, _ctx) {
      const command = buildReadCommand(params.path, params.offset, params.limit);
      const timeoutMs = params.maxTokens ? undefined : undefined;
      const result = await runHypaCommand(pi, config, command, timeoutMs, false, signal);
      return toToolText(result, command);
    },
    renderCall(args: any, theme: any) {
      return renderHypaReadCall(args ?? {}, theme);
    },
    renderResult(result: any, options: any, theme: any) {
      return previewResultText(result, options ?? {}, theme, "Reading file through Hypa...");
    },
  });

  pi.registerTool({
    name: "hypa_grep",
    label: "hypa_grep",
    description: `Search file contents with ripgrep through Hypa compression. Output is truncated to ${DEFAULT_MAX_LINES} lines or ${formatSize(DEFAULT_MAX_BYTES)} with full output saved when needed.`,
    promptSnippet: "Search file contents through Hypa compression",
    parameters: grepSchema,
    async execute(_toolCallId, params, signal, _onUpdate, _ctx) {
      const command = buildGrepCommand(params as { pattern: string; path?: string; glob?: string; ignoreCase?: boolean; literal?: boolean; context?: number; limit?: number });
      const result = await runHypaCommand(pi, config, command, params.timeoutMs, false, signal);
      return toToolText(result, command);
    },
    renderCall(args: any, theme: any) {
      return renderHypaGrepCall(args ?? {}, theme);
    },
    renderResult(result: any, options: any, theme: any) {
      return previewResultText(result, options ?? {}, theme, "Searching through Hypa...");
    },
  });

  pi.registerTool({
    name: "hypa_find",
    label: "hypa_find",
    description: `Find files through Hypa compression. Output is truncated to ${DEFAULT_MAX_LINES} lines or ${formatSize(DEFAULT_MAX_BYTES)} with full output saved when needed.`,
    promptSnippet: "Find files through Hypa compression",
    parameters: findSchema,
    async execute(_toolCallId, params, signal, _onUpdate, _ctx) {
      const command = buildFindCommand(params);
      const result = await runHypaCommand(pi, config, command, params.timeoutMs, false, signal);
      return toToolText(result, command);
    },
    renderCall(args: any, theme: any) {
      return renderHypaFindCall(args ?? {}, theme);
    },
    renderResult(result: any, options: any, theme: any) {
      return previewResultText(result, options ?? {}, theme, "Finding files through Hypa...");
    },
  });

  pi.registerTool({
    name: "hypa_ls",
    label: "hypa_ls",
    description: `List directory contents through Hypa compression. Output is truncated to ${DEFAULT_MAX_LINES} lines or ${formatSize(DEFAULT_MAX_BYTES)} with full output saved when needed.`,
    promptSnippet: "List directory contents through Hypa compression",
    parameters: lsSchema,
    async execute(_toolCallId, params, signal, _onUpdate, _ctx) {
      const command = buildLsCommand(params);
      const result = await runHypaCommand(pi, config, command, params.timeoutMs, false, signal);
      return toToolText(result, command);
    },
    renderCall(args: any, theme: any) {
      return renderHypaLsCall(args ?? {}, theme);
    },
    renderResult(result: any, options: any, theme: any) {
      return previewResultText(result, options ?? {}, theme, "Listing directory through Hypa...");
    },
  });
}
