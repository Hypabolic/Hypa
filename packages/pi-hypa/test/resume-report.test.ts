import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import path, { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import {
  reportResumeIfRequested,
  RESUME_ATTEMPT_ID_ENV,
  RESUME_REPORT_PATH_ENV,
  RESUME_REPORT_SCHEMA,
} from "../extensions/resume-report.js";

function tempDir(): string {
  return mkdtempSync(join(tmpdir(), "pi-hypa-resume-"));
}

test("reportResumeIfRequested is a no-op without attempt env", () => {
  const dir = tempDir();
  const reportPath = join(dir, "missing.json");
  const written = reportResumeIfRequested({}, { reason: "startup" }, {
    sessionManager: {
      getSessionId: () => "sess_live",
      getSessionFile: () => "/tmp/s.jsonl",
    },
  });
  assert.equal(written, undefined);
  assert.equal(existsSync(reportPath), false);
});

test("reportResumeIfRequested writes attempt_id and conversation_id", () => {
  const dir = tempDir();
  const reportPath = join(dir, "reports", "attempt.json");
  const written = reportResumeIfRequested(
    {
      [RESUME_ATTEMPT_ID_ENV]: "attempt1",
      [RESUME_REPORT_PATH_ENV]: reportPath,
    },
    { reason: "startup" },
    {
      sessionManager: {
        getSessionId: () => "sess_live",
        getSessionFile: () => "/tmp/s.jsonl",
      },
    },
  );
  assert.deepEqual(written, {
    schema: RESUME_REPORT_SCHEMA,
    attempt_id: "attempt1",
    conversation_id: "sess_live",
    session_file: "/tmp/s.jsonl",
    reason: "startup",
  });
  const disk = JSON.parse(readFileSync(reportPath, "utf8"));
  assert.deepEqual(disk, written);
});

test("reportResumeIfRequested skips a copied-file story without a session id", () => {
  const dir = tempDir();
  const reportPath = join(dir, "report.json");
  const written = reportResumeIfRequested(
    {
      [RESUME_ATTEMPT_ID_ENV]: "attempt1",
      [RESUME_REPORT_PATH_ENV]: reportPath,
    },
    { reason: "startup" },
    {
      sessionManager: {
        getSessionId: () => "  ",
        getSessionFile: () => "/copied.jsonl",
      },
    },
  );
  assert.equal(written, undefined);
  assert.equal(existsSync(reportPath), false);
});

test("reportResumeIfRequested refuses a relative report path", () => {
  const written = reportResumeIfRequested(
    {
      [RESUME_ATTEMPT_ID_ENV]: "attempt1",
      [RESUME_REPORT_PATH_ENV]: "relative.json",
    },
    { reason: "startup" },
    {
      sessionManager: {
        getSessionId: () => "sess_live",
        getSessionFile: () => undefined,
      },
    },
  );
  assert.equal(written, undefined);
});

test("win32 dest report paths are absolute under path.win32", () => {
  const reportPath = String.raw`C:\Users\hypa\.hypa\continuity\resume-reports\attempt.json`;
  assert.equal(path.win32.isAbsolute(reportPath), true);
  assert.equal(path.posix.isAbsolute(reportPath), false);
  assert.equal(path.win32.isAbsolute("relative.json"), false);
  const src = readFileSync(
    join(dirname(fileURLToPath(import.meta.url)), "../extensions/resume-report.ts"),
    "utf8",
  );
  assert.match(src, /path\.isAbsolute\(reportPath\)/);
  assert.doesNotMatch(src, /startsWith\("\/"\)/);
});
