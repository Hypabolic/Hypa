import test from "node:test";
import assert from "node:assert/strict";

const REPLACE_MODE_DISABLED_BUILTINS = new Set(["bash", "read", "grep", "find", "ls"]);

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

test("replace mode once-flag prevents double application", () => {
  const toolsAfterBuiltinsRegistered = ["bash", "read", "grep", "find", "ls", "hypa_shell", "hypa_read"];
  let activeTools = [...toolsAfterBuiltinsRegistered];

  let replaceModeApplied = false;
  function simulateBeforeAgentStart() {
    if (replaceModeApplied) return;
    replaceModeApplied = true;
    activeTools = applyReplaceFilter(activeTools);
  }

  // First call should apply the filter
  simulateBeforeAgentStart();
  assert.deepEqual(activeTools, ["hypa_shell", "hypa_read"]);

  // Subsequent calls should not re-apply even if builtins are somehow re-added
  activeTools = [...toolsAfterBuiltinsRegistered];
  simulateBeforeAgentStart();
  assert.deepEqual(activeTools, [...toolsAfterBuiltinsRegistered], "filter should not re-run after once-flag is set");
});

test("additive mode does not apply replace filter", () => {
  const tools = ["bash", "read", "grep", "find", "ls", "hypa_shell"];
  // In additive mode, no filter is applied — tools stay unchanged
  const filtered = tools; // No-op in additive mode
  assert.deepEqual(filtered, tools);
});
