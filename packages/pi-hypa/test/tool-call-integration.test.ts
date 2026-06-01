import test from "node:test";
import assert from "node:assert/strict";
import { mapRewriteResult } from "../extensions/policy.js";

function applyStatus(command: string, status: ReturnType<typeof mapRewriteResult>, hasUI: boolean, askFallback: "deny" | "allow") {
  const event = { input: { command } };
  switch (status.kind) {
    case "rewritten":
      event.input.command = status.command;
      return { event };
    case "passthrough":
      return { event };
    case "deny":
      return { event, block: true };
    case "ask":
      if (hasUI || askFallback === "allow") {
        event.input.command = status.command;
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
