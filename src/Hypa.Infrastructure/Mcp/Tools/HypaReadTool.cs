using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
public sealed class HypaReadTool
{
    [McpServerTool(Name = "hypa_read"), Description("Read files in context-aware modes: full, outline, signatures, pruned, smart.")]
    public static async Task<CallToolResult> ExecuteAsync(
        IFileSystem fileSystem,
        IProjectRootDetector projectRootDetector,
        CodeStructureProviderRegistry providerRegistry,
        IEvidenceLedger evidenceLedger,
        ISessionResolver sessionResolver,
        ITokenCounter tokenCounter,
        ILogger<HypaReadTool> logger,
        CancellationToken cancellationToken,
        [Description("File path (relative to project root, or absolute)")] string path,
        [Description("Read mode: full | outline | signatures | pruned | smart (default: smart)")] string? mode = null,
        [Description("Maximum tokens to return")] int? maxTokens = null)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(path))
            return McpToolResult.Err("SUMMARY\nError: path is required.");

        var projectRoot = projectRootDetector.Detect(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory();
        var resolvedPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(projectRoot, path));

        if (!IsWithinRoot(resolvedPath, projectRoot))
            return McpToolResult.Err($"SUMMARY\nError: path escapes project root: {path}");

        if (!File.Exists(resolvedPath))
            return McpToolResult.Err($"SUMMARY\nError: file not found: {path}");

        byte[] bytes;
        try
        {
            bytes = fileSystem.ReadAllBytes(resolvedPath);
        }
        catch (Exception ex)
        {
            return McpToolResult.Err($"SUMMARY\nError reading file: {ex.Message}");
        }

        var content = Encoding.UTF8.GetString(bytes);
        var effectiveMode = (mode ?? "smart").ToLowerInvariant();
        string text;

        if (effectiveMode is "outline" or "signatures" or "pruned" or "smart")
        {
            var lang = DetectLanguage(resolvedPath);
            var provider = providerRegistry.Select(lang);
            var fileId = new CodeFileIdentity
            {
                ProjectRoot = projectRoot,
                Path = resolvedPath,
                RelativePath = Path.GetRelativePath(projectRoot, resolvedPath),
                Language = lang,
                ContentHash = ComputeHash(bytes)
            };

            var doc = await provider.ParseAsync(fileId, content, cancellationToken);
            text = BuildStructuredOutput(effectiveMode, path, content, doc, maxTokens);
        }
        else
        {
            var truncated = TruncateIfNeeded(content, maxTokens);
            text = $"SUMMARY\nFile: {path}\n\nDETAILS\n{truncated}";
        }

        var tokenCount = tokenCounter.EstimateTokens(text);
        var args = McpToolResult.BuildArgsJson(
            ("path", path), ("mode", effectiveMode),
            ("maxTokens", maxTokens?.ToString()));
        var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), cancellationToken);
        if (!sessionResult.IsOk)
            logger.LogWarning("session not resolved, recording with empty ID: {Error}", sessionResult.Error.Message);
        await evidenceLedger.RecordToolCallAsync(new ToolCallRecord
        {
            SessionId = sessionResult.IsOk ? sessionResult.Value.Id : Guid.Empty,
            ToolName = "hypa_read",
            Args = args,
            ArgsHash = HashString(args),
            Result = text[..Math.Min(200, text.Length)],
            OutputHash = HashString(text),
            DurationMs = sw.ElapsedMilliseconds
        }, cancellationToken);

        return McpToolResult.Ok(text + $"\n\nSTATS\ntokens={tokenCount} mode={effectiveMode} duration={sw.ElapsedMilliseconds}ms");
    }

    private static string BuildStructuredOutput(string mode, string path, string content, CodeStructureDocument doc, int? maxTokens)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"File: {path} ({doc.Symbols.Count} symbols)");
        sb.AppendLine();
        sb.AppendLine("DETAILS");

        if (mode is "outline" or "signatures")
        {
            foreach (var sym in doc.Symbols.Where(s => s.ParentId is null))
            {
                sb.AppendLine($"  {sym.Kind} {sym.Name} (line {sym.Span.StartLine})");
                foreach (var child in doc.Symbols.Where(s => s.ParentId == sym.Id))
                    sb.AppendLine($"    {child.Kind} {child.Name} (line {child.Span.StartLine})");
            }
        }
        else
        {
            sb.Append(TruncateIfNeeded(content, maxTokens));
        }

        sb.AppendLine();
        sb.AppendLine("REFERENCES");
        sb.Append(path);

        return sb.ToString().TrimEnd();
    }

    private static string TruncateIfNeeded(string content, int? maxTokens)
    {
        if (maxTokens is null || content.Length <= maxTokens.Value * 4)
            return content;
        var limit = maxTokens.Value * 4;
        return content[..limit] + $"\n...[truncated at ~{maxTokens} tokens]";
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

    private static string ComputeHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..8];

    private static string HashString(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    // Prefix-only check (StartsWith) is insufficient: "/project-evil" starts with "/project".
    // Appending the separator ensures only true descendants match.
    private static bool IsWithinRoot(string resolvedPath, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return resolvedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || resolvedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
