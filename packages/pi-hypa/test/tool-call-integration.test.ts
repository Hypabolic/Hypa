import test from "node:test";
import assert from "node:assert/strict";
import { readFile, access } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import register from "../extensions/index.js";
import { getExecArgs } from "../extensions/rewrite-client.js";

const originalOutput = "original\n".repeat(300);
type Compression = "ok" | "fail" | "throw" | "killed" | "empty" | "unchanged" | "larger";

function harness(outcome = "Rewritten", compression: Compression = "ok", env: NodeJS.ProcessEnv = {}) {
  const handlers = new Map<string, (...args: any[]) => any>();
  const files: string[] = [];
  const calls: { binary: string; args: string[]; options: any }[] = [];
  const pi = {
    on(name: string, handler: (...args: any[]) => any) { handlers.set(name, handler); },
    registerTool() {}, registerCommand() {}, getActiveTools() { return ["bash"]; }, setActiveTools() {},
    async exec(binary: string, args: string[], options: any) {
      calls.push({ binary, args, options });
      const cliArgs = binary === process.execPath ? args.slice(1) : args;
      if (cliArgs[0] === "rewrite") {
        if (outcome === "error") throw new Error("offline");
        return { code: 0, stdout: JSON.stringify({ input: cliArgs[2], outcome, command: "hypa -c BROKEN" }) };
      }
      assert.deepEqual(cliArgs.slice(0, 4), ["compress", "--kind", "shell-output", "--file"]);
      const file = cliArgs.at(-1)!;
      files.push(file);
      assert.equal(await readFile(file, "utf8"), originalOutput);
      if (compression === "throw") throw new Error("offline");
      const stdout = compression === "empty" ? " \n" : compression === "unchanged" ? originalOutput
        : compression === "larger" ? originalOutput + "extra" : "summary\n";
      return { code: compression === "fail" ? 1 : 0, stdout, killed: compression === "killed" };
    },
  };
  const overrides = {
    HYPA_PI_CONFIG: "none", HYPA_PI_MODE: "additive", HYPA_PI_ENABLE_MCP_PROXY: "0",
    HYPA_PI_ASK_NON_INTERACTIVE: "deny", HYPA_PI_REWRITE_TIMEOUT_MS: "5000",
    HYPA_BIN: "/test tools/hypa", ...env,
  };
  const previous = { ...process.env };
  Object.assign(process.env, overrides);
  try { register(pi as unknown as ExtensionAPI); } finally {
    for (const key of Object.keys(overrides)) {
      if (previous[key] === undefined) delete process.env[key];
      else process.env[key] = previous[key];
    }
  }
  return { handlers, files, calls };
}

function toolCall(command = "printf test", timeout?: number) {
  return { type: "tool_call", toolName: "bash", toolCallId: "a", input: { command, timeout } };
}

function toolResult(overrides: Record<string, unknown> = {}) {
  return { toolName: "bash", toolCallId: "a", content: [{ type: "text", text: originalOutput }], isError: false, ...overrides };
}

const scripts = [
  'printf "FIRST\\n"\nprintf "SECOND\\n"',
  "cat <<'END'\nhello $USER\nEND",
  "value=world\nprintf '%s\\n' \"hello $value\"",
  "items=(one two)\nprintf '%s\\n' \"${items[@]}\"",
  "printf '%s\\n' joined\" words\" escaped\\ space",
  "printf '%s' before; exit 7",
];
for (const command of scripts) {
  test(`native Bash script survives hook: ${JSON.stringify(command)}`, async () => {
    const { handlers } = harness();
    const event = toolCall(command, 600);
    await handlers.get("tool_call")!(event, { hasUI: false });
    assert.deepEqual(event.input, { command, timeout: 600 });
    const run = (script: string) => {
      try { return { stdout: execFileSync("bash", ["-c", script], { encoding: "utf8" }), status: 0 }; }
      catch (error: any) {
        if (typeof error.status !== "number") throw error;
        return { stdout: error.stdout, status: error.status };
      }
    };
    assert.deepEqual(run(event.input.command), run(command));
  });
}

for (const mode of ["ok", "fail", "throw", "killed", "empty", "unchanged", "larger"] as const) {
  test(`output compression ${mode} preserves the result on failure and cleans temporary data`, async () => {
    const { handlers, files } = harness("Rewritten", mode);
    await handlers.get("tool_call")!(toolCall(), {});
    const event = toolResult({ details: { native: "preserved" } });
    const before = structuredClone(event);
    const result = await handlers.get("tool_result")!(event);
    assert.deepEqual(result, mode === "ok" ? { content: [{ type: "text", text: "summary\n" }] } : undefined);
    assert.deepEqual(event, before);
    assert.equal(files.length, 1);
    await assert.rejects(access(files[0]));
    assert.equal(await handlers.get("tool_result")!(event), undefined);
  });
}

