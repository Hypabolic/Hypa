using System.Text;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Live Pi resume reporter. Pane create and occupant start each mint a unique
/// attempt. Dest start may overlay its own attempt env.
/// </summary>
public static class OccupantResumeReporter
{
    public const string AttemptIdEnv = "HYPA_RESUME_ATTEMPT_ID";
    public const string ReportPathEnv = "HYPA_RESUME_REPORT_PATH";
    public const string FileName = "resume-reporter.ts";

    /// <summary>
    /// Keep this script aligned with Continuity dest-start
    /// <c>PiResumeReporterScript</c>. Occupant start cannot load Continuity.
    /// </summary>
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

    public static string NewAttemptId() => Guid.NewGuid().ToString("N");

    public static bool IsAttemptId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                continue;
            return false;
        }

        return true;
    }

    public static string ContinuityDir(string cubeHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cubeHome);
        return Path.Combine(Path.GetFullPath(cubeHome.Trim()), ".hypa", "continuity");
    }

    public static string PathFor(string cubeHome) =>
        Path.Combine(ContinuityDir(cubeHome), FileName);

    public static string PiAgentDir(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return Path.Combine(Path.GetFullPath(home.Trim()), ".pi", "agent");
    }

    public static string PiExtensionsDir(string home) =>
        Path.Combine(PiAgentDir(home), "extensions");

    public static string PiExtensionPath(string home) =>
        Path.Combine(PiExtensionsDir(home), FileName);

    public static string ReportFile(string cubeHome, string attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        return Path.Combine(ContinuityDir(cubeHome), "resume-reports", attemptId.Trim() + ".json");
    }

    public static void Write(string cubeHome)
    {
        var dir = ContinuityDir(cubeHome);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            PathFor(cubeHome),
            TypeScript,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Register the resume reporter in pane HOME Pi config. Pi loads
    /// <c>~/.pi/agent/extensions/*.ts</c> without an argv <c>-e</c> flag.
    /// </summary>
    public static void WritePiHomeExtension(string home)
    {
        var dir = PiExtensionsDir(home);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            PiExtensionPath(home),
            TypeScript,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
