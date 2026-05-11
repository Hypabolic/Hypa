using System.CommandLine;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Parsers;

namespace Hypa.Cli.Commands;

public sealed class ParseHealthCommand(ParseHealthService parseHealthService)
{
    public Command Build()
    {
        var cmd = new Command("parse-health", "Report parse tier distribution from recent runs.");
        cmd.SetHandler(async context =>
        {
            var ct = context.GetCancellationToken();
            var rows = await parseHealthService.GetReportAsync(ct);
            if (rows.Count == 0)
            {
                Console.WriteLine("No parse metrics recorded yet. Run some commands first.");
                return;
            }
            Console.WriteLine($"{"EXECUTABLE",-20} {"TIER",-14} {"COUNT",6}  {"PCT",6}");
            Console.WriteLine(new string('-', 52));
            foreach (var row in rows)
            {
                var tier = row.Tier switch
                {
                    ParseTier.Full => "Full",
                    ParseTier.Degraded => "Degraded",
                    ParseTier.Passthrough => "Passthrough",
                    _ => "Unknown",
                };
                Console.WriteLine($"{row.Executable,-20} {tier,-14} {row.Count,6}  {row.Pct,5:F1}%");
            }
        });
        return cmd;
    }
}
