using System.CommandLine;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class TrustCommand(TrustService trustService)
{
    public Command Build()
    {
        var cmd = new Command("trust", "Manage trust for project-local filters.");
        cmd.Add(BuildFiltersSubcommand());
        cmd.Add(BuildStatusSubcommand());
        return cmd;
    }

    private Command BuildFiltersSubcommand()
    {
        var sub = new Command("filters", "Grant trust to .hypa/filters/ in the current project.");
        sub.SetAction(async (parseResult, ct) =>
        {
            var message = await trustService.GrantFiltersAsync(ct);
            Console.WriteLine(message);

            return 0;
        });
        return sub;
    }

    private Command BuildStatusSubcommand()
    {
        var sub = new Command("status", "List all trust records.");
        sub.SetAction(async (parseResult, ct) =>
        {
            var records = await trustService.GetStatusAsync(ct);
            if (records.Count == 0)
            {
                Console.WriteLine("No trust records found.");
                return 0;
            }
            Console.WriteLine($"{"PROJECT ROOT",-40} {"FILE",-35} {"GRANTED AT",-25} HASH (first 8)");
            Console.WriteLine(new string('-', 115));
            foreach (var r in records)
            {
                var shortHash = r.FileHash.Length >= 8 ? r.FileHash[..8] : r.FileHash;
                Console.WriteLine($"{Truncate(r.ProjectRoot, 38),-40} {Truncate(Path.GetFileName(r.FilterFilePath), 33),-35} {r.GrantedAt:yyyy-MM-dd HH:mm}           {shortHash}");
            }

            return 0;
        });
        return sub;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : "…" + s[^(max - 1)..];
}
