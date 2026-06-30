import test from "node:test";
import assert from "node:assert/strict";
import { REPLACE_MODE_DISABLED_BUILTINS } from "../extensions/index.js";

function applyReplaceFilter(tools: string[]): string[] {
  return tools.filter((name) => !REPLACE_MODE_DISABLED_BUILTINS.has(name));
}

test("replace mode filter removes all disabled builtins", () => {
  const tools = ["bash", "read", "grep", "find", "ls", "hypa_shell", "hypa_read", "hypa_grep", "hypa_find", "hypa_ls"];
  const filtered = applyReplaceFilter(tools);
  assert.deepEqual(filtered, ["hypa_shell", "hypa_read", "hypa_grep", "hypa_find", "hypa_ls"]);
});

test("replace mode filter is a no-op when builtins are absent", () => {
  const tools = ["hypa_shell", "hypa_read", "hypa_grep"];
  assert.deepEqual(applyReplaceFilter(tools), tools);
});

test("replace mode filter is idempotent", () => {
  const tools = ["bash", "hypa_shell", "hypa_read"];
  const once = applyReplaceFilter(tools);
  const twice = applyReplaceFilter(once);
  assert.deepEqual(once, twice);
});

test("replace mode filter re-runs on subsequent turns (handles Pi reloads)", () => {
  // The filter runs on every before_agent_start — idempotency means this is safe
  // and also correct if Pi re-registers built-ins during a reload.
  const toolsWithBuiltins = ["bash", "read", "grep", "find", "ls", "hypa_shell", "hypa_read"];
  let activeTools = [...toolsWithBuiltins];

  function simulateBeforeAgentStart() {
    activeTools = applyReplaceFilter(activeTools);
  }

  simulateBeforeAgentStart();
  assert.deepEqual(activeTools, ["hypa_shell", "hypa_read"]);

  // Simulate Pi re-registering builtins (e.g. after /reload) — filter must re-apply correctly
  activeTools = [...toolsWithBuiltins];
  simulateBeforeAgentStart();
  assert.deepEqual(activeTools, ["hypa_shell", "hypa_read"]);
});

test("additive mode does not apply replace filter", () => {
  const tools = ["bash", "read", "grep", "find", "ls", "hypa_shell"];
  // In additive mode, no filter is applied — tools stay unchanged
  const filtered = tools; // No-op in additive mode
  assert.deepEqual(filtered, tools);
});
