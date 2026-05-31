using System.CommandLine;
using System.Text.Json;
using Hypa.Cli.Json;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Cli.Commands;

public sealed class CodeCommand(
    CodeIndexService indexService,
    CodeQueryService queryService,
    CodeDiagnosticsService diagnosticsService)
{
    public Command Build()
    {
        var cmd = new Command("code", "Index and query source code structure.");
        cmd.AddCommand(BuildIndex());
        cmd.AddCommand(BuildSymbols());
        cmd.AddCommand(BuildGraph());
        cmd.AddCommand(BuildDiagnostics());
        return cmd;
    }

    private Command BuildIndex()
    {
        var path = new Option<string?>("--path", "Path to a file or directory to index.");
        var json = new Option<bool>("--json", "Emit JSON.");
        var full = new Option<bool>("--full", "Force a complete re-index, ignoring cached state.");
        var cmd = new Command("index", "Index source code structure.");
        cmd.AddOption(path);
        cmd.AddOption(json);
        cmd.AddOption(full);
        cmd.SetHandler(async (context) =>
        {
            var ct = context.GetCancellationToken();
            var p = context.ParseResult.GetValueForOption(path);
            var asJson = context.ParseResult.GetValueForOption(json);
            var asFullRebuild = context.ParseResult.GetValueForOption(full);
            var result = asFullRebuild
                ? await indexService.IndexFullAsync(p, ct)
                : await indexService.IndexIncrementalAsync(p, ct);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, CodeJsonContext.Default.CodeIndexResult));
                return;
            }

            Console.WriteLine($"Indexed {result.FilesIndexed} files, skipped {result.FilesSkipped}" +
                (result.FilesDeleted > 0 ? $", deleted {result.FilesDeleted}" : "") + ".");
            Console.WriteLine($"Symbols: {result.SymbolCount}, references: {result.ReferenceCount}, edges: {result.EdgeCount}, diagnostics: {result.DiagnosticCount}");
        });
        return cmd;
    }

    private Command BuildSymbols()
    {
        var query = new Option<string?>("--query", "Filter symbols by name.");
        var path = new Option<string?>("--path", "Filter symbols by indexed relative path prefix.");
        var kind = new Option<string?>("--kind", "Filter symbols by kind.");
        var json = new Option<bool>("--json", "Emit JSON.");
        var cmd = new Command("symbols", "Query indexed symbols.");
        cmd.AddOption(query);
        cmd.AddOption(path);
        cmd.AddOption(kind);
        cmd.AddOption(json);
        cmd.SetHandler(async (context) =>
        {
            var ct = context.GetCancellationToken();
            var q = context.ParseResult.GetValueForOption(query);
            var p = context.ParseResult.GetValueForOption(path);
            var k = context.ParseResult.GetValueForOption(kind);
            var asJson = context.ParseResult.GetValueForOption(json);
            var symbols = await queryService.QuerySymbolsAsync(new CodeSymbolQuery { Query = q, Path = p, Kind = k }, ct);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(symbols, CodeJsonContext.Default.IReadOnlyListCodeSymbol));
                return;
            }

            foreach (var s in symbols)
                Console.WriteLine($"{s.Id} {s.Kind,-10} {s.Name,-32} {s.FilePath}:{s.Span.StartLine}:{s.Span.StartColumn}");
        });
        return cmd;
    }

    private Command BuildGraph()
    {
        var symbol = new Option<string?>("--symbol", "Symbol id to center the graph on.");
        var edgeKind = new Option<string?>("--edge-kind", "Filter graph edges by kind.");
        var from = new Option<string?>("--from", "Filter graph edges by source symbol id.");
        var to = new Option<string?>("--to", "Filter graph edges by target symbol id or target name.");
        var references = new Option<string?>("--references", "List syntactic reference candidates for a name.");
        var callers = new Option<string?>("--callers", "List call edges targeting a symbol id or name.");
        var callees = new Option<string?>("--callees", "List call edges emitted from a symbol id.");
        var path = new Option<string?>("--path", "Filter graph edges by relative path prefix.");
        var depth = new Option<int>("--depth", () => 1, "Graph depth. MVP stores direct edges only.");
        var json = new Option<bool>("--json", "Emit JSON.");
        var cmd = new Command("graph", "Query indexed dependency graph edges.");
        cmd.AddOption(symbol);
        cmd.AddOption(edgeKind);
        cmd.AddOption(from);
        cmd.AddOption(to);
        cmd.AddOption(references);
        cmd.AddOption(callers);
        cmd.AddOption(callees);
        cmd.AddOption(path);
        cmd.AddOption(depth);
        cmd.AddOption(json);
        cmd.SetHandler(async (context) =>
        {
            var ct = context.GetCancellationToken();
            var s = context.ParseResult.GetValueForOption(symbol);
            var ek = context.ParseResult.GetValueForOption(edgeKind);
            var f = context.ParseResult.GetValueForOption(from);
            var t = context.ParseResult.GetValueForOption(to);
            var refs = context.ParseResult.GetValueForOption(references);
            var cers = context.ParseResult.GetValueForOption(callers);
            var cees = context.ParseResult.GetValueForOption(callees);
            var p = context.ParseResult.GetValueForOption(path);
            var d = context.ParseResult.GetValueForOption(depth);
            var asJson = context.ParseResult.GetValueForOption(json);
            var result = await queryService.QueryGraphAsync(new CodeGraphQuery
            {
                SymbolId = s,
                Path = p,
                Depth = d,
                EdgeKind = ek,
                From = f,
                To = t,
                References = refs,
                Callers = cers,
                Callees = cees,
            }, ct);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, CodeJsonContext.Default.CodeGraphResult));
                return;
            }

            foreach (var edge in result.Edges)
            {
                var span = edge.SourceSpan is null ? "" : $" {edge.SourceSpan.StartLine}:{edge.SourceSpan.StartColumn}";
                var target = edge.TargetName is null ? edge.TargetId : $"{edge.TargetName} ({edge.TargetId})";
                Console.WriteLine($"{edge.Kind,-10} {edge.SourceId} -> {target} [{edge.TargetResolutionStatus}] {edge.Provenance.ProviderId}/{edge.Provenance.Confidence:0.00}{span}");
            }

            foreach (var reference in result.References)
                Console.WriteLine($"{reference.Kind,-10} {reference.FilePath}:{reference.Span.StartLine}:{reference.Span.StartColumn} -> {reference.Target} [{reference.Provenance.ProviderId}/{reference.Provenance.Confidence:0.00}]");
        });
        return cmd;
    }

    private Command BuildDiagnostics()
    {
        var json = new Option<bool>("--json", "Emit JSON.");
        var cmd = new Command("diagnostics", "List code intelligence diagnostics.");
        cmd.AddOption(json);
        cmd.SetHandler(async (context) =>
        {
            var ct = context.GetCancellationToken();
            var asJson = context.ParseResult.GetValueForOption(json);
            var diagnostics = await diagnosticsService.QueryDiagnosticsAsync(ct);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(diagnostics, CodeJsonContext.Default.IReadOnlyListCodeDiagnostic));
                return;
            }

            if (diagnostics.Count == 0)
            {
                Console.WriteLine("No code intelligence diagnostics recorded.");
                return;
            }

            foreach (var d in diagnostics)
                Console.WriteLine($"{d.Severity,-7} {d.Code,-24} {d.FilePath} {d.Message}");
        });
        return cmd;
    }

    public Command BuildMd()
    {
        var file = new Argument<string>("file", "Relative path to the indexed Markdown file.");
        var toc = new Option<bool>("--toc", "Print the table of contents.");
        var section = new Option<string?>("--section", "Print a specific section by heading path or text.");
        var depth = new Option<int>("--depth", () => 3, "Maximum heading depth for --toc.");
        var frontmatter = new Option<bool>("--frontmatter", "Print frontmatter.");
        var json = new Option<bool>("--json", "Emit JSON.");
        var cmd = new Command("md", "Query indexed Markdown structure.");
        cmd.AddArgument(file);
        cmd.AddOption(toc);
        cmd.AddOption(section);
        cmd.AddOption(depth);
        cmd.AddOption(frontmatter);
        cmd.AddOption(json);
        cmd.SetHandler(async (context) =>
        {
            var ct = context.GetCancellationToken();
            var filePath = context.ParseResult.GetValueForArgument(file);
            var absolutePath = Path.GetFullPath(filePath);
            await indexService.EnsureFreshAsync(absolutePath, ct);
            var printToc = context.ParseResult.GetValueForOption(toc);
            var sectionValue = context.ParseResult.GetValueForOption(section);
            var maxDepth = context.ParseResult.GetValueForOption(depth);
            var printFrontmatter = context.ParseResult.GetValueForOption(frontmatter);
            var asJson = context.ParseResult.GetValueForOption(json);
            var printSection = sectionValue is not null;

            if (!printFrontmatter && !printToc && !printSection)
                printToc = true;

            string? frontmatterResult = null;
            IReadOnlyList<MarkdownSection>? tocResult = null;
            IReadOnlyList<MarkdownSection>? sectionResult = null;

            if (printFrontmatter)
            {
                frontmatterResult = await queryService.QueryFrontmatterAsync(filePath, ct);
                if (!asJson)
                    Console.WriteLine(frontmatterResult ?? "(no frontmatter)");
            }

            if (printToc)
            {
                tocResult = await queryService.QueryTocAsync(filePath, maxDepth, ct);
                if (!asJson)
                {
                    foreach (var s in tocResult)
                        Console.WriteLine($"{new string(' ', (s.HeadingLevel - 1) * 2)}{s.HeadingText}");
                }
            }

            if (printSection)
            {
                var sections = await queryService.QueryMarkdownSectionsAsync(filePath, ct);
                sectionResult = sections
                    .Where(s => s.HeadingPath == sectionValue || s.HeadingText == sectionValue)
                    .ToArray();
                if (!asJson)
                {
                    if (sectionResult.Count == 0)
                    {
                        Console.WriteLine($"No Markdown section matched '{sectionValue}'.");
                    }

                    foreach (var s in sectionResult)
                    {
                        Console.WriteLine($"{s.HeadingPath} (L{s.StartLine}-{s.EndLine})");
                        Console.WriteLine();
                        Console.WriteLine(s.PlainText ?? s.Text ?? "(no content)");
                    }
                }
            }

            if (asJson)
            {
                var result = new MarkdownQueryJsonResult
                {
                    FilePath = filePath,
                    Frontmatter = printFrontmatter ? frontmatterResult : null,
                    Toc = printToc ? tocResult ?? [] : null,
                    Section = printSection ? sectionValue : null,
                    Sections = printSection ? sectionResult ?? [] : null,
                    SectionMatched = printSection ? sectionResult?.Count > 0 : null,
                };
                Console.WriteLine(JsonSerializer.Serialize(result, CodeJsonContext.Default.MarkdownQueryJsonResult));
            }
        });
        return cmd;
    }
}

internal sealed record MarkdownQueryJsonResult
{
    public required string FilePath { get; init; }
    public string? Frontmatter { get; init; }
    public IReadOnlyList<MarkdownSection>? Toc { get; init; }
    public string? Section { get; init; }
    public IReadOnlyList<MarkdownSection>? Sections { get; init; }
    public bool? SectionMatched { get; init; }
}
