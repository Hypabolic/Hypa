using System.CommandLine;
using Hypa.Infrastructure.Filters;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Filters;

namespace Hypa.Cli.Commands;

public sealed class FiltersCommand(FilterService filterService, FilterSavingsEstimator savingsEstimator)
{
    public Command Build()
    {
        var cmd = new Command("filters", "Manage DSL filters for output compression.");
        cmd.AddCommand(BuildListSubcommand());
        cmd.AddCommand(BuildTestSubcommand());
        cmd.AddCommand(BuildSavingsSubcommand());
        return cmd;
    }

    private Command BuildListSubcommand()
    {
        var sub = new Command("list", "List all available filters.");
        sub.SetHandler(() =>
        {
            var filters = filterService.ListFilters();
            if (filters.Count == 0)
            {
                Console.WriteLine("No filters found.");
                return;
            }
            Console.WriteLine($"{"ID",-30} {"SCOPE",-14} APPLIES-TO");
            Console.WriteLine(new string('-', 60));
            foreach (var f in filters)
            {
                var scope = f.Scope switch
                {
                    FilterScope.BuiltIn => "built-in",
                    FilterScope.UserGlobal => "user-global",
                    FilterScope.ProjectLocal => "project-local",
                    _ => "unknown",
                };
                var appliesTo = f.AppliesTo.Count == 0 ? "(any)" : string.Join(", ", f.AppliesTo);
                Console.WriteLine($"{f.Id,-30} {scope,-14} {appliesTo}");
            }
        });
        return sub;
    }

    private Command BuildTestSubcommand()
    {
        var idArg = new Argument<string>("filter-id", "The ID of the filter to test.");
        var fileArg = new Argument<string>("file", "Path to the file to filter.");
        var sub = new Command("test", "Apply a filter to a file and print the result.");
        sub.AddArgument(idArg);
        sub.AddArgument(fileArg);
        sub.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(idArg);
            var file = context.ParseResult.GetValueForArgument(fileArg);

            if (!File.Exists(file))
            {
                await Console.Error.WriteLineAsync($"File not found: {file}");
                context.ExitCode = 1;
                return;
            }

            var text = await File.ReadAllTextAsync(file, context.GetCancellationToken());
            var result = filterService.TestFilter(id, text);
            Console.Write(result);
            if (!result.EndsWith('\n'))
                Console.WriteLine();
        });
        return sub;
    }

    private Command BuildSavingsSubcommand()
    {
        var idOpt = new Option<string?>(["--id"], "Only estimate savings for a single filter ID.")
        {
            ArgumentHelpName = "filter-id",
        };
        var minSavedOpt = new Option<int>(["--min-saved"], () => 0, "Only show filters saving at least this many estimated tokens.");
        var formatOpt = new Option<string>(["--format"], () => "table", "Output format: table or markdown.")
        {
            ArgumentHelpName = "format",
        };
        var markdownOpt = new Option<bool>(["--markdown"], "Output the savings report as a Markdown table.");
        var sub = new Command("savings", "Estimate built-in filter savings from synthetic command-output payloads.");
        sub.AddOption(idOpt);
        sub.AddOption(minSavedOpt);
        sub.AddOption(formatOpt);
        sub.AddOption(markdownOpt);

        sub.SetHandler(context =>
        {
            var id = context.ParseResult.GetValueForOption(idOpt);
            var minSaved = context.ParseResult.GetValueForOption(minSavedOpt);
            var format = context.ParseResult.GetValueForOption(markdownOpt)
                ? "markdown"
                : context.ParseResult.GetValueForOption(formatOpt);
            var filters = filterService.ListFilters()
                .Where(f => f.Scope == FilterScope.BuiltIn)
                .ToArray();

            if (!string.IsNullOrWhiteSpace(id))
                filters = filters.Where(f => f.Id == id).ToArray();

            if (filters.Length == 0)
            {
                Console.Error.WriteLine(id is null ? "No built-in filters found." : $"No built-in filter found with id '{id}'.");
                context.ExitCode = 1;
                return;
            }

            var estimates = savingsEstimator.EstimateAll(filters)
                .Where(e => e.SavedTokens >= minSaved)
                .ToArray();

            var totalOriginal = estimates.Sum(e => e.OriginalTokens);
            var totalCompressed = estimates.Sum(e => e.CompressedTokens);
            var totalSaved = Math.Max(0, totalOriginal - totalCompressed);
            var totalPercent = totalOriginal == 0
                ? 0
                : (int)Math.Round((1.0 - (double)totalCompressed / totalOriginal) * 100);

            if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format, "md", StringComparison.OrdinalIgnoreCase))
            {
                WriteMarkdownSavings(estimates, totalOriginal, totalCompressed, totalSaved, totalPercent);
            }
            else if (string.Equals(format, "table", StringComparison.OrdinalIgnoreCase))
            {
                WriteTableSavings(estimates, totalOriginal, totalCompressed, totalSaved, totalPercent);
            }
            else
            {
                Console.Error.WriteLine($"Unknown format '{format}'. Expected 'table' or 'markdown'.");
                context.ExitCode = 1;
                return;
            }

            Console.WriteLine();
            Console.WriteLine("Estimates use synthetic payloads and the configured tokenizer; validate important filters with real command output.");
        });

        return sub;
    }

    private static void WriteTableSavings(
        IReadOnlyList<FilterSavingsEstimate> estimates,
        int totalOriginal,
        int totalCompressed,
        int totalSaved,
        int totalPercent)
    {
        Console.WriteLine($"{"FILTER",-26} {"APPLIES",-22} {"ORIG",8} {"COMP",8} {"SAVED",8} {"SAVE%",6}");
        Console.WriteLine(new string('-', 86));
        foreach (var e in estimates)
            Console.WriteLine($"{e.FilterId,-26} {Trim(e.AppliesTo, 22),-22} {e.OriginalTokens,8} {e.CompressedTokens,8} {e.SavedTokens,8} {e.SavedPercent,5}%");
        Console.WriteLine(new string('-', 86));
        Console.WriteLine($"{"TOTAL",-49} {totalOriginal,8} {totalCompressed,8} {totalSaved,8} {totalPercent,5}%");
    }

    private static void WriteMarkdownSavings(
        IReadOnlyList<FilterSavingsEstimate> estimates,
        int totalOriginal,
        int totalCompressed,
        int totalSaved,
        int totalPercent)
    {
        Console.WriteLine("| Filter | Applies | Original Tokens | Compressed Tokens | Saved Tokens | Saved |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|");
        foreach (var e in estimates)
        {
            Console.WriteLine($"| {EscapeMarkdown(e.FilterId)} | {EscapeMarkdown(e.AppliesTo)} | {e.OriginalTokens} | {e.CompressedTokens} | {e.SavedTokens} | {e.SavedPercent}% |");
        }

        Console.WriteLine($"| **TOTAL** |  | **{totalOriginal}** | **{totalCompressed}** | **{totalSaved}** | **{totalPercent}%** |");
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..Math.Max(0, max - 3)] + "...";

    private static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|");
}
