using System.Text.Json;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Cli.Doctor;

internal static class PluginDoctorStdoutParser
{
    public static DoctorCheckResult ToResult(string label, PluginDoctorRunResult ran)
    {
        if (ran.TimedOut)
            return new DoctorCheckResult(label, "timed out", DoctorStatus.Fail, "doctor child exceeded the timeout");
        if (ran.ExitCode is not 0)
        {
            var detail = string.IsNullOrWhiteSpace(ran.Stderr) ? ran.Stdout : ran.Stderr;
            return new DoctorCheckResult(
                label,
                "child exited " + (ran.ExitCode?.ToString() ?? "null"),
                DoctorStatus.Fail,
                string.IsNullOrWhiteSpace(detail) ? null : detail.Trim());
        }

        var line = LastNonEmptyLine(ran.Stdout);
        if (string.IsNullOrWhiteSpace(line))
            return new DoctorCheckResult(label, "malformed doctor json", DoctorStatus.Fail, "stdout was empty");

        PluginDoctorStdoutDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(line, PluginDoctorJsonContext.Default.PluginDoctorStdoutDto);
        }
        catch (JsonException ex)
        {
            return new DoctorCheckResult(label, "malformed doctor json", DoctorStatus.Fail, ex.Message);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Status))
            return new DoctorCheckResult(label, "malformed doctor json", DoctorStatus.Fail);

        var status = dto.Status.Trim();
        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            return new DoctorCheckResult(label, dto.Value ?? "ok", DoctorStatus.Ok, dto.Detail, dto.Hint);
        if (string.Equals(status, "warn", StringComparison.OrdinalIgnoreCase))
            return new DoctorCheckResult(label, dto.Value ?? "warn", DoctorStatus.Warn, dto.Detail, dto.Hint);
        if (string.Equals(status, "fail", StringComparison.OrdinalIgnoreCase))
            return new DoctorCheckResult(label, dto.Value ?? "fail", DoctorStatus.Fail, dto.Detail, dto.Hint);

        return new DoctorCheckResult(label, "malformed doctor json", DoctorStatus.Fail, "status must be ok, warn, or fail");
    }

    internal static string? LastNonEmptyLine(string stdout)
    {
        string? last = null;
        using var reader = new StringReader(stdout ?? "");
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                last = line.Trim();
        }

        return last;
    }
}
