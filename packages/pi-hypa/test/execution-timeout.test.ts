import test from "node:test";
import assert from "node:assert/strict";
import { injectExecutionTimeout } from "../extensions/execution-timeout.js";

test("injectExecutionTimeout adds GenericWrapper timeout after hypa", () => {
  assert.equal(
    injectExecutionTimeout('hypa -c "sleep 31"', 35),
    'hypa --timeout-ms 35000 -c "sleep 31"',
  );
});

test("injectExecutionTimeout adds Rewritten timeout after hypa", () => {
  assert.equal(injectExecutionTimeout("hypa git status", 35), "hypa --timeout-ms 35000 git status");
});

test("injectExecutionTimeout inserts after a quoted leading binary path", () => {
  assert.equal(
    injectExecutionTimeout(`'C:\\Program Files\\hypa.exe' -c "sleep 31"`, 35),
    `'C:\\Program Files\\hypa.exe' --timeout-ms 35000 -c "sleep 31"`,
  );
  assert.equal(
    injectExecutionTimeout(`"/usr/local/bin/hypa" git status`, 35),
    `"/usr/local/bin/hypa" --timeout-ms 35000 git status`,
  );
});

test("injectExecutionTimeout leaves the command unchanged without a timeout", () => {
  const command = 'hypa -c "sleep 31"';
  assert.equal(injectExecutionTimeout(command, undefined), command);
  assert.equal(injectExecutionTimeout(command, null), command);
});

test("injectExecutionTimeout leaves the command unchanged for invalid timeouts", () => {
  const command = "hypa git status";
  assert.equal(injectExecutionTimeout(command, "35"), command);
  assert.equal(injectExecutionTimeout(command, NaN), command);
  assert.equal(injectExecutionTimeout(command, Infinity), command);
  assert.equal(injectExecutionTimeout(command, -Infinity), command);
  assert.equal(injectExecutionTimeout(command, 0), command);
  assert.equal(injectExecutionTimeout(command, -1), command);
  assert.equal(injectExecutionTimeout(command, { seconds: 35 }), command);
});

test("injectExecutionTimeout does not round down a fractional second", () => {
  assert.equal(
    injectExecutionTimeout('hypa -c "sleep 31"', 35.7),
    'hypa --timeout-ms 35700 -c "sleep 31"',
  );
});

test("injectExecutionTimeout does not inject onto a non-hypa Ask command", () => {
  assert.equal(injectExecutionTimeout("sudo reboot", 35), "sudo reboot");
});

test("injectExecutionTimeout skips millisecond values the CLI cannot accept", () => {
  const command = "hypa git status";
  // 0.0001s → 0.1ms, not an integer
  assert.equal(injectExecutionTimeout(command, 0.0001), command);
  // 2147483.648s → 2147483648ms, above signed 32-bit --timeout-ms
  assert.equal(injectExecutionTimeout(command, 2147483.648), command);
});
