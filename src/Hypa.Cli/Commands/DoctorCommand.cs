using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using System.CommandLine;

namespace Hypa.Cli.Commands;

public sealed class DoctorCommand(DoctorService service, CodeDiagnosticsService codeDiagnostics)
{
    public Command Build()
    {
        var cmd = new Command("doctor", "Run diagnostics and report environment health.");
        cmd.SetHandler(() =>
        {
            var results = service.Run();
            foreach (var r in results)
            {
                var status = r.Status switch
                {
                    DoctorStatus.Ok => "  ok",
                    DoctorStatus.Warn => "warn",
                    DoctorStatus.Fail => "FAIL",
                    _ => "????",
                };
                Console.WriteLine($"[{status}] {r.Label,-20} {r.Value}");
                if (r.Detail is not null)
                    foreach (var line in r.Detail.Split('\n'))
                        Console.WriteLine($"       {line}");
            }
        });
        cmd.AddCommand(BuildCodeIntelligence());
        return cmd;
    }

    private Command BuildCodeIntelligence()
    {
        var cmd = new Command("code-intelligence", "Report code intelligence provider health.");
        cmd.SetHandler(async (context) =>
        {
            var ct = context.GetCancellationToken();
            var results = await codeDiagnostics.DoctorAsync(ct);
            foreach (var r in results)
            {
                var status = r.Status switch
                {
                    "ok" => "  ok",
                    "warn" => "warn",
                    "fail" => "FAIL",
                    _ => r.Status,
                };
                Console.WriteLine($"[{status}] {r.ProviderId,-20} {r.Message}");
            }
        });
        return cmd;
    }
}
