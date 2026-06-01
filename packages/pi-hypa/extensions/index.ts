import { isToolCallEventType, type ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { formatStatus, loadConfig } from "./policy.js";
import { resolveHypaBinary, rewriteCommand } from "./rewrite-client.js";
import { registerHypaMcpProxyBridge } from "./mcp-proxy-bridge.js";
import { registerHypaTools } from "./tools.js";
import type { HypaDiagnostics, RewriteStatus } from "./types.js";

const REPLACE_MODE_DISABLED_BUILTINS = new Set(["bash", "read", "grep", "find", "ls"]);

type HypaExtensionAPI = ExtensionAPI & {
  registerTool(definition: Record<string, unknown>): void;
  getActiveTools(): string[];
  setActiveTools(names: string[]): void;
};

export default function (pi: ExtensionAPI) {
  const hypaPi = pi as HypaExtensionAPI;
  const config = loadConfig();
  const diagnostics: HypaDiagnostics = {
    mode: config.mode,
    binary: config.binary,
    resolvedBinary: resolveHypaBinary(config.binary),
  };

  function record(status: RewriteStatus) {
    diagnostics.lastRewrite = status;
  }

  registerHypaTools(hypaPi, config);
  registerHypaMcpProxyBridge(hypaPi, config);

  if (config.mode === "replace") {
    pi.on("session_start", () => {
      const active = hypaPi.getActiveTools().filter((name: string) => !REPLACE_MODE_DISABLED_BUILTINS.has(name));
      hypaPi.setActiveTools(active);
    });
  }

  pi.on("tool_call", async (event, ctx) => {
    if (!isToolCallEventType("bash", event)) return;

    const original = event.input.command;
    const status = await rewriteCommand(pi, config, original, ctx.signal);
    record(status);

    switch (status.kind) {
      case "rewritten":
        event.input.command = status.command;
        return;
      case "passthrough":
      case "skipped":
      case "error":
        return;
      case "deny":
        return { block: true, reason: status.reason };
      case "ask": {
        if (ctx.hasUI) {
          const ok = await ctx.ui.confirm("Hypa confirmation", status.reason);
          if (!ok) return { block: true, reason: "Blocked by user after Hypa confirmation request." };
          event.input.command = status.command;
          return;
        }

        if (config.askNonInteractive === "allow") {
          event.input.command = status.command;
          return;
        }

        return {
          block: true,
          reason: `${status.reason} Non-interactive fallback is deny (set HYPA_PI_ASK_NON_INTERACTIVE=allow to allow).`,
        };
      }
    }
  });

  pi.registerCommand("hypa", {
    description: "Show Hypa Pi extension diagnostics",
    handler: async (_args, ctx) => {
      diagnostics.resolvedBinary = resolveHypaBinary(config.binary);
      const lines = [
        "Hypa Pi extension",
        `Mode: ${diagnostics.mode}`,
        `Binary: ${diagnostics.binary}`,
        `Resolved binary: ${diagnostics.resolvedBinary}`,
        `Rewrite timeout: ${config.rewriteTimeoutMs}ms`,
        `Ask fallback (non-UI): ${config.askNonInteractive}`,
        `MCP proxy discovery: ${config.mcpProxyEnabled ? "enabled" : "disabled"}`,
        `MCP proxy timeout: ${config.mcpProxyTimeoutMs}ms`,
        `Pi MCP config for dedup: ${config.piMcpConfigPath ?? "default"}`,
        `Active Hypa tools: ${hypaPi.getActiveTools().filter((name: string) => name.startsWith("hypa_")).join(", ") || "none"}`,
        `Last rewrite: ${formatStatus(diagnostics.lastRewrite)}`,
      ];
      ctx.ui.notify(lines.join("\n"), diagnostics.lastRewrite?.kind === "error" ? "warning" : "info");
    },
  });
}
