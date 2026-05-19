using System.Diagnostics;
using System.Runtime.InteropServices;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Infrastructure.Updates;

public sealed class NpmUpdateStrategy : IUpdateStrategy
{
    public string Name => "npm";

    public bool CanHandle(InstallMetadata metadata) =>
        string.Equals(
            Environment.GetEnvironmentVariable("HYPA_INSTALL_SOURCE"),
            "npm",
            StringComparison.OrdinalIgnoreCase);

    public Task<Result<UpdatePlan, Error>> PlanAsync(UpdateInfo update, InstallMetadata metadata, CancellationToken ct)
    {
        const string command = "npm update -g @hypabolic/hypa";
        var plan = new UpdatePlan(
            Strategy: Name,
            CanAutoUpdate: true,
            Summary: "Update via npm",
            Command: command,
            Detail: $"Run: {command}");

        return Task.FromResult(Result<UpdatePlan, Error>.Ok(plan));
    }

    public Task<Result<Unit, Error>> ApplyAsync(UpdateInfo update, InstallMetadata metadata, CancellationToken ct)
    {
        try
        {
            var (file, args) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ("cmd.exe", "/c npm update -g @hypabolic/hypa")
                : ("npm", "update -g @hypabolic/hypa");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
            });

            if (process is null)
                return Task.FromResult(Result<Unit, Error>.Fail(new Error("Update.NpmFailed", "Failed to start npm process.")));

            process.WaitForExit();

            return process.ExitCode == 0
                ? Task.FromResult(Result<Unit, Error>.Ok(Unit.Value))
                : Task.FromResult(Result<Unit, Error>.Fail(new Error("Update.NpmFailed", $"npm exited with code {process.ExitCode}.")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<Unit, Error>.Fail(new Error("Update.NpmFailed", ex.Message)));
        }
    }
}
