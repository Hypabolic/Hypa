using System.CommandLine;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class SearchCommand(SearchService searchService)
{
    public Command Build()
    {
        var cmd = new Command("search", "Search files, symbols, and indexed context.");
        var queryArg = new Argument<string>("query", "Search query.");
        var scopeOpt = new Option<string?>("--scope", "Scope: project, session, code, docs.");
        var kindOpt = new Option<string?>("--kind", "Search kind: text, regex, symbol.");
        var maxOpt = new Option<int?>("--max", "Maximum number of results.");
        cmd.AddArgument(queryArg);
        cmd.AddOption(scopeOpt);
        cmd.AddOption(kindOpt);
        cmd.AddOption(maxOpt);
        cmd.SetHandler(async context =>
        {
            var query = context.ParseResult.GetValueForArgument(queryArg);
            var scope = context.ParseResult.GetValueForOption(scopeOpt);
            var kind = context.ParseResult.GetValueForOption(kindOpt);
            var max = context.ParseResult.GetValueForOption(maxOpt);
            var result = await searchService.SearchAsync(query, scope, kind, max, context.GetCancellationToken());

            if (!result.IsOk)
            {
                Console.Error.WriteLine($"SUMMARY\nError: {result.Error.Message}");
                context.ExitCode = 1;
                return;
            }

            Console.Out.WriteLine(result.Value.Text);
            context.ExitCode = 0;
        });
        return cmd;
    }
}
