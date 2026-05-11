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
        var cmd = new Command("index", "Index source code structure.");
        cmd.AddOption(path);
        cmd.AddOption(json);
        cmd.SetHandler(async (context) =>
        {
            var ct = context.GetCancellationToken();
            var p = context.ParseResult.GetValueForOption(path);
            var asJson = context.ParseResult.GetValueForOption(json);
            var result = await indexService.IndexAsync(p, ct);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, CodeJsonContext.Default.CodeIndexResult));
                return;
            }

            Console.WriteLine($"Indexed {result.FilesIndexed} files, skipped {result.FilesSkipped}.");
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
}
