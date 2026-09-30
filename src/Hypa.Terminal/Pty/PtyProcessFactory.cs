using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Selects process-io or hypa-pty-host. Production Unix path does not fork
/// from managed code. Unix default is hypa-pty-host. Process-io is opt-in
/// for tests and batch work.
/// </summary>
public sealed class PtyProcessFactory : IPtyProcessFactory
{
    private readonly ILogger _logger;

    public PtyProcessFactory(PtyProviderOptions? options = null, ILogger? logger = null)
    {
        Options = options ?? PtyProviderOptions.FromEnvironment();
        _logger = logger ?? NullLogger.Instance;
    }

    public PtyProviderOptions Options { get; }

    public IPtyProcess Spawn(
        string fileName,
        IReadOnlyList<string> args,
        string cwd,
        int cols,
        int rows,
        IReadOnlyDictionary<string, string>? env = null)
    {
        return Options.Provider switch
        {
            PtyProviderKind.HypaPtyHost => SpawnHost(fileName, args, cwd, cols, rows, env),
            _ => ProcessPtyFallback.Spawn(fileName, args, cwd, env, _logger),
        };
    }

    private IPtyProcess SpawnHost(
        string fileName,
        IReadOnlyList<string> args,
        string cwd,
        int cols,
        int rows,
        IReadOnlyDictionary<string, string>? env)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "hypa-pty-host is Unix-only. Use process-io on Windows.");
        }

        var helperPath = PtyHostSupervisor.ResolveHelperPath(Options.HelperPath);
        if (helperPath is null || !File.Exists(helperPath))
        {
            if (Options.RequireHelperBinary)
            {
                throw new FileNotFoundException(
                    "hypa-pty-host binary not found. Set HYPA_PTY_HOST or build with " +
                    "scripts/build-hypa-pty-host.sh. Production path fails closed when selected.",
                    helperPath ?? "hypa-pty-host");
            }

            _logger.LogWarning(
                "hypa-pty-host missing; falling back to process-io (RequireHelperBinary=false)");
            return ProcessPtyFallback.Spawn(fileName, args, cwd, env, _logger);
        }

        _logger.LogInformation(
            "Spawning pane via hypa-pty-host ({Helper}) for {File}",
            helperPath, fileName);

        return PtyHostProcess.Spawn(
            fileName,
            args,
            cwd,
            cols,
            rows,
            env,
            new PtyHostOptions
            {
                HelperPath = helperPath,
                HelloTimeout = Options.HelloTimeout,
            },
            Options.SpawnTimeout,
            _logger);
    }
}
