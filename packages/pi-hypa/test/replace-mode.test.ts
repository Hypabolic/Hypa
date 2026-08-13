import test from "node:test";
import assert from "node:assert/strict";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import registerHypaExtension, {
  applyReplaceModeFilter,
  REPLACE_MODE_BUILTIN_REPLACEMENTS,
} from "../extensions/index.js";

const PARENT_FULL = [
  "bash",
  "read",
  "grep",
  "find",
  "ls",
  "hypa_shell",
  "hypa_read",
  "hypa_grep",
  "hypa_find",
  "hypa_ls",
];

const PARENT_HYPA_ONLY = ["hypa_shell", "hypa_read", "hypa_grep", "hypa_find", "hypa_ls"];

const SUBAGENT_NO_HYPA = ["read", "bash", "edit", "write", "grep", "find", "ls"];

const EXPLORE_STYLE = ["read", "bash", "grep", "find", "ls"];

const PARTIAL_ALLOWLIST = ["bash", "read", "grep", "find", "ls", "edit", "write", "hypa_read"];

test("replace mode strips all builtins when all five hypa replacements are present", () => {
  const filtered = applyReplaceModeFilter(PARENT_FULL, "replace");
  assert.deepEqual(filtered, PARENT_HYPA_ONLY);
});

test("replace mode keeps builtins when no hypa_* tools are present (subagent)", () => {
  assert.deepEqual(applyReplaceModeFilter(SUBAGENT_NO_HYPA, "replace"), SUBAGENT_NO_HYPA);
});

test("replace mode keeps explore-style builtin-only tool lists unchanged", () => {
  assert.deepEqual(applyReplaceModeFilter(EXPLORE_STYLE, "replace"), EXPLORE_STYLE);
});

test("replace mode strips only builtins whose hypa replacement is present", () => {
  const filtered = applyReplaceModeFilter(PARTIAL_ALLOWLIST, "replace");
  assert.deepEqual(filtered, ["bash", "grep", "find", "ls", "edit", "write", "hypa_read"]);
});

test("unpaired hypa tools do not authorize stripping builtins", () => {
  const tools = ["bash", "read", "grep", "hypa_mcp_proxy"];
  assert.deepEqual(applyReplaceModeFilter(tools, "replace"), tools);
});

test("replace mode strips builtins when hypa replacement appears before them", () => {
  const tools = ["hypa_read", "read", "bash"];
  assert.deepEqual(applyReplaceModeFilter(tools, "replace"), ["hypa_read", "bash"]);
});

test("replace mode filter returns empty array unchanged", () => {
  assert.deepEqual(applyReplaceModeFilter([], "replace"), []);
});

test("replace mode filter is a no-op when builtins are absent", () => {
  const tools = ["hypa_shell", "hypa_read", "hypa_grep"];
  assert.deepEqual(applyReplaceModeFilter(tools, "replace"), tools);
});

test("additive mode does not apply replace filter", () => {
  const tools = ["bash", "read", "grep", "find", "ls", "hypa_shell"];
  assert.deepEqual(applyReplaceModeFilter(tools, "additive"), tools);
});

test("replace mode filter is idempotent on full parent, no-hypa subagent, and partial lists", () => {
  for (const tools of [PARENT_FULL, SUBAGENT_NO_HYPA, PARTIAL_ALLOWLIST]) {
    const once = applyReplaceModeFilter(tools, "replace");
    const twice = applyReplaceModeFilter(once, "replace");
    assert.deepEqual(once, twice);
  }
});

test("replace mode filter re-runs on subsequent turns (handles Pi reloads)", () => {
  // The filter runs on every before_agent_start — idempotency means this is safe
  // and also correct if Pi re-registers built-ins during a reload.
  let activeTools = [...PARENT_FULL];

  function simulateBeforeAgentStart() {
    activeTools = applyReplaceModeFilter(activeTools, "replace");
  }

  simulateBeforeAgentStart();
  assert.deepEqual(activeTools, PARENT_HYPA_ONLY);

  // Simulate Pi re-registering builtins (e.g. after /reload) — filter must re-apply correctly
  activeTools = [...PARENT_FULL];
  simulateBeforeAgentStart();
  assert.deepEqual(activeTools, PARENT_HYPA_ONLY);
});

