import test from "node:test";
import assert from "node:assert/strict";
import { injectExecutionTimeout } from "../extensions/execution-timeout.js";
import { mapRewriteResult } from "../extensions/policy.js";
import { qualifyRewrittenHypaCommand } from "../extensions/rewrite-client.js";

function applyStatus(
  command: string,
  status: ReturnType<typeof mapRewriteResult>,
  hasUI: boolean,
  askFallback: "deny" | "allow",
  extras?: { timeout?: unknown; resolvedBinary?: string },
) {
  const timeout = extras?.timeout;
  const resolvedBinary = extras?.resolvedBinary ?? "hypa";
  const event = { input: { command, timeout } };
  switch (status.kind) {
    case "rewritten":
      event.input.command = qualifyRewrittenHypaCommand(
        injectExecutionTimeout(status.command, timeout),
        resolvedBinary,
      );
      return { event };
    case "passthrough":
      return { event };
    case "deny":
      return { event, block: true };
    case "ask":
      if (hasUI || askFallback === "allow") {
        event.input.command = qualifyRewrittenHypaCommand(
          injectExecutionTimeout(status.command, timeout),
          resolvedBinary,
        );
        return { event };
      }
      return { event, block: true };
  }
  throw new Error(`Unhandled status: ${JSON.stringify(status)}`);
}

test("simulated tool call mutates rewritten bash command", () => {
  const status = mapRewriteResult({ input: "git status", outcome: "Rewritten", command: "hypa git status" });
  const result = applyStatus("git status", status, false, "deny");
  assert.equal(result.event.input.command, "hypa git status");
  assert.equal(result.block, undefined);
});

test("simulated non-ui ask fallback denies by default", () => {
  const status = mapRewriteResult({ input: "sudo reboot", outcome: "Ask", command: "sudo reboot" });
  const result = applyStatus("sudo reboot", status, false, "deny");
  assert.equal(result.event.input.command, "sudo reboot");
  assert.equal(result.block, true);
});

test("simulated non-ui ask fallback can allow deterministically", () => {
  const status = mapRewriteResult({ input: "sudo reboot", outcome: "Ask", command: "hypa -c 'sudo reboot'" });
  const result = applyStatus("sudo reboot", status, false, "allow");
  assert.equal(result.event.input.command, "hypa -c 'sudo reboot'");
  assert.equal(result.block, undefined);
});

test("simulated tool call qualifies rewritten command with the resolved Windows binary", () => {
  const binary = String.raw`C:\Program Files\Hypa\hypa.exe`;
  const status = mapRewriteResult({ input: "git status", outcome: "Rewritten", command: "hypa git status" });
  const result = applyStatus("git status", status, false, "deny", { resolvedBinary: binary });
  assert.equal(result.event.input.command, `'${binary}' git status`);
  assert.equal(result.block, undefined);
});

test("simulated ask-allow path qualifies rewritten command with the resolved binary", () => {
  const binary = "/opt/homebrew/bin/hypa";
  const status = mapRewriteResult({
    input: "sudo reboot",
    outcome: "Ask",
    command: "hypa -c 'sudo reboot'",
  });
  const result = applyStatus("sudo reboot", status, false, "allow", { resolvedBinary: binary });
  assert.equal(result.event.input.command, "/opt/homebrew/bin/hypa -c 'sudo reboot'");
  assert.equal(result.block, undefined);
});

test("simulated tool call forwards GenericWrapper bash timeout to hypa --timeout-ms", () => {
  const status = mapRewriteResult({
    input: "sleep 31",
    outcome: "GenericWrapper",
    command: 'hypa -c "sleep 31"',
  });
  const result = applyStatus("sleep 31", status, false, "deny", { timeout: 35 });
  assert.equal(result.event.input.command, 'hypa --timeout-ms 35000 -c "sleep 31"');
  assert.equal(result.block, undefined);
});

test("simulated tool call forwards Rewritten bash timeout to hypa --timeout-ms", () => {
  const status = mapRewriteResult({ input: "git status", outcome: "Rewritten", command: "hypa git status" });
  const result = applyStatus("git status", status, false, "deny", { timeout: 35 });
  assert.equal(result.event.input.command, "hypa --timeout-ms 35000 git status");
});

test("simulated ask-allow path forwards timeout when the command is rewritten", () => {
  const status = mapRewriteResult({ input: "sudo reboot", outcome: "Ask", command: "hypa -c 'sudo reboot'" });
  const result = applyStatus("sudo reboot", status, false, "allow", { timeout: 35 });
  assert.equal(result.event.input.command, "hypa --timeout-ms 35000 -c 'sudo reboot'");
  assert.equal(result.block, undefined);
});

test("simulated ask-allow path does not inject timeout onto a non-hypa command", () => {
  const status = mapRewriteResult({ input: "sudo reboot", outcome: "Ask", command: "sudo reboot" });
  const result = applyStatus("sudo reboot", status, false, "allow", { timeout: 35 });
  assert.equal(result.event.input.command, "sudo reboot");
});

test("simulated tool call leaves rewritten command unchanged without a timeout", () => {
  const status = mapRewriteResult({
    input: "sleep 31",
    outcome: "GenericWrapper",
    command: 'hypa -c "sleep 31"',
  });
  const result = applyStatus("sleep 31", status, false, "deny");
  assert.equal(result.event.input.command, 'hypa -c "sleep 31"');
});

test("simulated passthrough does not inject timeout onto the original command", () => {
  const status = mapRewriteResult({ input: "echo ok", outcome: "Passthrough", command: "echo ok" });
  const result = applyStatus("echo ok", status, false, "deny", { timeout: 35 });
  assert.equal(result.event.input.command, "echo ok");
});

test("simulated tool call injects timeout then qualifies the leading hypa token", () => {
  const binary = String.raw`C:\Program Files\Hypa\hypa.exe`;
  const status = mapRewriteResult({
    input: "sleep 31",
    outcome: "GenericWrapper",
    command: 'hypa -c "sleep 31"',
  });
  const result = applyStatus("sleep 31", status, false, "deny", { timeout: 35, resolvedBinary: binary });
  assert.equal(result.event.input.command, `'${binary}' --timeout-ms 35000 -c "sleep 31"`);
});
