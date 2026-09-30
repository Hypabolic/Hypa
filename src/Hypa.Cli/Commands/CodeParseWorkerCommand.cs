using System.CommandLine;
using System.Text.Json;
using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Cli.Commands;

/// <summary>
/// Lean in-process parse worker used by export parse isolation.
/// Writes one JSON envelope to stdout. Does not open the index database.
/// Does not re-enter isolation.
/// </summary>
public sealed class CodeParseWorkerCommand(CodeStructureProviderRegistry providers)
{
    public Command Build()
    {
        var languageOpt = new Option<string>("--language")
        {
            Description = "Canonical language id (e.g. c-sharp, markdown).",
            Required = true,
        };
        var pathOpt = new Option<string>("--path")
        {
            Description = "Absolute path of the file to parse.",
            Required = true,
        };
        var relativePathOpt = new Option<string?>("--relative-path")
        {
            Description = "Workspace-relative path from the parent (required for stable monikers under isolation).",
        };
        var projectRootOpt = new Option<string?>("--project-root")
        {
            Description = "Workspace root from the parent export.",
        };
        var gitBlobOidOpt = new Option<string?>("--git-blob-oid")
        {
            Description = "Optional clean-tree git blob oid from the parent.",
        };
        var quietOpt = new Option<bool>("--quiet") { Description = "Silence non-JSON stderr diagnostics." };
        var timeoutOpt = new Option<int?>("--timeout-ms") { Description = "Optional advisory timeout in milliseconds (parent enforces kill)." };

        var cmd = new Command("parse-worker", "Parse one file in-process and emit a JSON document envelope (isolation worker).");
        cmd.Add(languageOpt);
        cmd.Add(pathOpt);
        cmd.Add(relativePathOpt);
        cmd.Add(projectRootOpt);
        cmd.Add(gitBlobOidOpt);
        cmd.Add(quietOpt);
        cmd.Add(timeoutOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var language = parseResult.GetValue(languageOpt);
            var path = parseResult.GetValue(pathOpt);
            var relativePathArg = parseResult.GetValue(relativePathOpt);
            var projectRootArg = parseResult.GetValue(projectRootOpt);
            var gitBlobOid = parseResult.GetValue(gitBlobOidOpt);
            var quiet = parseResult.GetValue(quietOpt);
            var timeoutMs = parseResult.GetValue(timeoutOpt);

            if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(path))
            {
                WriteFailure("bad-args", "Both --language and --path are required.");
                return 2;
            }

            // Parent enforces process kill; advisory timeout enables cooperative cancel when set.
            CancellationTokenSource? timeoutCts = null;
            if (timeoutMs is int advisoryMs && advisoryMs > 0)
            {
                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(advisoryMs);
            }

            using var _timeoutLifetime = timeoutCts;
            var workCt = timeoutCts?.Token ?? ct;

            var absolutePath = Path.GetFullPath(path);
            if (!File.Exists(absolutePath))
            {
                WriteFailure("io-error", $"File not found: {absolutePath}");
                return 1;
            }

            SourceText source;
            try
            {
                source = await SourceText.ReadUtf8Async(absolutePath, workCt);
            }
            catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                WriteFailure("timeout", $"Parse worker advisory timeout after {timeoutMs}ms.");
                return 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WriteFailure("io-error", ex.Message);
                return 1;
            }

            var info = new FileInfo(absolutePath);
            var identity = BuildIdentity(
                absolutePath,
                language,
                source,
                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                relativePathArg,
                projectRootArg,
                gitBlobOid);

            try
            {
                var selected = providers.Select(language);
                var document = await selected.ParseAsync(identity, source, workCt);
                var envelope = new NativeParseWorkerEnvelope
                {
                    Ok = true,
                    Document = document,
                };
                Console.Out.Write(
                    JsonSerializer.Serialize(envelope, NativeParseWorkerJsonContext.Default.NativeParseWorkerEnvelope));
                Console.Out.Write('\n');
                Console.Out.Flush();
                return 0;
            }
            catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                WriteFailure("timeout", $"Parse worker advisory timeout after {timeoutMs}ms.");
                return 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!quiet)
                    Console.Error.WriteLine($"parse-worker: {ex.Message}");
                WriteFailure("parse-failure", ex.Message);
                return 1;
            }
        });

        return cmd;
    }

    /// <summary>
    /// Build the file identity used for parse monikers and the returned document.
    /// Parent must pass workspace-relative path metadata; ad-hoc CLI use falls back to filename.
    /// </summary>
    internal static CodeFileIdentity BuildIdentity(
        string absolutePath,
        string language,
        SourceText source,
        long mtimeMs,
        string? relativePath,
        string? projectRoot,
        string? gitBlobOid)
    {
        var normalizedRelative = string.IsNullOrWhiteSpace(relativePath)
        ? Path.GetFileName(absolutePath)
        : relativePath.Replace('\\', '/').Trim();

        var root = string.IsNullOrWhiteSpace(projectRoot)
            ? Path.GetDirectoryName(absolutePath) ?? absolutePath
            : projectRoot;

        return new CodeFileIdentity
        {
            ProjectRoot = root,
            Path = absolutePath,
            RelativePath = normalizedRelative,
            Language = language,
            ContentHash = source.Sha256Hex,
            SizeBytes = source.SizeBytes,
            IndexedAt = DateTimeOffset.UtcNow,
            GitBlobOid = string.IsNullOrWhiteSpace(gitBlobOid) ? null : gitBlobOid,
            MTimeMs = mtimeMs,
        };
    }

    private static void WriteFailure(string error, string message)
    {
        var envelope = new NativeParseWorkerEnvelope
        {
            Ok = false,
            Error = error,
            Message = message,
        };
        Console.Out.Write(
            JsonSerializer.Serialize(envelope, NativeParseWorkerJsonContext.Default.NativeParseWorkerEnvelope));
        Console.Out.Write('\n');
        Console.Out.Flush();
    }
}
