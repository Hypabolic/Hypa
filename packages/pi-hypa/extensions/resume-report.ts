import { mkdirSync, renameSync, writeFileSync } from "node:fs";
import path from "node:path";

export const RESUME_ATTEMPT_ID_ENV = "HYPA_RESUME_ATTEMPT_ID";
export const RESUME_REPORT_PATH_ENV = "HYPA_RESUME_REPORT_PATH";
export const RESUME_REPORT_SCHEMA = 1;
const ATTEMPT_ID_PATTERN = /^[A-Za-z0-9_-]+$/;

export interface ResumeReportEnv {
  [key: string]: string | undefined;
}

export interface ResumeSessionStartEvent {
  reason?: string;
}

export interface ResumeSessionManager {
  getSessionId(): string;
  getSessionFile(): string | undefined;
}

export interface ResumeReportContext {
  sessionManager: ResumeSessionManager;
}

export interface PiResumeReport {
  schema: number;
  attempt_id: string;
  conversation_id: string;
  session_file?: string;
  reason?: string;
}

export function reportResumeIfRequested(
  env: ResumeReportEnv,
  event: ResumeSessionStartEvent,
  ctx: ResumeReportContext,
): PiResumeReport | undefined {
  const attemptId = env[RESUME_ATTEMPT_ID_ENV]?.trim();
  const reportPath = env[RESUME_REPORT_PATH_ENV]?.trim();
  if (!attemptId || !reportPath) return undefined;
  if (!ATTEMPT_ID_PATTERN.test(attemptId)) return undefined;
  if (!path.isAbsolute(reportPath)) return undefined;

  let conversationId = "";
  let sessionFile: string | undefined;
  try {
    conversationId = ctx.sessionManager.getSessionId()?.trim() ?? "";
    sessionFile = ctx.sessionManager.getSessionFile();
  } catch {
    return undefined;
  }
  if (!conversationId) return undefined;

  const report: PiResumeReport = {
    schema: RESUME_REPORT_SCHEMA,
    attempt_id: attemptId,
    conversation_id: conversationId,
  };
  if (sessionFile) report.session_file = sessionFile;
  if (event.reason) report.reason = event.reason;

  try {
    mkdirSync(path.dirname(reportPath), { recursive: true });
    const partial = `${reportPath}.partial`;
    writeFileSync(partial, `${JSON.stringify(report)}\n`, { encoding: "utf8" });
    renameSync(partial, reportPath);
    return report;
  } catch {
    return undefined;
  }
}
