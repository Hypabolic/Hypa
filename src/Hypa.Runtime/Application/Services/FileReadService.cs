using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Hypa.Runtime.Application;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Sdk.CodeIntelligence;
using Microsoft.Extensions.Logging;

namespace Hypa.Runtime.Application.Services;

public sealed record FileReadOutput
{
    public string Text { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public int Tokens { get; init; }
    public long DurationMs { get; init; }
}

public sealed class FileReadService(
    IFileSystem fileSystem,
    IProjectRootDetector projectRootDetector,
    CodeStructureProviderRegistry providerRegistry,
    IEvidenceLedger evidenceLedger,
    ISessionResolver sessionResolver,
    ITokenCounter tokenCounter,
    ILogger<FileReadService> logger)
{
    public async Task<Result<FileReadOutput, Error>> ReadAsync(
        string path,
        string? mode,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(path))
            return Result<FileReadOutput, Error>.Fail(new Error("PATH_REQUIRED", "path is required."));

        var projectRoot = ResolveProjectRoot();
        var resolvedPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(projectRoot, path));

        if (!PathJail.IsWithinRoot(resolvedPath, projectRoot))
            return Result<FileReadOutput, Error>.Fail(new Error("PATH_ESCAPE", $"path escapes project root: {path}"));

        if (!fileSystem.FileExists(resolvedPath))
            return Result<FileReadOutput, Error>.Fail(new Error("FILE_NOT_FOUND", $"file not found: {path}"));

        byte[] bytes;
        try
        {
            bytes = fileSystem.ReadAllBytes(resolvedPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<FileReadOutput, Error>.Fail(new Error("READ_ERROR", $"Error reading file: {ex.Message}"));
        }

        var content = Encoding.UTF8.GetString(bytes);
        var requestedMode = (mode ?? "smart").ToLowerInvariant();
        var modeResult = NormalizeMode(requestedMode);
        if (!modeResult.IsOk)
            return Result<FileReadOutput, Error>.Fail(modeResult.Error);

        var effectiveMode = modeResult.Value;
        string text;

        if (effectiveMode is "outline" or "signatures")
        {
            var language = DetectLanguage(resolvedPath);
            var provider = providerRegistry.Select(language);
            var fileId = new CodeFileIdentity
            {
                ProjectRoot = projectRoot,
                Path = resolvedPath,
                RelativePath = Path.GetRelativePath(projectRoot, resolvedPath),
                Language = language,
                ContentHash = ComputeHash(bytes),
            };

            var document = await provider.ParseAsync(fileId, content, cancellationToken);
            text = BuildStructuredOutput(path, document);
        }
        else
        {
            var truncated = TruncateIfNeeded(content, maxTokens);
            var modeNote = requestedMode != effectiveMode
                ? $"\nMode: {requestedMode} currently aliases to {effectiveMode}."
                : string.Empty;
            text = $"SUMMARY\nFile: {path}{modeNote}\n\nDETAILS\n{truncated}";
        }

        var tokenCount = tokenCounter.EstimateTokens(text);
        var output = new FileReadOutput
        {
            Text = text + $"\n\nSTATS\ntokens={tokenCount} mode={effectiveMode} duration={sw.ElapsedMilliseconds}ms",
            Mode = effectiveMode,
            Tokens = tokenCount,
            DurationMs = sw.ElapsedMilliseconds,
        };

        var args = CapabilityToolEvidence.BuildArgsJson(
            ("path", path),
            ("mode", requestedMode),
            ("effectiveMode", effectiveMode),
            ("maxTokens", maxTokens?.ToString()));
        await CapabilityToolEvidence.RecordAsync(
            evidenceLedger,
            sessionResolver,
            logger,
            "hypa_read",
            args,
            text,
            sw.ElapsedMilliseconds,
            cancellationToken);

        return Result<FileReadOutput, Error>.Ok(output);
    }

    private string ResolveProjectRoot()
    {
        var currentDirectory = fileSystem.GetCurrentDirectory();
        return Path.GetFullPath(projectRootDetector.Detect(currentDirectory) ?? currentDirectory);
    }

    private static string BuildStructuredOutput(string path, CodeStructureDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"File: {path} ({doc.Symbols.Count} symbols)");
        sb.AppendLine();
        sb.AppendLine("DETAILS");

        foreach (var symbol in doc.Symbols.Where(s => s.ParentId is null))
        {
            sb.AppendLine($"  {symbol.Kind} {symbol.Name} (line {symbol.Span.StartLine})");
            foreach (var child in doc.Symbols.Where(s => s.ParentId == symbol.Id))
                sb.AppendLine($"    {child.Kind} {child.Name} (line {child.Span.StartLine})");
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
            _ => "text",
        };

    private static string ComputeHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..8];

    private static Result<string, Error> NormalizeMode(string mode) =>
        mode switch
        {
            "outline" or "signatures" or "full" => Result<string, Error>.Ok(mode),
            "smart" or "pruned" => Result<string, Error>.Ok("full"),
            _ => Result<string, Error>.Fail(new Error("INVALID_MODE", $"unsupported read mode: {mode}")),
        };
}