test("replace mode filter does not mutate the input array", () => {
  for (const mode of ["replace", "additive"]) {
    const tools = [...PARENT_FULL];
    const copy = [...tools];
    applyReplaceModeFilter(tools, mode);
    assert.deepEqual(tools, copy);
  }
});

type Handler = (...args: unknown[]) => unknown;

function createFakePi(initialTools: string[] = []) {
  const handlers = new Map<string, Handler[]>();
  const registeredTools: string[] = [];
  let activeTools = [...initialTools];
  let setActiveToolsCalls = 0;
  const pi = {
    on(event: string, handler: Handler) {
      const list = handlers.get(event) ?? [];
      list.push(handler);
      handlers.set(event, list);
    },
    registerTool(definition: Record<string, unknown>) {
      if (typeof definition.name === "string") registeredTools.push(definition.name);
    },
    registerCommand() {},
    getActiveTools() {
      return activeTools;
    },
    setActiveTools(names: string[]) {
      setActiveToolsCalls += 1;
      activeTools = [...names];
    },
  };
  return {
    pi: pi as unknown as ExtensionAPI,
    handlers,
    registeredTools,
    get setActiveToolsCalls() {
      return setActiveToolsCalls;
    },
    getActiveTools: () => activeTools,
    setActiveTools: (names: string[]) => {
      activeTools = [...names];
    },
  };
}

function withEnv(env: Record<string, string | undefined>, fn: () => void) {
  const keys = Object.keys(env);
  const previous = new Map(keys.map((key) => [key, process.env[key]]));
  try {
    for (const [key, value] of Object.entries(env)) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
    fn();
  } finally {
    for (const [key, value] of previous) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
  }
}

const HOOK_TEST_ENV = {
  HYPA_PI_MODE: "replace",
  HYPA_PI_CONFIG: "none",
  HYPA_PI_ENABLE_MCP_PROXY: "0",
  HYPA_BIN: "/tmp/hypa",
} as const;

test("replace mode registers before_agent_start and filters parent vs subagent lists", () => {
  withEnv({ ...HOOK_TEST_ENV, HYPA_PI_MODE: "replace" }, () => {
    const fake = createFakePi(PARENT_FULL);
    registerHypaExtension(fake.pi);

    assert.equal(fake.handlers.get("before_agent_start")?.length, 1);

    // Pairing map must match tools actually registered by registerHypaTools
    const registered = new Set(fake.registeredTools);
    for (const hypaName of Object.values(REPLACE_MODE_BUILTIN_REPLACEMENTS)) {
      assert.equal(registered.has(hypaName), true, `expected registered tool ${hypaName}`);
    }

    for (const handler of fake.handlers.get("before_agent_start") ?? []) {
      handler();
    }
    assert.deepEqual(fake.getActiveTools(), PARENT_HYPA_ONLY);
    assert.equal(fake.setActiveToolsCalls, 1);

    // Subagent-style list with no hypa replacements stays intact and skips setActiveTools
    fake.setActiveTools(SUBAGENT_NO_HYPA);
    const callsBefore = fake.setActiveToolsCalls;
    for (const handler of fake.handlers.get("before_agent_start") ?? []) {
      handler();
    }
    assert.deepEqual(fake.getActiveTools(), SUBAGENT_NO_HYPA);
    assert.equal(fake.setActiveToolsCalls, callsBefore);
  });
});

test("additive mode does not register before_agent_start replace filter", () => {
  withEnv({ ...HOOK_TEST_ENV, HYPA_PI_MODE: "additive" }, () => {
    const fake = createFakePi(PARENT_FULL);
    registerHypaExtension(fake.pi);

    assert.equal(fake.handlers.has("before_agent_start"), false);
    assert.deepEqual(fake.getActiveTools(), PARENT_FULL);
  });
});