for (const scenario of [
  { outcome: "Deny", hasUI: false, confirm: false, fallback: "allow", blocked: true },
  { outcome: "Ask", hasUI: false, confirm: false, fallback: "deny", blocked: true },
  { outcome: "Ask", hasUI: false, confirm: false, fallback: "allow", blocked: false },
  { outcome: "Ask", hasUI: true, confirm: false, fallback: "allow", blocked: true },
  { outcome: "Ask", hasUI: true, confirm: true, fallback: "deny", blocked: false },
]) {
  test(`policy decision: ${JSON.stringify(scenario)}`, async () => {
    const { handlers, files } = harness(scenario.outcome, "ok", { HYPA_PI_ASK_NON_INTERACTIVE: scenario.fallback });
    const event = toolCall(scripts[0], 35);
    const result = await handlers.get("tool_call")!(event, {
      hasUI: scenario.hasUI, ui: { confirm: async () => scenario.confirm },
    });
    assert.equal(result?.block === true, scenario.blocked);
    assert.deepEqual(event.input, { command: scripts[0], timeout: 35 });
    const output = await handlers.get("tool_result")!(toolResult());
    assert.equal(output !== undefined, !scenario.blocked);
    assert.equal(files.length, scenario.blocked ? 0 : 1);
  });
}

for (const outcome of ["Rewritten", "GenericWrapper"]) {
  for (const binary of [String.raw`C:\Program Files\Hypa\hypa.exe`, "/opt/homebrew/bin/hypa", "/test tools/bin.js"]) {
    test(`${outcome} preserves native timeout and invokes resolved binary ${binary}`, async () => {
      const { handlers, calls } = harness(outcome, "ok", { HYPA_BIN: binary });
      const event = toolCall("sleep 31", 35);
      const signal = new AbortController().signal;
      assert.equal(await handlers.get("tool_call")!(event, { signal }), undefined);
      assert.deepEqual(event.input, { command: "sleep 31", timeout: 35 });
      assert.equal(calls[0].options.signal, signal);
      assert.ok(await handlers.get("tool_result")!(toolResult()));
      assert.equal(calls.length, 2);
      for (const call of calls) {
        const args = binary.endsWith(".js") ? call.args.slice(1) : call.args;
        assert.deepEqual([call.binary, call.args], getExecArgs(binary, args));
        assert.equal(call.options.timeout, 5000);
        assert.equal(call.args.includes("--timeout-ms"), false);
      }
    });
  }
}

for (const timeout of [200, 10000]) {
  test(`compression timeout is capped independently of native Bash: ${timeout}`, async () => {
    const { handlers, calls } = harness("Rewritten", "ok", { HYPA_PI_REWRITE_TIMEOUT_MS: String(timeout) });
    await handlers.get("tool_call")!(toolCall("echo test", 600), {});
    await handlers.get("tool_result")!(toolResult());
    assert.equal(calls[0].options.timeout, timeout);
    assert.equal(calls[1].options.timeout, Math.min(timeout, 5000));
  });
}

for (const outcome of ["Passthrough", "error", "skipped"]) {
  test(`${outcome} stays native without compression`, async () => {
    const { handlers, files } = harness(outcome);
    const event = toolCall(outcome === "skipped" ? "hypa git status" : "echo test", 35);
    const before = structuredClone(event);
    assert.equal(await handlers.get("tool_call")!(event, {}), undefined);
    assert.deepEqual(event, before);
    assert.equal(await handlers.get("tool_result")!(toolResult()), undefined);
    assert.equal(files.length, 0);
  });
}

for (const overrides of [
  { isError: true }, { details: { truncation: {} } },
  { content: [{ type: "text", text: "short" }] }, { content: [] },
  { content: [{ type: "image", data: "data", mimeType: "image/png" }] },
  { content: [{ type: "text", text: originalOutput }, { type: "text", text: "extra" }] },
  { toolCallId: "unmatched" }, { toolName: "read" },
]) {
  test(`ineligible result is untouched: ${JSON.stringify(overrides).slice(0, 90)}`, async () => {
    const { handlers, files } = harness();
    await handlers.get("tool_call")!(toolCall(), {});
    assert.equal(await handlers.get("tool_result")!(toolResult(overrides)), undefined);
    assert.equal(files.length, 0);
  });
}

for (const lifecycle of ["agent_end", "session_shutdown"]) {
  test(`${lifecycle} clears pending compression`, async () => {
    const { handlers, files } = harness();
    await handlers.get("tool_call")!(toolCall(), {});
    await handlers.get(lifecycle)!();
    assert.equal(await handlers.get("tool_result")!(toolResult()), undefined);
    assert.equal(files.length, 0);
  });
}
