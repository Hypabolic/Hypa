using Hypa.AgentIntelligence;
using Hypa.AgentIntelligence.Detection;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Infrastructure.Logging;
using Hypa.AgentRuntime.Infrastructure.Occupants;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.ControlPlane;
using Hypa.ControlPlane.Unix;
using Hypa.Terminal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hypa.AgentServer;

public sealed record AgentRuntimeHostOptions(
    string SessionName,
    string Cwd,
    string? StateDirOverride,
    string[] HostArgs)
{
    /// <summary>Set only when the user passed <c>--cwd</c>.</summary>
    public string? ExplicitCwd { get; init; }
}

/// <summary>
/// Composition root for the mux host. Shared by the internal <c>hypa-runtime</c>
/// binary and <c>hypa mux serve</c> so AOT trim sees one static graph.
/// </summary>
public static class AgentRuntimeHostBuilder
{
    internal const string SupervisorDaemonizeVariable = "HYPA_MUX_DAEMONIZE";
    internal const string SupervisorLogVariable = "HYPA_MUX_LOG";

    /// <summary>
    /// Snapshot of supervisor-only spawn flags. Consume before host/pane spawn
    /// so ProcessPtyFallback children do not inherit daemonize or mux.log.
    /// </summary>
    internal sealed record SupervisorSpawnSnapshot(bool WantsSupervisorDaemonize, string? LogPath);

