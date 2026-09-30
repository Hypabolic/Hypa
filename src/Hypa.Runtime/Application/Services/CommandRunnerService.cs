using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using Hypa.Runtime.Domain.Metrics;
using Hypa.Runtime.Domain.Filters;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace Hypa.Runtime.Application.Services;

public sealed class CommandRunnerService(
    ICommandRunner runner,
    IEnumerable<IOutputCompressor> compressors,
    ITokenCounter tokenCounter,
    IArtifactRepository artifacts,
    IEvidenceLedger evidence,
    ISessionResolver sessionResolver,
    IConfigLoader configLoader,
    IPackageManagerScriptResolver packageManagerScriptResolver,
    FilterService filterService,
    IFilterEngine filterEngine,
    IParseMetricsRepository parseMetrics,
    ILogger<CommandRunnerService> logger) : ICommandRunnerService
{
    private readonly IReadOnlyList<IOutputCompressor> _compressors = compressors.ToList();

    public async Task<Result<BufferedRunOutput, Error>> RunBufferedAsync(
        CommandInvocation invocation,
        CompressionOptions options,
        CancellationToken ct)
    {
        var effectiveOptions = await ResolveCompressionOptionsAsync(options, ct);
        var resolvedPackageScript = packageManagerScriptResolver.TryResolve(invocation);
        var runResult = await runner.RunAsync(invocation, ct);
        if (!runResult.IsOk)
            return Result<BufferedRunOutput, Error>.Fail(runResult.Error);

        var output = runResult.Value;
        var rawCombined = output.Stdout.Length == 0
            ? output.Stderr
            : output.Stderr.Length == 0
                ? output.Stdout
                : output.Stdout + "\n" + output.Stderr;
        var combined = output.WasTimedOut
            ? AppendTimeoutDiagnostic(rawCombined, invocation, output)
            : rawCombined;
        var originalTokens = tokenCounter.EstimateTokens(combined);

        string finalText;
        string reducerId = "passthrough";
        int compressedTokens = originalTokens;
        bool wasTruncated = false;
        bool selectedCompressedOutput = false;

        if (originalTokens <= effectiveOptions.SmallOutputThreshold)
        {
            finalText = combined;
        }
        else
        {
            var compressor = _compressors.FirstOrDefault(c => c.CanHandle(invocation));
            if (compressor is null)
                return Result<BufferedRunOutput, Error>.Fail(new Error("NO_COMPRESSOR", "No compressor registered."));

            var result = compressor.Compress(invocation, output, effectiveOptions);
            reducerId = result.ReducerId;
            wasTruncated = result.WasTruncated;

            if (result.CompressedTokens >= originalTokens)
            {
                finalText = combined;
                compressedTokens = originalTokens;
                reducerId = "passthrough";
            }
            else
            {
                finalText = result.Text;
                compressedTokens = result.CompressedTokens;
                selectedCompressedOutput = true;
            }
        }

        Guid? teeArtifactId = null;
        var sessionId = await ResolveSessionIdBestEffortAsync(ct);

        if ((output.ExitCode != 0 || output.WasTimedOut) && effectiveOptions.TeeOnFailure)
        {
            teeArtifactId = await TeeAsync(combined, sessionId, ct);
        }
        else if (wasTruncated && effectiveOptions.TeeOnTruncation)
        {
            teeArtifactId = await TeeAsync(combined, sessionId, ct);
        }


        // Apply DSL filters (built-in → user-global → trusted project-local)
        string? appliedFilterId = null;
        bool fallbackDiagnosticsComposed = false;
        var filterExecutable = resolvedPackageScript?.Executable ?? invocation.Executable;
        var filterCommand = resolvedPackageScript?.Command ?? invocation.OriginalCommand;
        foreach (var filter in filterService.GetApplicableFilters(filterExecutable, filterCommand))
        {
            var isSpecificResolvedFilter = resolvedPackageScript is not null
                && (filter.AppliesTo.Count > 0 || filter.CompiledMatchCommand is not null);
            if (!isSpecificResolvedFilter &&
                resolvedPackageScript is not null &&
                selectedCompressedOutput &&
                !fallbackDiagnosticsComposed)
            {
                finalText = AppendResolvedDiagnostics(finalText, invocation, output);
                fallbackDiagnosticsComposed = true;
            }
            var textForFilter = isSpecificResolvedFilter
                ? rawCombined
                : resolvedPackageScript is not null
                    ? finalText
                    : filter.MergeStderr && output.Stderr.Length > 0
                        ? finalText.TrimEnd() + "\n" + output.Stderr
                        : finalText;
            var fr = filterEngine.Apply(filter, textForFilter);
            var voided = string.IsNullOrWhiteSpace(fr.Text) &&
                !string.IsNullOrWhiteSpace(
                    isSpecificResolvedFilter ? textForFilter : finalText);
            var rejectedFailureOnEmptyOrMatchOutputReplacement =
                isSpecificResolvedFilter &&
                (output.ExitCode != 0 || output.WasTimedOut) &&
                filter.Stages.Any(stage =>
                    stage.Stage.Replacement is not null &&
                    stage.Stage.Kind is FilterStageKind.OnEmpty or FilterStageKind.MatchOutput &&
                    string.Equals(fr.Text, stage.Stage.Replacement, StringComparison.Ordinal));
            if (fr.StagesApplied > 0 && !voided && !rejectedFailureOnEmptyOrMatchOutputReplacement)
            {
                finalText = isSpecificResolvedFilter
                    ? output.WasTimedOut
                        ? AppendContentLinesIfMissing(fr.Text, CreateTimeoutDiagnostic(invocation, output))
                        : fr.Text
                    : fr.Text;
                appliedFilterId = filter.Id;
                break;
            }
        }

        if (appliedFilterId is null &&
            resolvedPackageScript is not null &&
            selectedCompressedOutput &&
            !fallbackDiagnosticsComposed)
        {
            finalText = AppendResolvedDiagnostics(finalText, invocation, output);
        }

        compressedTokens = tokenCounter.EstimateTokens(finalText);

        var commandMetrics = new CommandMetricsRecord
        {
            SessionId = sessionId,
            Command = invocation.OriginalCommand,
            ExitCode = output.ExitCode,
            DurationMs = (long)output.Duration.TotalMilliseconds,
            OriginalTokens = originalTokens,
            CompressedTokens = compressedTokens,
            ReducerId = reducerId,
            TeeArtifactId = teeArtifactId,
        };
        await RecordCommandMetricsBestEffortAsync(commandMetrics, ct);

        // Record parse metrics
        await RecordParseMetricsBestEffortAsync(new ParseMetricsRecord
        {
            RunId = commandMetrics.Id.ToString(),
            Executable = invocation.Executable,
            Arguments = string.Join(' ', invocation.Arguments),
            ParseTier = ParseTier.Passthrough,
            FilterId = appliedFilterId,
            RecordedAt = DateTimeOffset.UtcNow,
        }, ct);

        var shouldShowCompressionMetadata =
            effectiveOptions.ShowCompressionMetadata &&
            originalTokens > effectiveOptions.SmallOutputThreshold &&
            compressedTokens < originalTokens;

        if (shouldShowCompressionMetadata)
        {
            var saving = (int)Math.Round((1.0 - (double)compressedTokens / originalTokens) * 100);
            var metaLine = $"[hypa: {originalTokens}→{compressedTokens} tok, -{saving}%, reducer={reducerId}]";
            finalText = finalText.TrimEnd() + "\n" + metaLine;
        }

        if (teeArtifactId.HasValue)
        {
            finalText = finalText.TrimEnd() + $"\n[hypa: full output -> artifact:{teeArtifactId.Value:N}, expires in 24h]";
        }

        return Result<BufferedRunOutput, Error>.Ok(new BufferedRunOutput(finalText, output.ExitCode));
    }

    public async Task<Result<int, Error>> RunPassthroughAsync(
        CommandInvocation invocation,
        CancellationToken ct)
    {
        var passthroughInvocation = invocation with { Mode = ToolRunMode.Passthrough };
        var runResult = await runner.RunAsync(passthroughInvocation, ct);
        if (!runResult.IsOk)
            return Result<int, Error>.Fail(runResult.Error);

        var output = runResult.Value;
        var sessionId = await ResolveSessionIdBestEffortAsync(ct);

        await RecordCommandMetricsBestEffortAsync(new CommandMetricsRecord
        {
            SessionId = sessionId,
            Command = invocation.OriginalCommand,
            ExitCode = output.ExitCode,
            DurationMs = (long)output.Duration.TotalMilliseconds,
            OriginalTokens = 0,
            CompressedTokens = 0,
            ReducerId = "passthrough",
        }, ct);

        return Result<int, Error>.Ok(output.ExitCode);
    }

    private async Task<Guid?> TeeAsync(string content, Guid sessionId, CancellationToken ct)
    {
        try
        {
            var storeResult = await artifacts.StoreAsync(content, "text/plain", sessionId, ct);
            if (!storeResult.IsOk)
                logger.LogDebug("Failed to tee command output: {Error}", storeResult.Error.Message);
            return storeResult.IsOk ? storeResult.Value.Id : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to tee command output");
            return null;
        }
    }

    private async Task RecordCommandMetricsBestEffortAsync(CommandMetricsRecord record, CancellationToken ct)
    {
        try
        {
            await evidence.RecordCommandMetricsAsync(record, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to record command metrics");
        }
    }

    private async Task RecordParseMetricsBestEffortAsync(ParseMetricsRecord record, CancellationToken ct)
    {
        try
        {
            await parseMetrics.RecordAsync(record, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to record parse metrics");
        }
    }

    private async Task<Guid> ResolveSessionIdBestEffortAsync(CancellationToken ct)
    {
        try
        {
            var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), ct);
            if (sessionResult.IsOk)
                return sessionResult.Value.Id;

            logger.LogDebug("Session not resolved, recording with empty ID: {Error}", sessionResult.Error.Message);
            return Guid.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to resolve session, recording with empty ID");
            return Guid.Empty;
        }
    }

    private async Task<CompressionOptions> ResolveCompressionOptionsAsync(
        CompressionOptions options,
        CancellationToken ct)
    {
        try
        {
            var config = await configLoader.LoadAsync(ct);
            var showMetadata = config.IsOk
                ? config.Value.ShowCompressionMetadata
                : HypaConfig.Default.ShowCompressionMetadata;

            return options with
            {
                ShowCompressionMetadata = options.ShowCompressionMetadata && showMetadata,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to load config, using default compression options");
            return options;
        }
    }

    private static string AppendResolvedDiagnostics(
        string filteredStdout,
        CommandInvocation invocation,
        CommandOutput output) =>
        AppendContentLinesIfMissing(
            filteredStdout,
            output.Stderr,
            output.WasTimedOut ? CreateTimeoutDiagnostic(invocation, output) : null);

    private static string AppendContentLinesIfMissing(
        string text,
        string content,
        string? additionalContent = null)
    {
        if (content.Length == 0 && string.IsNullOrEmpty(additionalContent))
            return text;

        var remainingExistingCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = NormalizeLine(rawLine);
            if (line.Length == 0)
                continue;

            remainingExistingCounts.TryGetValue(line, out var count);
            remainingExistingCounts[line] = count + 1;
        }

        StringBuilder? result = null;
        AppendMissingLines(content, text, remainingExistingCounts, ref result);
        if (!string.IsNullOrEmpty(additionalContent))
            AppendMissingLines(additionalContent, text, remainingExistingCounts, ref result);

        return result?.ToString() ?? text;
    }

    private static void AppendMissingLines(
        string content,
        string originalText,
        Dictionary<string, int> remainingExistingCounts,
        ref StringBuilder? result)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = NormalizeLine(rawLine);
            if (line.Length == 0)
                continue;

            if (remainingExistingCounts.TryGetValue(line, out var remainingCount) &&
                remainingCount > 0)
            {
                remainingExistingCounts[line] = remainingCount - 1;
                continue;
            }

            result ??= new StringBuilder(originalText);
            if (result.Length > 0 && result[^1] != '\n')
                result.Append('\n');
            result.Append(line);
        }
    }

    private static string NormalizeLine(string rawLine) =>
        rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;

    private static string CreateTimeoutDiagnostic(
        CommandInvocation invocation,
        CommandOutput output) =>
        $"[hypa: command timed out after {invocation.Timeout.TotalSeconds:0.###}s; " +
        $"killed process; exit={CommandOutput.TimeoutExitCode}; " +
        $"elapsed={output.Duration.TotalSeconds:0.###}s]";

    private static string AppendTimeoutDiagnostic(
        string text,
        CommandInvocation invocation,
        CommandOutput output)
    {
        var line = CreateTimeoutDiagnostic(invocation, output);

        return string.IsNullOrWhiteSpace(text)
            ? line
            : text.TrimEnd() + "\n" + line;
    }
}
