using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Runner;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Infrastructure.CodeIntelligence;

/// <summary>
/// Spawns the same hypa binary as <c>code parse-worker</c> so a native SIGSEGV
/// kills only the worker. Parent maps exit codes and stdout JSON to
/// <see cref="Result{T,E}"/>.
/// </summary>
public sealed class ProcessNativeParseHost(ICommandRunner commandRunner) : INativeParseHost
{
    public const string WorkerEnvName = "HYPA_CODE_PARSE_WORKER";
    public const string WorkerEnvValue = "1";

    public async Task<Result<CodeStructureDocument, NativeParseError>> ParseAsync(
        NativeParseRequest request,
        CancellationToken ct)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(WorkerEnvName),
                WorkerEnvValue,
                StringComparison.Ordinal))
        {
            return Result<CodeStructureDocument, NativeParseError>.Fail(new NativeParseError
            {
                Kind = NativeParseErrorKind.ProtocolError,
                Message = "Refusing nested parse isolation (already inside a parse worker).",
            });
        }

        string executable;
        try
        {
            executable = ResolveSelfExecutable();
        }
        catch (Exception ex)
        {
            return Result<CodeStructureDocument, NativeParseError>.Fail(new NativeParseError
            {
                Kind = NativeParseErrorKind.SpawnFailed,
                Message = ex.Message,
            });
        }

        var timeout = request.Timeout > TimeSpan.Zero
            ? request.Timeout
            : TimeSpan.FromSeconds(60);

        // Pass parent identity so monikers / artifact paths use workspace-relative paths
        // (not filename-only) and retain GitBlobOid under isolation.
        var args = BuildWorkerArguments(request);

        var binaryDir = Path.GetDirectoryName(executable);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkerEnvName] = WorkerEnvValue,
            // Do not force DOTNET_gcConcurrent=0. Concurrent GC is safe when the host
            // AppArmor profile allows peer tgkill (Atomic: hypa-dotnet-gc, GitHub #96).
            // Hostile hosts may set DOTNET_gcConcurrent=0 in the parent environment;
            // the worker inherits that env from the process launcher unless overridden.
        };

        // Linux: grammar .so files sit next to the published binary.
        if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(binaryDir))
        {
            var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            if (string.IsNullOrEmpty(existing))
                env["LD_LIBRARY_PATH"] = binaryDir;
            else if (!existing.Split(Path.PathSeparator).Contains(binaryDir))
                env["LD_LIBRARY_PATH"] = binaryDir + Path.PathSeparator + existing;
        }

        // Must set Timeout explicitly — CommandInvocation defaults to 30s.
        var invocation = new CommandInvocation
        {
            Executable = executable,
            Arguments = args,
            OriginalCommand = $"hypa code parse-worker --language {request.Language} --path {request.AbsolutePath}",
            WorkingDirectory = binaryDir,
            EnvOverrides = env,
            Timeout = timeout,
            Mode = ToolRunMode.Buffered,
        };

        var run = await commandRunner.RunAsync(invocation, ct);
        if (!run.IsOk)
        {
            return Result<CodeStructureDocument, NativeParseError>.Fail(new NativeParseError
            {
                Kind = NativeParseErrorKind.SpawnFailed,
                Message = run.Error.Message,
            });
        }

        var output = run.Value;
        if (output.WasTimedOut || output.ExitCode == CommandOutput.TimeoutExitCode)
        {
            return Result<CodeStructureDocument, NativeParseError>.Fail(new NativeParseError
            {
                Kind = NativeParseErrorKind.TimedOut,
                ExitCode = CommandOutput.TimeoutExitCode,
                Message = $"Native parse worker timed out after {timeout.TotalSeconds:0}s.",
            });
        }

        // Abnormal process death (SIGSEGV = 139, other signals >= 128, unknown codes).
        var isNormalManagedExit = output.ExitCode is 0 or 1 or 2;
        if (!isNormalManagedExit)
        {
            return Result<CodeStructureDocument, NativeParseError>.Fail(new NativeParseError
            {
                Kind = NativeParseErrorKind.Crashed,
                ExitCode = output.ExitCode,
                Message = $"Native parse worker crashed (exit {output.ExitCode}).",
            });
        }

        NativeParseWorkerEnvelope? envelope;
        try
        {
            var stdout = output.Stdout.Trim();
            if (string.IsNullOrEmpty(stdout))
            {
                return Result<CodeStructureDocument, NativeParseError>.Fail(new NativeParseError
                {
                    Kind = output.ExitCode == 0
                        ? NativeParseErrorKind.ProtocolError
                        : NativeParseErrorKind.Crashed,
                    ExitCode = output.ExitCode,
                    Message = output.ExitCode == 0
                        ? "Native parse worker returned empty stdout."
                        : $"Native parse worker failed with empty stdout (exit {output.ExitCode}).",
                });
            }

            envelope = JsonSerializer.Deserialize(stdout, NativeParseWorkerJsonContext.Default.NativeParseWorkerEnvelope);
        }
        catch (JsonException ex)
        {
            return Result<CodeStructureDocument, NativeParseError>.Fail(new NativeParseError
            {
                Kind = output.ExitCode == 0
                    ? NativeParseErrorKind.ProtocolError
                    : NativeParseErrorKind.Crashed,
                ExitCode = output.ExitCode,
                Message = $"Native parse worker stdout was not valid JSON: {ex.Message}",
            });
        }

        if (envelope is null)
        {
            return Result<CodeStructureDocument, NativeParseError>.Fail(new NativeParseError
            {
                Kind = NativeParseErrorKind.ProtocolError,
                ExitCode = output.ExitCode,
                Message = "Native parse worker stdout deserialised to null.",
            });
        }

        if (envelope.Ok)
        {
            if (envelope.Document is null)
            {
                return Result<CodeStructureDocument, NativeParseError>.Fail(new NativeParseError
                {
                    Kind = NativeParseErrorKind.ProtocolError,
                    ExitCode = output.ExitCode,
                    Message = "Native parse worker returned ok without a document.",
                });
            }

            return Result<CodeStructureDocument, NativeParseError>.Ok(envelope.Document);
        }

        return Result<CodeStructureDocument, NativeParseError>.Fail(new NativeParseError
        {
            Kind = NativeParseErrorKind.ParseFailure,
            ExitCode = output.ExitCode,
            Message = envelope.Message ?? envelope.Error ?? "Native parse worker reported parse failure.",
        });
    }

    /// <summary>
    /// Build CLI args for <c>code parse-worker</c>, including parent identity metadata.
    /// Exposed for unit tests.
    /// </summary>
    internal static string[] BuildWorkerArguments(NativeParseRequest request)
    {
        var timeout = request.Timeout > TimeSpan.Zero
            ? request.Timeout
            : TimeSpan.FromSeconds(60);

        var args = new List<string>
        {
            "code",
            "parse-worker",
            "--language",
            request.Language,
            "--path",
            request.AbsolutePath,
            "--quiet",
            "--relative-path",
            request.File.RelativePath,
            "--project-root",
            request.File.ProjectRoot,
            "--timeout-ms",
            ((int)timeout.TotalMilliseconds).ToString(),
        };

        if (!string.IsNullOrEmpty(request.File.GitBlobOid))
        {
            args.Add("--git-blob-oid");
            args.Add(request.File.GitBlobOid);
        }

        return args.ToArray();
    }

    /// <summary>
    /// Resolve the hypa binary for self-spawn. Prefer <see cref="Environment.ProcessPath"/>
    /// when it is not the <c>dotnet</c> host. Fall back to the apphost next to assemblies
    /// for <c>dotnet run</c> / JIT dev.
    /// </summary>
    internal static string ResolveSelfExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var name = Path.GetFileNameWithoutExtension(processPath);
            if (!name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }
        }

        var apphost = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "hypa.exe" : "hypa");
        if (File.Exists(apphost))
            return apphost;

        if (!string.IsNullOrEmpty(processPath))
            return processPath;

        throw new InvalidOperationException(
            "Cannot resolve hypa executable path for parse isolation. Publish hypa or set ProcessPath.");
    }
}
