using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using Hypa.Cli.Json;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Cli.Commands;

public sealed class CodeCommand(
    CodeIndexService indexService,
    IndexArtifactExportService exportService,
    CodeQueryService queryService,
    CodeDiagnosticsService diagnosticsService)
{
    public Command Build()
    {
        var cmd = new Command("code", "Index and query source code structure.");
        cmd.Add(BuildIndex());
        cmd.Add(BuildSymbols());
        cmd.Add(BuildGraph());
        cmd.Add(BuildDiagnostics());
        return cmd;
    }

    private Command BuildIndex()
    {
        var path = new Option<string?>("--path") { Description = "Path to a file or directory to index." };
        var workspace = new Option<string?>("--workspace") { Description = "Workspace root for index artifact export." };
        var db = new Option<string?>("--db") { Description = "Index database path for export mode." };
        var noDb = new Option<bool>("--no-db") { Description = "Disable index database writes in export mode." };
        var emit = new Option<string?>("--emit") { Description = "Emit index artifact: ndjson or json." };
        var quiet = new Option<bool>("--quiet") { Description = "Silence export-mode progress on stderr." };
        var include = new Option<string[]>("--include") { Description = "Include glob for export mode." };
        var exclude = new Option<string[]>("--exclude") { Description = "Exclude glob for export mode." };
        var noGitignore = new Option<bool>("--no-gitignore") { Description = "Do not respect repo .gitignore files in export mode." };
        var json = new Option<bool>("--json") { Description = "Emit JSON." };
        var full = new Option<bool>("--full") { Description = "Force a complete re-index, ignoring cached state. Invalid with --files." };
        var profile = new Option<string?>("--profile")
        {
            Description =
                "Export profile for --emit: 'full' (default) or 'code-graph' " +
                "(excludes JSON/YAML/TOML key symbols and known generated/vendored/lockfile/minified noise; " +
                "Markdown section emission is unaffected).",
        };
        var files = new Option<string?>("--files")
        {
            Description =
                "Path to a UTF-8 newline-delimited manifest of workspace-relative paths for incremental export. " +
                "Requires --emit and a persistent index db (not --no-db). Mutually exclusive with --full, --include, and --exclude.",
        };
        var parseIsolation = new Option<string?>("--parse-isolation")
        {
            Description =
                "Native parse isolation for --emit: 'on' (default, per-file worker) or 'off' (in-process). " +
                "Also controlled by HYPA_CODE_PARSE_ISOLATION.",
        };
        var cmd = new Command("index", "Index source code structure.");
        cmd.Add(path);
        cmd.Add(workspace);
        cmd.Add(db);
        cmd.Add(noDb);
        cmd.Add(emit);
        cmd.Add(quiet);
        cmd.Add(include);
        cmd.Add(exclude);
        cmd.Add(noGitignore);
        cmd.Add(json);
        cmd.Add(full);
        cmd.Add(profile);
        cmd.Add(files);
        cmd.Add(parseIsolation);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var emitFormat = parseResult.GetValue(emit);
            var filesValue = parseResult.GetValue(files);
            if (!string.IsNullOrWhiteSpace(filesValue) && string.IsNullOrWhiteSpace(emitFormat))
            {
                Console.Error.WriteLine("--files requires --emit.");
                return 2;
            }

            if (!string.IsNullOrWhiteSpace(emitFormat))
            {
                if (emitFormat is not ("ndjson" or "json"))
                {
                    Console.Error.WriteLine("--emit must be 'ndjson' or 'json'.");
                    return 2;
                }

                var workspaceValue = parseResult.GetValue(workspace);
                if (string.IsNullOrWhiteSpace(workspaceValue))
                {
                    Console.Error.WriteLine("--workspace is required when --emit is used.");
                    return 2;
                }

                var noDbValue = parseResult.GetValue(noDb);
                var dbValue = parseResult.GetValue(db);
                if (noDbValue && !string.IsNullOrWhiteSpace(dbValue))
                {
                    Console.Error.WriteLine("--db and --no-db are mutually exclusive.");
                    return 2;
                }

                var includeValue = parseResult.GetValue(include) ?? [];
                var excludeValue = parseResult.GetValue(exclude) ?? [];
                var fullValue = parseResult.GetValue(full);
                IReadOnlyList<string>? filesManifest = null;

                if (!string.IsNullOrWhiteSpace(filesValue))
                {
                    if (noDbValue)
                    {
                        Console.Error.WriteLine("--files cannot be used with --no-db.");
                        return 2;
                    }

                    if (fullValue)
                    {
                        Console.Error.WriteLine("--files cannot be used with --full.");
                        return 2;
                    }

                    if (includeValue.Length > 0 || excludeValue.Length > 0)
                    {
                        Console.Error.WriteLine("--files cannot be used with --include or --exclude.");
                        return 2;
                    }

                    try
                    {
                        filesManifest = IndexArtifactExportService.ReadFilesManifest(filesValue, workspaceValue);
                    }
                    catch (ArgumentException ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        return 2;
                    }
                }

                var profileValue = parseResult.GetValue(profile);
                IndexProfile indexProfile;
                switch (profileValue?.Trim().ToLowerInvariant())
                {
                    case null or "":
                    case "full":
                        indexProfile = IndexProfile.Full;
                        break;
                    case "code-graph":
                        indexProfile = IndexProfile.CodeGraph;
                        break;
                    default:
                        Console.Error.WriteLine("--profile must be 'full' or 'code-graph'.");
                        return 2;
                }

                var isolationFlag = parseResult.GetValue(parseIsolation);
                if (!TryResolveParseIsolation(isolationFlag, out var isolationMode, out var isolationError))
                {
                    Console.Error.WriteLine(isolationError);
                    return 2;
                }

                IndexArtifactExportResult exportResult;
                try
                {
                    exportResult = await exportService.ExportAsync(new IndexArtifactExportOptions
                    {
                        Workspace = workspaceValue,
                        PersistToDatabase = !noDbValue,
                        UseGitIgnore = !parseResult.GetValue(noGitignore),
                        IncludeGlobs = includeValue,
                        ExcludeGlobs = excludeValue,
                        BinaryVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                            ?? "0.1.0",
                        Profile = indexProfile,
                        FilesManifest = filesManifest,
                        ParseIsolationMode = isolationMode,
                    }, ct);
                }
                catch (IncrementalExportUnavailableException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 4;
                }
                catch (IndexArtifactPersistenceException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }

                // Surface otherwise-silent code-graph policy exclusions on stderr so a
                // false-positive (a legit source file wrongly dropped) is visible.
                // Respects --quiet, and never touches stdout (the artifact stream).
                var quietValue = parseResult.GetValue(quiet);
                if (!quietValue && exportResult.PolicyExcludedPaths.Count > 0)
                {
                    Console.Error.WriteLine($"code-graph: excluded {exportResult.PolicyExcludedPaths.Count} file(s) by noise policy:");
                    foreach (var excludedPath in exportResult.PolicyExcludedPaths)
                        Console.Error.WriteLine($"  excluded: {excludedPath}");
                }

                if (emitFormat == "ndjson")
                    WriteNdjson(exportResult.Artifact);
                else
                    WriteArtifactLine(JsonSerializer.Serialize(exportResult.Artifact, IndexArtifactJsonContext.Default.IndexArtifactDocument));

                // Flush the artifact before any db mutation so a crash between emit and
                // consumer ingest leaves stored state unmutated (retry re-derives the same records).
                Console.Out.Flush();
                if (exportResult.CommitPersistenceAsync is not null)
                {
                    try
                    {
                        await exportResult.CommitPersistenceAsync(ct);
                    }
                    catch (IndexArtifactPersistenceException ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        return 1;
                    }
                }

                return exportResult.IsPartial ? 3 : 0;
            }

            var p = parseResult.GetValue(path);
            var asJson = parseResult.GetValue(json);
            var asFullRebuild = parseResult.GetValue(full);
            var result = asFullRebuild
                ? await indexService.IndexFullAsync(p, ct)
                : await indexService.IndexIncrementalAsync(p, ct);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, CodeJsonContext.Default.CodeIndexResult));
                return 0;
            }

            Console.WriteLine($"Indexed {result.FilesIndexed} files, skipped {result.FilesSkipped}" +
                (result.FilesDeleted > 0 ? $", deleted {result.FilesDeleted}" : "") + ".");
            Console.WriteLine($"Symbols: {result.SymbolCount}, references: {result.ReferenceCount}, edges: {result.EdgeCount}, diagnostics: {result.DiagnosticCount}");

            return 0;
        });
        return cmd;
    }

    private static void WriteNdjson(IndexArtifactDocument artifact)
    {
        WriteArtifactLine(JsonSerializer.Serialize(artifact.Header, IndexArtifactJsonContext.Default.IndexArtifactHeader));
        foreach (var file in artifact.Files)
            WriteArtifactLine(JsonSerializer.Serialize(file, IndexArtifactJsonContext.Default.IndexFileRecord));
        WriteArtifactLine(JsonSerializer.Serialize(artifact.Footer, IndexArtifactJsonContext.Default.IndexArtifactFooter));
    }

    // Contract §4.4: LF between records and a trailing LF, regardless of platform — never Environment.NewLine.
    private static void WriteArtifactLine(string json)
    {
        Console.Out.Write(json);
        Console.Out.Write('\n');
    }

    /// <summary>
    /// Resolve parse isolation from CLI flag and <c>HYPA_CODE_PARSE_ISOLATION</c>.
    /// Unset = on. Values: on | per-file | off.
    /// </summary>
    internal static bool TryResolveParseIsolation(
        string? cliValue,
        out ParseIsolationMode mode,
        out string error)
    {
        mode = ParseIsolationMode.On;
        error = string.Empty;

        var raw = cliValue;
        if (string.IsNullOrWhiteSpace(raw))
            raw = Environment.GetEnvironmentVariable("HYPA_CODE_PARSE_ISOLATION");

        if (string.IsNullOrWhiteSpace(raw))
            return true;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "on":
            case "per-file":
                mode = ParseIsolationMode.On;
                return true;
            case "off":
                mode = ParseIsolationMode.Off;
                return true;
            default:
                error = "--parse-isolation / HYPA_CODE_PARSE_ISOLATION must be 'on', 'per-file', or 'off'.";
                return false;
        }
    }

    private Command BuildSymbols()
    {
        var query = new Option<string?>("--query") { Description = "Filter symbols by name." };
        var path = new Option<string?>("--path") { Description = "Filter symbols by indexed relative path prefix." };
        var kind = new Option<string?>("--kind") { Description = "Filter symbols by kind." };
        var json = new Option<bool>("--json") { Description = "Emit JSON." };
        var cmd = new Command("symbols", "Query indexed symbols.");
        cmd.Add(query);
        cmd.Add(path);
        cmd.Add(kind);
        cmd.Add(json);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var q = parseResult.GetValue(query);
            var p = parseResult.GetValue(path);
            var k = parseResult.GetValue(kind);
            var asJson = parseResult.GetValue(json);
            var symbols = await queryService.QuerySymbolsAsync(new CodeSymbolQuery { Query = q, Path = p, Kind = k }, ct);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(symbols, CodeJsonContext.Default.IReadOnlyListCodeSymbol));
                return 0;
            }

            foreach (var s in symbols)
                Console.WriteLine($"{s.Id} {s.Kind,-10} {s.Name,-32} {s.FilePath}:{s.Span.StartLine}:{s.Span.StartColumn}");

            return 0;
        });
        return cmd;
    }

    private Command BuildGraph()
    {
        var symbol = new Option<string?>("--symbol") { Description = "Symbol id to center the graph on." };
        var edgeKind = new Option<string?>("--edge-kind") { Description = "Filter graph edges by kind." };
        var from = new Option<string?>("--from") { Description = "Filter graph edges by source symbol id." };
        var to = new Option<string?>("--to") { Description = "Filter graph edges by target symbol id or target name." };
        var references = new Option<string?>("--references") { Description = "List syntactic reference candidates for a name." };
        var callers = new Option<string?>("--callers") { Description = "List call edges targeting a symbol id or name." };
        var callees = new Option<string?>("--callees") { Description = "List call edges emitted from a symbol id." };
        var path = new Option<string?>("--path") { Description = "Filter graph edges by relative path prefix." };
        var depth = new Option<int>("--depth") { Description = "Graph depth. MVP stores direct edges only.", DefaultValueFactory = _ => 1 };
        var json = new Option<bool>("--json") { Description = "Emit JSON." };
        var cmd = new Command("graph", "Query indexed dependency graph edges.");
        cmd.Add(symbol);
        cmd.Add(edgeKind);
        cmd.Add(from);
        cmd.Add(to);
        cmd.Add(references);
        cmd.Add(callers);
        cmd.Add(callees);
        cmd.Add(path);
        cmd.Add(depth);
        cmd.Add(json);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var s = parseResult.GetValue(symbol);
            var ek = parseResult.GetValue(edgeKind);
            var f = parseResult.GetValue(from);
            var t = parseResult.GetValue(to);
            var refs = parseResult.GetValue(references);
            var cers = parseResult.GetValue(callers);
            var cees = parseResult.GetValue(callees);
            var p = parseResult.GetValue(path);
            var d = parseResult.GetValue(depth);
            var asJson = parseResult.GetValue(json);
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
                return 0;
            }

            foreach (var edge in result.Edges)
            {
                var span = edge.SourceSpan is null ? "" : $" {edge.SourceSpan.StartLine}:{edge.SourceSpan.StartColumn}";
                var target = edge.TargetName is null ? edge.TargetId : $"{edge.TargetName} ({edge.TargetId})";
                Console.WriteLine($"{edge.Kind,-10} {edge.SourceId} -> {target} [{edge.TargetResolutionStatus}] {edge.Provenance.ProviderId}/{edge.Provenance.Confidence:0.00}{span}");
            }

            foreach (var reference in result.References)
                Console.WriteLine($"{reference.Kind,-10} {reference.FilePath}:{reference.Span.StartLine}:{reference.Span.StartColumn} -> {reference.Target} [{reference.Provenance.ProviderId}/{reference.Provenance.Confidence:0.00}]");

            return 0;
        });
        return cmd;
    }

    private Command BuildDiagnostics()
    {
        var json = new Option<bool>("--json") { Description = "Emit JSON." };
        var cmd = new Command("diagnostics", "List code intelligence diagnostics.");
        cmd.Add(json);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var asJson = parseResult.GetValue(json);
            var diagnostics = await diagnosticsService.QueryDiagnosticsAsync(ct);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(diagnostics, CodeJsonContext.Default.IReadOnlyListCodeDiagnostic));
                return 0;
            }

            if (diagnostics.Count == 0)
            {
                Console.WriteLine("No code intelligence diagnostics recorded.");
                return 0;
            }

            foreach (var d in diagnostics)
                Console.WriteLine($"{d.Severity,-7} {d.Code,-24} {d.FilePath} {d.Message}");

            return 0;
        });
        return cmd;
    }

    public Command BuildMd()
    {
        var file = new Argument<string>("file") { Description = "Relative path to the indexed Markdown file." };
        var toc = new Option<bool>("--toc") { Description = "Print the table of contents." };
        var section = new Option<string?>("--section") { Description = "Print a specific section by heading path or text." };
        var depth = new Option<int>("--depth") { Description = "Maximum heading depth for --toc.", DefaultValueFactory = _ => 3 };
        var frontmatter = new Option<bool>("--frontmatter") { Description = "Print frontmatter." };
        var json = new Option<bool>("--json") { Description = "Emit JSON." };
        var cmd = new Command("md", "Query indexed Markdown structure.");
        cmd.Add(file);
        cmd.Add(toc);
        cmd.Add(section);
        cmd.Add(depth);
        cmd.Add(frontmatter);
        cmd.Add(json);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var filePath = parseResult.GetValue(file)!;
            var absolutePath = Path.GetFullPath(filePath);
            await indexService.EnsureFreshAsync(absolutePath, ct);
            var printToc = parseResult.GetValue(toc);
            var sectionValue = parseResult.GetValue(section);
            var maxDepth = parseResult.GetValue(depth);
            var printFrontmatter = parseResult.GetValue(frontmatter);
            var asJson = parseResult.GetValue(json);
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

            return 0;
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
