using System.CommandLine;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class SearchCommand(SearchService searchService)
{
    public Command Build()
    {
        var cmd = new Command("search", "Search files, symbols, and indexed context.");
        var queryArg = new Argument<string>("query") { Description = "Search query." };
        var scopeOpt = new Option<string?>("--scope") { Description = "Scope: project, session, code, docs." };
        var kindOpt = new Option<string?>("--kind") { Description = "Search kind: text, regex, symbol." };
        var maxOpt = new Option<int?>("--max") { Description = "Maximum number of results." };
        cmd.Add(queryArg);
        cmd.Add(scopeOpt);
        cmd.Add(kindOpt);
        cmd.Add(maxOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var query = parseResult.GetValue(queryArg)!;
            var scope = parseResult.GetValue(scopeOpt);
            var kind = parseResult.GetValue(kindOpt);
            var max = parseResult.GetValue(maxOpt);
            var result = await searchService.SearchAsync(query, scope, kind, max, ct);

            if (!result.IsOk)
            {
                Console.Error.WriteLine($"SUMMARY\nError: {result.Error.Message}");
                return 1;
            }

            Console.Out.WriteLine(result.Value.Text);
            return 0;
        });
        return cmd;
    }
}
