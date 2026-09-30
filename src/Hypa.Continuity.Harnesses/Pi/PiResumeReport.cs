using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Harnesses.Pi;

/// <summary>
/// Dest resume report from a running Pi process. File copy is not this document.
/// </summary>
public sealed record PiResumeReport
{
    public const int CurrentSchema = 1;
    public const string AttemptIdEnv = LiveResumeStartArgs.AttemptIdEnv;
    public const string ReportPathEnv = LiveResumeStartArgs.ReportPathEnv;
    public const string ReporterFileName = "resume-reporter.ts";

    public int Schema { get; init; }
    public string? AttemptId { get; init; }
    public string? ConversationId { get; init; }
    public string? SessionFile { get; init; }
    public string? Reason { get; init; }

    public static string NewAttemptId() => Guid.NewGuid().ToString("N");

    public static string ContinuityDir(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return Path.Combine(Path.GetFullPath(home.Trim()), ".hypa", "continuity");
    }

    public static string ReportsDirectory(string home) =>
        Path.Combine(ContinuityDir(home), "resume-reports");

    public static string ReporterPath(string home) =>
        Path.Combine(ContinuityDir(home), ReporterFileName);

    public static string ReportFile(string home, string attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        return Path.Combine(ReportsDirectory(home), attemptId + ".json");
    }

    public static bool IsAttemptId(string? value) => LiveResumeStartArgs.IsAttemptId(value);

    public static bool TryReadEnv(
        IReadOnlyDictionary<string, string>? env,
        out string attemptId,
        out string reportPath)
    {
        attemptId = "";
        reportPath = "";
        if (env is null)
            return false;
        if (!env.TryGetValue(AttemptIdEnv, out var rawAttempt)
            || !IsAttemptId(rawAttempt))
        {
            return false;
        }

        if (!env.TryGetValue(ReportPathEnv, out var rawPath)
            || string.IsNullOrWhiteSpace(rawPath)
            || !Path.IsPathRooted(rawPath.Trim()))
        {
            return false;
        }

        attemptId = rawAttempt.Trim();
        reportPath = Path.GetFullPath(rawPath.Trim());
        return true;
    }

    public static bool IsUnderHome(string path, string home) =>
        LiveResumeStartArgs.IsUnderHome(path, home);

    /// <summary>
    /// Parse a complete report. Returns <c>null</c> when the file is for another attempt.
    /// </summary>
    public static ResumeProbeResult? ReadIfMatching(string json, string attemptId)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        PiResumeReport? report;
        try
        {
            report = JsonSerializer.Deserialize(json, PiResumeReportJsonContext.Default.PiResumeReport);
        }
        catch (JsonException ex)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "resume report is not JSON: " + ex.Message);
        }

        if (report is null)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "resume report empty");
        }

        if (!string.Equals(report.AttemptId, attemptId, StringComparison.Ordinal))
            return null;

        if (report.Schema != CurrentSchema)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "resume report schema unsupported");
        }

        if (string.IsNullOrWhiteSpace(report.ConversationId))
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "resume report conversation_id empty");
        }

        return ResumeProbeResult.Found(
            report.ConversationId.Trim(),
            ResumeEvidence.HarnessReported,
            report.SessionFile);
    }

    public static string Serialize(PiResumeReport report) =>
        JsonSerializer.Serialize(report, PiResumeReportJsonContext.Default.PiResumeReport);
}

[JsonSerializable(typeof(PiResumeReport))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class PiResumeReportJsonContext : JsonSerializerContext;
