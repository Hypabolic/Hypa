using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Sessions;
using Hypa.Sdk.CodeIntelligence;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Mcp.Tools;

[McpServerToolType]
public sealed class HypaSearchTool
{
    [McpServerTool(Name = "hypa_search"), Description("Search files, symbols, and indexed context. Kinds: text, regex, symbol.")]
    public static async Task<CallToolResult> ExecuteAsync(
        IFileSystem fileSystem,
        IProjectRootDetector projectRootDetector,
        ICodeIndexRepository codeIndexRepository,
        IEvidenceLedger evidenceLedger,
        ISessionResolver sessionResolver,
        ILogger<HypaSearchTool> logger,
        CancellationToken cancellationToken,
        [Description("Search query")] string query,
        [Description("Scope: project | session | code | docs")] string? scope = null,
        [Description("Search kind: text | regex | symbol (default: text)")] string? kind = null,
        [Description("Maximum number of results (default: 20)")] int? maxResults = null)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(query))
            return McpToolResult.Err("SUMMARY\nError: query is required.");

        var effectiveKind = (kind ?? "text").ToLowerInvariant();
        var effectiveMax = maxResults ?? 20;

        CallToolResult toolResult;
        if (effectiveKind == "symbol")
            toolResult = await SearchSymbolsAsync(query, effectiveMax, codeIndexRepository, cancellationToken);
        else
            toolResult = await SearchTextAsync(query, effectiveKind, effectiveMax, fileSystem, projectRootDetector);
        var resultText = McpToolResult.TextOf(toolResult);

        var args = McpToolResult.BuildArgsJson(
            ("query", query), ("kind", effectiveKind),
            ("scope", scope), ("maxResults", (maxResults ?? 20).ToString()));
        var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), cancellationToken);
        if (!sessionResult.IsOk)
            logger.LogWarning("session not resolved, recording with empty ID: {Error}", sessionResult.Error.Message);
        await evidenceLedger.RecordToolCallAsync(new ToolCallRecord
        {
            SessionId = sessionResult.IsOk ? sessionResult.Value.Id : Guid.Empty,
            ToolName = "hypa_search",
            Args = args,
            ArgsHash = HashString(args),
            Result = resultText[..Math.Min(200, resultText.Length)],
            OutputHash = HashString(resultText),
            DurationMs = sw.ElapsedMilliseconds
        }, cancellationToken);

        return toolResult;
    }

    private static async Task<CallToolResult> SearchSymbolsAsync(
        string query, int maxResults, ICodeIndexRepository codeIndexRepository, CancellationToken ct)
    {
        var symbols = await codeIndexRepository.QuerySymbolsAsync(new CodeSymbolQuery { Query = query }, ct);

        if (symbols.Count == 0)
            return McpToolResult.Ok($"SUMMARY\nNo symbols matching '{query}'.");

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Found {symbols.Count} symbol(s) matching '{query}'.");
        sb.AppendLine();
        sb.AppendLine("REFERENCES");
        foreach (var sym in symbols.Take(maxResults))
            sb.AppendLine($"  {sym.Kind} {sym.Name} in {sym.FilePath}:{sym.Span.StartLine}");

        return McpToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static Task<CallToolResult> SearchTextAsync(
        string query, string kind, int maxResults, IFileSystem fileSystem, IProjectRootDetector projectRootDetector)
    {
        var projectRoot = projectRootDetector.Detect(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory();
        var files = fileSystem.GetFiles(projectRoot, "*.*", recursive: true);
        var matches = new List<(string File, int Line, string Text)>();

        Regex? regex = null;
        if (kind == "regex")
        {
            try
            {
                regex = new Regex(query, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(McpToolResult.Err($"SUMMARY\nError: invalid regex: {ex.Message}"));
            }
        }

        var normalizedRoot = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var file in files.Take(1000))
        {
            if (!file.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                !file.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsExcluded(file)) continue;

            try
            {
                var content = Encoding.UTF8.GetString(fileSystem.ReadAllBytes(file));
                var lines = content.Split('\n');

                for (var i = 0; i < lines.Length && matches.Count < maxResults; i++)
                {
                    var line = lines[i];
                    var matched = regex is not null
                        ? regex.IsMatch(line)
                        : line.Contains(query, StringComparison.OrdinalIgnoreCase);

                    if (matched)
                        matches.Add((Path.GetRelativePath(projectRoot, file), i + 1, line.Trim()));
                }
            }
            catch (Exception) { }

            if (matches.Count >= maxResults) break;
        }

        if (matches.Count == 0)
            return Task.FromResult(McpToolResult.Ok($"SUMMARY\nNo matches for '{query}' ({kind})."));

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Found {matches.Count} match(es) for '{query}'.");
        sb.AppendLine();
        sb.AppendLine("REFERENCES");
        foreach (var (filePath, lineNum, lineText) in matches)
            sb.AppendLine($"  {filePath}:{lineNum}: {lineText}");

        return Task.FromResult(McpToolResult.Ok(sb.ToString().TrimEnd()));
    }

    private static string HashString(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static bool IsExcluded(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.') || path.Contains("/obj/") || path.Contains("/bin/") ||
               path.Contains("/.git/") || path.Contains("/node_modules/");
    }
}
