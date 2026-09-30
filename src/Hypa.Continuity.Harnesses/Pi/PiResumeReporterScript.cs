namespace Hypa.Continuity.Harnesses.Pi;

/// <summary>
/// Standalone Pi <c>-e</c> reporter. Dest HOME may not load user-level pi-hypa.
/// </summary>
public static class PiResumeReporterScript
{
    public const string TypeScript =
        """
        import { mkdirSync, renameSync, writeFileSync } from "node:fs";
        import path from "node:path";

        const ATTEMPT_ID_ENV = "HYPA_RESUME_ATTEMPT_ID";
        const REPORT_PATH_ENV = "HYPA_RESUME_REPORT_PATH";
        const ATTEMPT_ID_PATTERN = /^[A-Za-z0-9_-]+$/;

        export default function (pi) {
          pi.on("session_start", (event, ctx) => {
            const attemptId = process.env[ATTEMPT_ID_ENV]?.trim();
            const reportPath = process.env[REPORT_PATH_ENV]?.trim();
            if (!attemptId || !reportPath) return;
            if (!ATTEMPT_ID_PATTERN.test(attemptId)) return;
            if (!path.isAbsolute(reportPath)) return;
            let conversationId = "";
            let sessionFile = undefined;
            try {
              conversationId = ctx.sessionManager.getSessionId()?.trim() ?? "";
              sessionFile = ctx.sessionManager.getSessionFile();
            } catch {
              return;
            }
            if (!conversationId) return;
            const report = {
              schema: 1,
              attempt_id: attemptId,
              conversation_id: conversationId,
            };
            if (sessionFile) report.session_file = sessionFile;
            if (event?.reason) report.reason = event.reason;
            try {
              mkdirSync(path.dirname(reportPath), { recursive: true });
              const partial = reportPath + ".partial";
              writeFileSync(partial, JSON.stringify(report) + "\n", { encoding: "utf8" });
              renameSync(partial, reportPath);
            } catch {
              return;
            }
          });
        }
        """;
}
