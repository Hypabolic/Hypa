using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Hypa.Runtime.Application;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Sdk.CodeIntelligence;
using Microsoft.Extensions.Logging;

namespace Hypa.Runtime.Application.Services;

public sealed record SearchOutput
{
    public string Text { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public int MaxResults { get; init; }
    public long DurationMs { get; init; }
}

public sealed class SearchService(
    IFileSystem fileSystem,
    IProjectRootDetector projectRootDetector,
    ICodeIndexRepository codeIndexRepository,
    IEvidenceLedger evidenceLedger,
    ISessionResolver sessionResolver,
    ILogger<SearchService> logger)
{
    public async Task<Result<SearchOutput, Error>> SearchAsync(
        string query,
        string? scope,
        string? kind,
        int? maxResults,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(query))
            return Result<SearchOutput, Error>.Fail(new Error("QUERY_REQUIRED", "query is required."));

        var effectiveKind = (kind ?? "text").ToLowerInvariant();
        var effectiveScope = (scope ?? "project").ToLowerInvariant();
        var effectiveMax = maxResults ?? 20;
        var scopeResult = ValidateScope(effectiveScope);
        if (!scopeResult.IsOk)
            return Result<SearchOutput, Error>.Fail(scopeResult.Error);

        var kindResult = ValidateKind(effectiveKind);
        if (!kindResult.IsOk)
            return Result<SearchOutput, Error>.Fail(kindResult.Error);

        var result = effectiveKind == "symbol"
            ? await SearchSymbolsAsync(query, effectiveMax, cancellationToken)
            : SearchText(query, effectiveKind, effectiveScope, effectiveMax);

        if (!result.IsOk)
        {
            await RecordEvidenceAsync(query, scope, effectiveKind, effectiveMax, result.Error.Message, sw.ElapsedMilliseconds, cancellationToken);
            return Result<SearchOutput, Error>.Fail(result.Error);
        }

        var output = new SearchOutput
        {
            Text = result.Value,
            Kind = effectiveKind,
            MaxResults = effectiveMax,
            DurationMs = sw.ElapsedMilliseconds,
        };

        await RecordEvidenceAsync(query, scope, effectiveKind, effectiveMax, output.Text, sw.ElapsedMilliseconds, cancellationToken);
        return Result<SearchOutput, Error>.Ok(output);
    }

    private async Task<Result<string, Error>> SearchSymbolsAsync(string query, int maxResults, CancellationToken ct)
    {
        var symbols = await codeIndexRepository.QuerySymbolsAsync(new CodeSymbolQuery { Query = query }, ct);

        if (symbols.Count == 0)
            return Result<string, Error>.Ok($"SUMMARY\nNo symbols matching '{query}'.");

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Found {symbols.Count} symbol(s) matching '{query}'.");
        sb.AppendLine();
        sb.AppendLine("REFERENCES");
        foreach (var symbol in symbols.Take(maxResults))
            sb.AppendLine($"  {symbol.Kind} {symbol.Name} in {symbol.FilePath}:{symbol.Span.StartLine}");

        return Result<string, Error>.Ok(sb.ToString().TrimEnd());
    }

    private Result<string, Error> SearchText(string query, string kind, string scope, int maxResults)
    {
        var projectRoot = ResolveProjectRoot();
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
                return Result<string, Error>.Fail(new Error("INVALID_REGEX", $"invalid regex: {ex.Message}"));
            }
        }

        var searchableFiles = files
            .Select(Path.GetFullPath)
            .Where(file => PathJail.IsWithinRoot(file, projectRoot))
            .Where(file => !IsExcluded(file))
            .Where(file => IsIncludedByScope(file, scope))
            .ToArray();
        var searchedFiles = searchableFiles.Take(1000).ToArray();
        var limitReached = searchableFiles.Length > searchedFiles.Length;

        foreach (var resolvedFile in searchedFiles)
        {
            try
            {
                var content = Encoding.UTF8.GetString(fileSystem.ReadAllBytes(resolvedFile));
                var lines = content.Split('\n');

                for (var i = 0; i < lines.Length && matches.Count < maxResults; i++)
                {
                    var line = lines[i];
                    var matched = regex is not null
                        ? regex.IsMatch(line)
                        : line.Contains(query, StringComparison.OrdinalIgnoreCase);

                    if (matched)
                        matches.Add((Path.GetRelativePath(projectRoot, resolvedFile), i + 1, line.Trim()));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }

            if (matches.Count >= maxResults)
                break;
        }

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        if (matches.Count == 0)
            sb.AppendLine($"No matches for '{query}' ({kind}).");
        else
            sb.AppendLine($"Found {matches.Count} match(es) for '{query}'.");
        if (limitReached)
            sb.AppendLine($"Searched {searchedFiles.Length} of {searchableFiles.Length} files (limit reached). Results may be incomplete.");
        if (matches.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("REFERENCES");
            foreach (var (filePath, lineNum, lineText) in matches)
                sb.AppendLine($"  {filePath}:{lineNum}: {lineText}");
        }

        return Result<string, Error>.Ok(sb.ToString().TrimEnd());
    }

    private async Task RecordEvidenceAsync(
        string query,
        string? scope,
        string kind,
        int maxResults,
        string resultText,
        long durationMs,
        CancellationToken ct)
    {
        var args = CapabilityToolEvidence.BuildArgsJson(
            ("query", query),
            ("kind", kind),
            ("scope", scope),
            ("maxResults", maxResults.ToString()));
        await CapabilityToolEvidence.RecordAsync(
            evidenceLedger,
            sessionResolver,
            logger,
            "hypa_search",
            args,
            resultText,
            durationMs,
            ct);
    }

    private string ResolveProjectRoot()
    {
        var currentDirectory = fileSystem.GetCurrentDirectory();
        return Path.GetFullPath(projectRootDetector.Detect(currentDirectory) ?? currentDirectory);
    }

    private static bool IsExcluded(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var separator = Path.DirectorySeparatorChar;
        var name = Path.GetFileName(path);
        return name.StartsWith('.') ||
               normalized.Contains($"{separator}obj{separator}") ||
               normalized.Contains($"{separator}bin{separator}") ||
               normalized.Contains($"{separator}.git{separator}") ||
               normalized.Contains($"{separator}node_modules{separator}");
    }

    private static bool IsIncludedByScope(string path, string scope) =>
        scope switch
        {
            "docs" => IsDocumentationFile(path),
            "session" or "project" => true,
            "code" => IsCodeFile(path),
            _ => false,
        };

    private static bool IsDocumentationFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".txt" or ".rst" or ".adoc";

    private static bool IsCodeFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".rs" or ".go";

    private static Result<string, Error> ValidateScope(string scope) =>
        scope is "project" or "session" or "code" or "docs"
            ? Result<string, Error>.Ok(scope)
            : Result<string, Error>.Fail(new Error("INVALID_SCOPE", $"unsupported search scope: {scope}"));

    private static Result<string, Error> ValidateKind(string kind) =>
        kind is "text" or "regex" or "symbol"
            ? Result<string, Error>.Ok(kind)
            : Result<string, Error>.Fail(new Error("INVALID_KIND", $"unsupported search kind: {kind}"));
}
