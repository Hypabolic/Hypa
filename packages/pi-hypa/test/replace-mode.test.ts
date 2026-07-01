import test from "node:test";
import assert from "node:assert/strict";
import { applyReplaceModeFilter } from "../extensions/index.js";

test("replace mode filter removes all disabled builtins", () => {
  const tools = ["bash", "read", "grep", "find", "ls", "hypa_shell", "hypa_read", "hypa_grep", "hypa_find", "hypa_ls"];
  const filtered = applyReplaceModeFilter(tools, "replace");
  assert.deepEqual(filtered, ["hypa_shell", "hypa_read", "hypa_grep", "hypa_find", "hypa_ls"]);
});

test("replace mode filter is a no-op when builtins are absent", () => {
  const tools = ["hypa_shell", "hypa_read", "hypa_grep"];
  assert.deepEqual(applyReplaceModeFilter(tools, "replace"), tools);
});

test("replace mode filter is idempotent", () => {
  const tools = ["bash", "hypa_shell", "hypa_read"];
  const once = applyReplaceModeFilter(tools, "replace");
  const twice = applyReplaceModeFilter(once, "replace");
  assert.deepEqual(once, twice);
});

test("replace mode filter re-runs on subsequent turns (handles Pi reloads)", () => {
  // The filter runs on every before_agent_start — idempotency means this is safe
  // and also correct if Pi re-registers built-ins during a reload.
  const toolsWithBuiltins = ["bash", "read", "grep", "find", "ls", "hypa_shell", "hypa_read"];
  let activeTools = [...toolsWithBuiltins];

  function simulateBeforeAgentStart() {
    activeTools = applyReplaceModeFilter(activeTools, "replace");
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
  assert.deepEqual(applyReplaceModeFilter(tools, "additive"), tools);
});
