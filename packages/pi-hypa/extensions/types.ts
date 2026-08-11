export type HypaPiMode = "additive" | "replace";
export type AskNonInteractivePolicy = "deny" | "allow";

export type RewriteOutcome =
  | "Rewritten"
  | "GenericWrapper"
  | "Passthrough"
  | "Deny"
  | "Ask";

export interface RewriteResultV1 {
  schemaVersion?: 1;
  input: string;
  outcome: RewriteOutcome;
  command: string;
}

export type RewriteStatus =
  | { kind: "rewritten"; outcome: "Rewritten" | "GenericWrapper"; input: string; command: string }
  | { kind: "passthrough"; outcome: "Passthrough"; input: string; command: string }
  | { kind: "deny"; input: string; command: string; reason: string }
  | { kind: "ask"; input: string; command: string; reason: string }
  | { kind: "skipped"; input: string; reason: string }
  | { kind: "error"; input: string; error: string };

export interface HypaPiConfig {
  mode: HypaPiMode;
  binary: string;
  rewriteTimeoutMs: number;
  askNonInteractive: AskNonInteractivePolicy;
  mcpProxyEnabled: boolean;
  mcpProxyTimeoutMs: number;
  piMcpConfigPath?: string;
}

/**
 * Emitted on the Node.js `process` EventEmitter as `"pi-hypa:file-read"` each
 * time the `hypa_read` tool is invoked. Consumers such as pi-lens can listen
 * for this event and forward the information to their internal read-guard so
 * that a subsequent `edit` call on the same file is not blocked.
 *
 * @example
 * ```ts
 * process.on("pi-hypa:file-read", (event: HypaFileReadEvent) => {
 *   readGuard.recordRead({ filePath: event.filePath, ... });
 * });
 * ```
 */
export interface HypaFileReadEvent {
  /** Absolute path to the file being read. */
  filePath: string;
  /** First line requested (1-indexed). Defaults to 1 when no offset was given. */
  requestedOffset: number;
  /** Number of lines requested. `undefined` means the whole file was requested. */
  requestedLimit: number | undefined;
  /** Unix timestamp (ms) captured at the moment the tool_call event fired. */
  timestamp: number;
}

export interface HypaDiagnostics {
  mode: HypaPiMode;
  configFilePath?: string;
  binary: string;
  resolvedBinary: string;
  lastRewrite?: RewriteStatus;
}
