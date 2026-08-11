import test from "node:test";
import assert from "node:assert/strict";
import { homedir } from "node:os";
import * as path from "node:path";
import type { HypaFileReadEvent } from "../extensions/types.js";

// ---------------------------------------------------------------------------
// Helpers — simulate the tool_call handler logic in isolation
// ---------------------------------------------------------------------------

function normalizePathArg(p: string): string {
  const unprefixed = p.startsWith("@") ? p.slice(1) : p;
  if (unprefixed === "~") return homedir();
  if (unprefixed.startsWith("~/")) return homedir() + unprefixed.slice(1);
  return unprefixed;
}

function buildReadEvent(
  rawPath: string,
  offset?: number,
  limit?: number,
  cwd = "/project",
): HypaFileReadEvent {
  const normalized = normalizePathArg(rawPath);
  const filePath = path.isAbsolute(normalized) ? normalized : path.resolve(cwd, normalized);
  return {
    filePath,
    requestedOffset: typeof offset === "number" ? Math.max(1, Math.floor(offset)) : 1,
    requestedLimit: typeof limit === "number" ? Math.max(1, Math.floor(limit)) : undefined,
    timestamp: Date.now(),
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test("buildReadEvent resolves relative paths against cwd", () => {
  const evt = buildReadEvent("src/main.go", undefined, undefined, "/project");
  assert.equal(evt.filePath, "/project/src/main.go");
  assert.equal(evt.requestedOffset, 1);
  assert.equal(evt.requestedLimit, undefined);
});

test("buildReadEvent keeps absolute paths as-is", () => {
  const evt = buildReadEvent("/tmp/foo.ts");
  assert.equal(evt.filePath, "/tmp/foo.ts");
});

test("buildReadEvent expands tilde to home directory", () => {
  const evt = buildReadEvent("~/notes.md");
  assert.equal(evt.filePath, path.join(homedir(), "notes.md"));
});

test("buildReadEvent strips leading @ from file-mention paths", () => {
  const evt = buildReadEvent("@src/main.go", undefined, undefined, "/project");
  assert.equal(evt.filePath, "/project/src/main.go");
});

test("buildReadEvent clamps offset to minimum 1", () => {
  const evt = buildReadEvent("file.ts", 0);
  assert.equal(evt.requestedOffset, 1);

  const evt2 = buildReadEvent("file.ts", -5);
  assert.equal(evt2.requestedOffset, 1);
});

test("buildReadEvent clamps limit to minimum 1", () => {
  const evt = buildReadEvent("file.ts", 1, 0);
  assert.equal(evt.requestedLimit, 1);
});

test("buildReadEvent floors fractional offset and limit", () => {
  const evt = buildReadEvent("file.ts", 3.9, 10.7);
  assert.equal(evt.requestedOffset, 3);
  assert.equal(evt.requestedLimit, 10);
});

test("buildReadEvent produces undefined requestedLimit when no limit given", () => {
  const evt = buildReadEvent("file.ts", 5);
  assert.equal(evt.requestedLimit, undefined);
});

test("process emits pi-hypa:file-read event with correct shape", async () => {
  const received: HypaFileReadEvent[] = [];
  const listener = (evt: HypaFileReadEvent) => received.push(evt);

  process.on("pi-hypa:file-read", listener as (...args: unknown[]) => void);
  try {
    const evt = buildReadEvent("src/handler.go", 10, 50, "/myproject");
    (process as NodeJS.EventEmitter).emit("pi-hypa:file-read", evt);

    assert.equal(received.length, 1);
    assert.equal(received[0].filePath, "/myproject/src/handler.go");
    assert.equal(received[0].requestedOffset, 10);
    assert.equal(received[0].requestedLimit, 50);
    assert.ok(typeof received[0].timestamp === "number");
  } finally {
    process.off("pi-hypa:file-read", listener as (...args: unknown[]) => void);
  }
});