    public static async Task<int> Run(string[] args)
    {
        var spawn = ConsumeSupervisorSpawnEnvironment();
        using var _ = IgnoreDisconnectSignals(spawn.WantsSupervisorDaemonize);
        if (spawn.WantsSupervisorDaemonize)
            TryCreateSession();

        var sessionName = "default";
        var sessionSet = false;
        var cwd = Environment.CurrentDirectory;
        string? explicitCwd = null;
        string? stateDirOverride = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--session" when i + 1 < args.Length:
                    sessionName = args[++i];
                    sessionSet = true;
                    break;
                case "--cwd" when i + 1 < args.Length:
                    explicitCwd = args[++i];
                    cwd = explicitCwd;
                    break;
                case "--state-dir" when i + 1 < args.Length:
                    stateDirOverride = args[++i];
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return 0;
            }
        }

        var attachLoader = new FileAttachConfigLoader();
        var attachLoaded = attachLoader.Load();
        if (!attachLoaded.IsOk)
        {
            AttachConfigErrors.Write(Console.Error, attachLoaded.Errors);
            return 1;
        }

        if (NestedAttachGuard.IsBlocked(attachLoaded.Value, Environment.GetEnvironmentVariable(NestedAttachGuard.EnvName)))
        {
            Console.Error.WriteLine(NestedAttachGuard.Message);
            return 1;
        }

        sessionName = ResolveServeSession(sessionSet, sessionName, attachLoaded.Value);

        if (!SessionId.IsValidName(sessionName))
        {
            Console.Error.WriteLine(
                "Invalid --session name. Use 1–64 characters of [A-Za-z0-9._-] only (no path separators).");
            return 2;
        }

        string socketPath;
        try
        {
            socketPath = UnixSocketServer.ResolveSocketPath(sessionName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var statePaths = RuntimeStatePaths.Resolve(sessionName, socketPath, stateDirOverride);
        var options = new AgentRuntimeHostOptions(sessionName, cwd, stateDirOverride, args)
        {
            ExplicitCwd = explicitCwd,
        };

        HostApplicationBuilder builder;
        Hypa.Terminal.Pty.PtyProviderOptions ptyOptions;
        Hypa.Terminal.Vt.Ghostty.VtRuntimeHealth vtHealth;
        try
        {
            (builder, ptyOptions, vtHealth) = Create(
                options, socketPath, statePaths, attachLoaded.Value, attachLoader, spawn.LogPath);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("FATAL:", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        using var host = builder.Build();

        Console.WriteLine($"hypa mux session={sessionName}");
        Console.WriteLine($"socket={socketPath}");
        Console.WriteLine($"state={statePaths.StateDirectory}");
        Console.WriteLine($"cwd={cwd}");
        Console.WriteLine($"pty.provider={ptyOptions.ProviderWireName} interactive={ptyOptions.Interactive}");
        Console.WriteLine($"vt.provider={vtHealth.Provider}");
        if (vtHealth.Ghostty is { } g)
        {
            Console.WriteLine($"vt.ghostty_version={g.Version} abi={g.Abi} link={g.LinkMode}");
            if (!string.IsNullOrEmpty(g.Build))
                Console.WriteLine($"vt.ghostty_build={g.Build}");
        }
        else if (!string.IsNullOrEmpty(vtHealth.FallbackReason))
        {
            Console.WriteLine($"vt.fallback_reason={vtHealth.FallbackReason}");
        }

        if (ptyOptions.Provider == Hypa.Terminal.Pty.PtyProviderKind.ProcessIo)
        {
            Console.WriteLine("NOTE: panes use redirected process I/O (ProcessPtyFallback; not interactive PTY).");
            Console.WriteLine("      Unix default is hypa-pty-host. process-io is opt-in for tests and batch.");
        }
        else if (ptyOptions.Provider == Hypa.Terminal.Pty.PtyProviderKind.HypaPtyHost)
        {
            Console.WriteLine("NOTE: default Unix panes use hypa-pty-host (interactive PTY; openpty/fork only inside helper).");
            Console.WriteLine("      process-io is opt-in for tests and batch. Windows stays process-io.");
        }

        Console.WriteLine("NOTE: VT provider is ghostty (GhosttyVtEngine wired into PaneRuntime).");

        Console.WriteLine("Control plane ready. Agents: see skills/hypa-runtime/SKILL.md");
        Console.WriteLine("      Status chips and window title are attach UX. hypa --skill prints the agent skill.");

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Mux serve session: --session &gt; HYPA_SESSION &gt; session.name &gt; default.
    /// </summary>
    internal static string ResolveServeSession(
        bool sessionSet,
        string sessionName,
        Hypa.AgentRuntime.Domain.AttachConfig.AttachClientConfig config) =>
        AttachSessionResolver.Resolve(sessionSet ? sessionName : null, config);

    public static (HostApplicationBuilder Builder, Hypa.Terminal.Pty.PtyProviderOptions Pty, Hypa.Terminal.Vt.Ghostty.VtRuntimeHealth Vt)
        Create(
            AgentRuntimeHostOptions options,
            string socketPath,
            RuntimeStatePaths statePaths,
            Hypa.AgentRuntime.Domain.AttachConfig.AttachClientConfig? attachConfig = null,
            IAttachConfigLoader? attachLoader = null,
            string? logPath = null)
    {
        attachConfig ??= Hypa.AgentRuntime.Domain.AttachConfig.AttachClientConfig.Default;
        attachLoader ??= new FileAttachConfigLoader();
        var builder = Host.CreateApplicationBuilder(options.HostArgs);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var processLog = ProcessLogSinkFactory.OpenMux(socketPath, logPath);
        builder.Services.AddSingleton(processLog);
        builder.Services.AddSingleton<IProcessLogSink>(processLog);

        builder.Services.AddSingleton(statePaths);
        builder.Services.AddSingleton(attachConfig);
        builder.Services.AddSingleton<IAttachConfigLoader>(attachLoader);
        builder.Services.AddSingleton<IAttachConfigRuntime>(sp =>
            new LiveAttachConfigRuntime(
                sp.GetRequiredService<IAttachConfigLoader>(),
                sp.GetRequiredService<Hypa.AgentRuntime.Domain.AttachConfig.AttachClientConfig>()));
        builder.Services.AddSingleton(new AppState(SessionId.New(options.SessionName)));
        builder.Services.AddSingleton<IRuntimeSchemaMigrator, SqliteRuntimeSchemaMigrator>();
        builder.Services.AddSingleton<IRuntimeSessionStore, SqliteRuntimeSessionStore>();
        builder.Services.AddSingleton<IJournalManifestStore, SqliteJournalManifestStore>();
        builder.Services.AddSingleton<IEventSubscriptionHub>(sp =>
            new EventSubscriptionHub(
                sp.GetRequiredService<IProcessLogSink>(),
                sp.GetRequiredService<AppState>().SessionId.Value));
        builder.Services.AddSingleton<IProcessLivenessProbe, OsProcessLivenessProbe>();
        builder.Services.AddSingleton<IPaneKeyComboEncoder, VtPaneKeyComboEncoder>();
        builder.Services.AddSingleton<IAgentPathCanonicalizer, OsAgentPathCanonicalizer>();
        builder.Services.AddSingleton<IPaneProcessInfoProbe, Hypa.Terminal.Pty.UnixPaneProcessInfoProbe>();
        builder.Services.AddSingleton<IAgentPresentationCompressor, DefaultAgentPresentationCompressor>();
        builder.Services.AddSingleton<IEventPayloadRedactor, DefaultEventPayloadRedactor>();
        builder.Services.AddSingleton<IIntelligencePipeline>(sp =>
            new PaneIntelligencePipeline(
                sp.GetService<ILogger<PaneIntelligencePipeline>>(),
                compressor: sp.GetRequiredService<IAgentPresentationCompressor>()));
        builder.Services.AddSingleton<IAttachConfigEnvironment, SystemAttachConfigEnvironment>();
        builder.Services.AddSingleton<IAttachConfigFiles, SystemAttachConfigFiles>();
        builder.Services.AddSingleton<IAgentManifestTextFetcher, HttpAgentManifestFetcher>();
        builder.Services.AddSingleton(sp =>
            new HeuristicAgentDetector(
                sp.GetRequiredService<IAttachConfigEnvironment>(),
                sp.GetRequiredService<IAttachConfigFiles>(),
                sp.GetService<IAgentManifestTextFetcher>(),
                TimeProvider.System,
                sp.GetService<ILogger<HeuristicAgentDetector>>()));
        builder.Services.AddSingleton<IAgentDetector>(sp => sp.GetRequiredService<HeuristicAgentDetector>());
        builder.Services.AddSingleton<IAgentManifestCatalog>(sp => sp.GetRequiredService<HeuristicAgentDetector>());
        builder.Services.AddSingleton<IOccupantManifestRegistry>(_ => BundledOccupantManifestRegistry.Default);
        builder.Services.AddSingleton<IRuntimeEvidenceJournal, SqliteRuntimeEvidenceJournal>();
        builder.Services.AddSingleton<IGitWorkspaceProbe, ProcessGitWorkspaceProbe>();
        builder.Services.AddSingleton<IGitWorktreePort, Hypa.AgentRuntime.Infrastructure.Git.ProcessGitWorktreeAdapter>();
        builder.Services.AddSingleton<ICheckpointStore, FileCheckpointStore>();
        builder.Services.AddSingleton<ICheckpointArtifactBuilder, DefaultCheckpointArtifactBuilder>();
        builder.Services.AddSingleton<ICheckpointService, DefaultCheckpointService>();

        var ptyOptions = Hypa.Terminal.Pty.PtyProviderOptions.FromEnvironment();
        builder.Services.AddSingleton(ptyOptions);

        var vtSelection = Hypa.Terminal.Vt.Ghostty.VtProviderSelection.FromEnvironment();
        Hypa.Terminal.Vt.Ghostty.VtRuntimeHealth vtHealth;
        try
        {
            var info = Hypa.Terminal.Vt.Ghostty.GhosttyVtProbe.ProbeRequired(vtSelection.LibraryPathOverride);
            using (var probeEngine = new Hypa.Terminal.Vt.Ghostty.GhosttyVtEngine(
                       cols: 20,
                       rows: 6,
                       maxScrollback: 16,
                       libraryPathOverride: vtSelection.LibraryPathOverride))
            {
                probeEngine.Feed("h14"u8);
                _ = probeEngine.CaptureSnapshot();
            }

            vtHealth = Hypa.Terminal.Vt.Ghostty.VtRuntimeHealth.ForGhostty(info);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("FATAL: Ghostty VT required but failed to load/probe/engine: " + ex.Message, ex);
        }

        var vtEngineFactory = new Hypa.Terminal.Vt.Ghostty.VtEngineFactory(vtSelection);
        builder.Services.AddSingleton(vtSelection);
        builder.Services.AddSingleton(vtHealth);
        builder.Services.AddSingleton<Hypa.Terminal.Vt.Ghostty.IVtEngineFactory>(vtEngineFactory);
        builder.Services.AddSingleton<Hypa.Terminal.Pty.IPtyProcessFactory>(sp =>
            new Hypa.Terminal.Pty.PtyProcessFactory(
                sp.GetRequiredService<Hypa.Terminal.Pty.PtyProviderOptions>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<Hypa.Terminal.Pty.PtyProcessFactory>()));
        builder.Services.AddSingleton<IPaneRuntimeFactory>(sp =>
            new PaneRuntimeFactory(
                sp.GetRequiredService<IIntelligencePipeline>(),
                sp.GetService<ILoggerFactory>(),
                sp.GetRequiredService<Hypa.Terminal.Pty.IPtyProcessFactory>(),
                sp.GetRequiredService<Hypa.Terminal.Vt.Ghostty.IVtEngineFactory>(),
                sp.GetRequiredService<IPaneProcessInfoProbe>()));
        builder.Services.AddSingleton<IRuntimeEventJournal>(sp =>
        {
            var state = sp.GetRequiredService<AppState>();
            var paths = sp.GetRequiredService<RuntimeStatePaths>();
            return new FileRuntimeEventJournal(
                paths,
                sp.GetRequiredService<IJournalManifestStore>(),
                state.SessionId.Value,
                sp.GetService<ILogger<FileRuntimeEventJournal>>(),
                retention: new SqliteJournalRetentionQuery(paths),
                processLog: sp.GetService<IProcessLogSink>());
        });
        builder.Services.AddSingleton<IControlPlaneService>(sp =>
        {
            var pty = sp.GetRequiredService<Hypa.Terminal.Pty.PtyProviderOptions>();
            var vt = sp.GetRequiredService<Hypa.Terminal.Vt.Ghostty.VtRuntimeHealth>();
            return new ControlPlaneService(
                sp.GetRequiredService<AppState>(),
                sp.GetRequiredService<IPaneRuntimeFactory>(),
                sp.GetRequiredService<IIntelligencePipeline>(),
                sp.GetRequiredService<IAgentDetector>(),
                sp.GetService<ILogger<ControlPlaneService>>(),
                sp.GetRequiredService<IRuntimeSessionStore>(),
                sp.GetRequiredService<IRuntimeEventJournal>(),
                sp.GetRequiredService<IEventSubscriptionHub>(),
                ptyProvider: pty.ProviderWireName,
                ptyInteractive: pty.Interactive,
                redactor: sp.GetRequiredService<IEventPayloadRedactor>(),
                presentation: sp.GetRequiredService<IAgentPresentationCompressor>(),
                evidence: sp.GetRequiredService<IRuntimeEvidenceJournal>(),
                checkpoints: sp.GetRequiredService<ICheckpointService>(),
                vtProvider: vt.Provider,
                vtFallbackReason: vt.FallbackReason,
                vtGhosttyVersion: vt.Ghostty?.Version,
                vtGhosttyBuild: vt.Ghostty?.Build,
                vtAbi: vt.Ghostty?.Abi,
                vtCapabilities: vt.Capabilities,
                keyComboEncoder: sp.GetRequiredService<IPaneKeyComboEncoder>(),
                processInfoProbe: sp.GetRequiredService<IPaneProcessInfoProbe>(),
                hostStop: sp.GetService<IRuntimeHostStop>(),
                attachConfig: sp.GetRequiredService<Hypa.AgentRuntime.Domain.AttachConfig.AttachClientConfig>(),
                attachConfigRuntime: sp.GetRequiredService<IAttachConfigRuntime>(),
                occupants: sp.GetRequiredService<IOccupantManifestRegistry>(),
                stateDirectory: statePaths.StateDirectory,
                gitWorktrees: sp.GetRequiredService<IGitWorktreePort>(),
                runtimeSocketPath: socketPath,
                paneHistoryStore: new FilePaneHistorySnapshotStore(statePaths.StateDirectory),
                processLog: sp.GetRequiredService<IProcessLogSink>());
        });
        builder.Services.AddSingleton<IRuntimeHostStop>(sp =>
            new HostApplicationLifetimeStop(sp.GetRequiredService<IHostApplicationLifetime>()));
        builder.Services.AddSingleton(sp =>
            new UnixSocketServer(
                sp.GetRequiredService<IControlPlaneService>(),
                socketPath,
                UnixSocketServerOptions.Default,
                sp.GetService<ILogger<UnixSocketServer>>(),
                sp.GetRequiredService<IProcessLogSink>(),
                sp.GetRequiredService<AppState>().SessionId.Value,
                sp.GetRequiredService<IEventPayloadRedactor>()));
        builder.Services.AddHostedService<AgentRuntimeHostedService>();
        builder.Services.AddSingleton(new RuntimeOptions(
            options.SessionName, options.Cwd, socketPath, statePaths.StateDirectory)
        {
            ExplicitCwd = options.ExplicitCwd,
        });

        return (builder, ptyOptions, vtHealth);
    }

    internal static bool WantsSupervisorDaemonize()
    {
        if (OperatingSystem.IsWindows())
            return false;

        return Environment.GetEnvironmentVariable(SupervisorDaemonizeVariable) == "1";
    }

    /// <summary>
    /// Copy supervisor spawn env, then unset it. Only the supervisor child
    /// may daemonize. Foreground <c>hypa mux serve</c> must still stop on SIGINT.
    /// </summary>
    internal static SupervisorSpawnSnapshot ConsumeSupervisorSpawnEnvironment()
    {
        var snapshot = new SupervisorSpawnSnapshot(
            WantsSupervisorDaemonize(),
            Environment.GetEnvironmentVariable(SupervisorLogVariable));
        Environment.SetEnvironmentVariable(SupervisorDaemonizeVariable, null);
        Environment.SetEnvironmentVariable(SupervisorLogVariable, null);
        return snapshot;
    }

    private static void TryCreateSession()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        try
        {
            _ = NativeSetsid();
        }
        catch
        {
            // EPERM when already a session leader (setsid(1) wrapper).
        }
    }

    private static IDisposable? IgnoreDisconnectSignals(bool daemonize)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var registrations = new List<PosixSignalRegistration>
        {
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, static ctx => ctx.Cancel = true),
        };

        // Supervisor path only. Foreground `hypa mux serve` must still stop on SIGINT.
        if (daemonize)
        {
            registrations.Add(
                PosixSignalRegistration.Create(PosixSignal.SIGINT, static ctx => ctx.Cancel = true));
        }

        return new SignalIgnoreScope(registrations);
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int NativeSetsid();

    private sealed class HostApplicationLifetimeStop(IHostApplicationLifetime lifetime) : IRuntimeHostStop
    {
        public void RequestStop() => lifetime.StopApplication();
    }

    private sealed class SignalIgnoreScope(IReadOnlyList<PosixSignalRegistration> registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations)
                registration.Dispose();
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            hypa mux serve — Hypa workspace mux host (internal binary: hypa-runtime)

            Users should run:
              hypa
              hypa mux serve [--session NAME] [--cwd PATH] [--state-dir PATH]

            hypa-runtime is an internal/debug alias of this host. Prefer hypa.

            Usage:
              hypa mux serve [--session NAME] [--cwd PATH] [--state-dir PATH]
              hypa-runtime   [--session NAME] [--cwd PATH] [--state-dir PATH]

            Environment:
              HYPA_RUNTIME_SOCKET      Override Unix socket path (must end with .sock)
              HYPA_RUNTIME_STATE_DIR   Override runtime state root (runtime.db location)
              HYPA_PTY_PROVIDER        Unix default hypa-pty-host | process-io
              HYPA_PTY_HOST            Absolute path to hypa-pty-host binary
              HYPA_VT_PROVIDER         ghostty (default; basic and f1 are rejected)
              HYPA_VT_REQUIRED         empty | ghostty (fail closed if Ghostty cannot load)
              HYPA_GHOSTTY_VT          Absolute path to libghostty-vt.so|.dylib

            Default Unix I/O: hypa-pty-host (interactive PTY; fork only inside the helper).
            Fails closed if the helper binary is missing. Windows default is process-io.
            Windows is not a Ghostty-only F1 mux RID.
            process-io is opt-in for tests and batch (HYPA_PTY_PROVIDER=process-io).
            Porta.Pty spike: not adopted.

            VT: Ghostty is the only pane engine. Missing libghostty-vt fails closed.
            F1 is the workspace floor with Ghostty. F2 is the Ghostty package floor
            plus recorded goldens. HYPA_VT_PROVIDER=basic is rejected.
            Status chips and window title are attach UX. hypa --skill prints the agent skill.
            M6 is the attach UX axis.

            The server owns processes, VT state, intelligence middleware, and the
            NDJSON control plane. Clients (CLI, TUI, MCP) attach as thin peers.
            """);
    }
}
