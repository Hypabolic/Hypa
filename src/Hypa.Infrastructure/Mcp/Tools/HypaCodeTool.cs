using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Sessions;
using Hypa.Sdk.CodeIntelligence;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Mcp.Tools;

[McpServerToolType]
public sealed class HypaCodeTool
{
    private static readonly IReadOnlySet<string> MutatingActions =
        new HashSet<string>(StringComparer.Ordinal) { "index" };

    [McpServerTool(Name = "hypa_code"), Description("Code intelligence queries: index, symbols, references, graph, diagnostics.")]
    public static async Task<CallToolResult> ExecuteAsync(
        IFileSystem fileSystem,
        IProjectRootDetector projectRootDetector,
        CodeStructureProviderRegistry providerRegistry,
        ICodeIndexRepository codeIndexRepository,
        IEvidenceLedger evidenceLedger,
        ISessionResolver sessionResolver,
        ILogger<HypaCodeTool> logger,
        McpRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken,
        [Description("Action: index | symbols | references | graph | diagnostics")] string action = "symbols",
        [Description("File path (for index or symbol queries)")] string? path = null,
        [Description("Symbol name or ID")] string? symbol = null)
    {
        var sw = Stopwatch.StartNew();

        if (runtimeOptions.ReadOnly && MutatingActions.Contains(action))
            return McpToolResult.Err($"SUMMARY\nRead-only mode: action '{action}' is not permitted.");

        var toolResult = await DispatchAsync(action, path, symbol, fileSystem, projectRootDetector, providerRegistry, codeIndexRepository, cancellationToken);
        var resultText = McpToolResult.TextOf(toolResult);

        var args = McpToolResult.BuildArgsJson(("action", action), ("path", path), ("symbol", symbol));
        var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), cancellationToken);
        if (!sessionResult.IsOk)
            logger.LogWarning("session not resolved, recording with empty ID: {Error}", sessionResult.Error.Message);
        await evidenceLedger.RecordToolCallAsync(new ToolCallRecord
        {
            SessionId = sessionResult.IsOk ? sessionResult.Value.Id : Guid.Empty,
            ToolName = "hypa_code",
            Args = args,
            ArgsHash = HashString(args),
            Result = resultText[..Math.Min(200, resultText.Length)],
            OutputHash = HashString(resultText),
            DurationMs = sw.ElapsedMilliseconds
        }, cancellationToken);

        return toolResult;
    }

    private static async Task<CallToolResult> DispatchAsync(
        string action, string? path, string? symbol,
        IFileSystem fileSystem, IProjectRootDetector projectRootDetector,
        CodeStructureProviderRegistry providerRegistry, ICodeIndexRepository codeIndexRepository,
        CancellationToken ct) =>
        action switch
        {
            "symbols" => await SymbolsAsync(path, symbol, codeIndexRepository, ct),
            "references" => await ReferencesAsync(path, symbol, codeIndexRepository, ct),
            "graph" => await GraphAsync(path, symbol, codeIndexRepository, ct),
            "diagnostics" => await DiagnosticsAsync(codeIndexRepository, ct),
            "index" => await IndexAsync(path, fileSystem, projectRootDetector, providerRegistry, codeIndexRepository, ct),
            _ => McpToolResult.Err($"SUMMARY\nUnknown action '{action}'. Valid actions: index, symbols, references, graph, diagnostics.")
        };

    private static async Task<CallToolResult> SymbolsAsync(string? path, string? symbol, ICodeIndexRepository repo, CancellationToken ct)
    {
        var symbols = await repo.QuerySymbolsAsync(new CodeSymbolQuery { Path = path, Query = symbol }, ct);
        if (symbols.Count == 0)
            return McpToolResult.Ok("SUMMARY\nNo symbols found.");

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Found {symbols.Count} symbol(s).");
        sb.AppendLine();
        sb.AppendLine("REFERENCES");
        foreach (var sym in symbols)
            sb.AppendLine($"  {sym.Kind} {sym.Name} in {sym.FilePath}:{sym.Span.StartLine}");

        return McpToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static async Task<CallToolResult> ReferencesAsync(string? path, string? symbol, ICodeIndexRepository repo, CancellationToken ct)
    {
        var graph = await repo.QueryGraphAsync(new CodeGraphQuery { SymbolId = symbol, Path = path }, ct);
        if (graph.References.Count == 0)
            return McpToolResult.Ok($"SUMMARY\nNo references found for '{symbol}'.");

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Found {graph.References.Count} reference(s).");
        sb.AppendLine();
        sb.AppendLine("REFERENCES");
        foreach (var r in graph.References)
            sb.AppendLine($"  {r.Kind} {r.Target} in {r.FilePath}:{r.Span.StartLine}");

        return McpToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static async Task<CallToolResult> GraphAsync(string? path, string? symbol, ICodeIndexRepository repo, CancellationToken ct)
    {
        var graph = await repo.QueryGraphAsync(new CodeGraphQuery { SymbolId = symbol, Path = path }, ct);
        return McpToolResult.Ok($"SUMMARY\nGraph: {graph.Symbols.Count} symbol(s), {graph.Edges.Count} edge(s), {graph.References.Count} reference(s).");
    }

    private static async Task<CallToolResult> DiagnosticsAsync(ICodeIndexRepository repo, CancellationToken ct)
    {
        var diagnostics = await repo.QueryDiagnosticsAsync(ct);
        if (diagnostics.Count == 0)
            return McpToolResult.Ok("SUMMARY\nNo diagnostics.");

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Found {diagnostics.Count} diagnostic(s).");
        sb.AppendLine();
        sb.AppendLine("DETAILS");
        foreach (var d in diagnostics.Take(50))
            sb.AppendLine($"  [{d.Severity}] {d.FilePath}:{d.Span?.StartLine} {d.Code} {d.Message}");

        return McpToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static async Task<CallToolResult> IndexAsync(
        string? path, IFileSystem fileSystem, IProjectRootDetector projectRootDetector,
        CodeStructureProviderRegistry providerRegistry, ICodeIndexRepository repo, CancellationToken ct)
    {
        var projectRoot = projectRootDetector.Detect(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory();
        var searchRoot = path is not null ? Path.GetFullPath(Path.Combine(projectRoot, path)) : projectRoot;

        if (!IsWithinRoot(searchRoot, projectRoot))
            return McpToolResult.Err("SUMMARY\nError: path escapes project root.");

        var files = fileSystem.GetFiles(searchRoot, "*.cs", recursive: true)
            .Concat(fileSystem.GetFiles(searchRoot, "*.ts", recursive: true))
            .Concat(fileSystem.GetFiles(searchRoot, "*.js", recursive: true))
            .Where(f => !IsExcluded(f))
            .Take(500)
            .ToList();

        var documents = new List<CodeStructureDocument>();
        foreach (var file in files)
        {
            var lang = DetectLanguage(file);
            var provider = providerRegistry.Select(lang);
            var bytes = fileSystem.ReadAllBytes(file);
            var content = Encoding.UTF8.GetString(bytes);
            var fileId = new CodeFileIdentity
            {
                ProjectRoot = projectRoot,
                Path = file,
                RelativePath = Path.GetRelativePath(projectRoot, file),
                Language = lang,
                ContentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..8]
            };
            documents.Add(await provider.ParseAsync(fileId, content, ct));
        }

        await repo.SaveDocumentsAsync(documents, ct);
        return McpToolResult.Ok($"SUMMARY\nIndexed {documents.Count} file(s) under '{searchRoot}'.\n\nSTATS\nfiles={documents.Count}");
    }

    private static string HashString(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static bool IsExcluded(string path) =>
        path.Contains("/obj/") || path.Contains("/bin/") || path.Contains("/.git/") || path.Contains("/node_modules/");

    private static bool IsWithinRoot(string resolvedPath, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return resolvedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || resolvedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectLanguage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".rs" => "rust",
            ".go" => "go",
            _ => "text"
        };
}
