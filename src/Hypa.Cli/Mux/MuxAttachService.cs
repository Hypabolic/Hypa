using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Config;
using Hypa.Placement.Application;
using Hypa.Placement.Infrastructure;

namespace Hypa.Cli.Mux;

/// <summary>
/// What attach does after the user confirms a restart from the sidebar:
/// stop the mux, then exec the installed <c>hypa attach</c>. Tests swap these.
/// </summary>
internal sealed record MuxRestartSteps(
    Func<MuxRestartRequest, Task<bool>> Stop,
    Func<string, bool> ExecAttach)
{
    public static MuxRestartSteps Process { get; } = new(
        async restart => await MuxServerStopper.StopAsync(
                restart.Session,
                restart.SocketPath,
                Console.Error,
                Console.Error)
            .ConfigureAwait(false) == 0,
        session => MuxRestarter.TryExecAttach(session, Console.Error));
}

public sealed class MuxAttachService : ILiveAttachHost
{
    private readonly IMuxSupervisor _supervisor;
    private readonly IMuxAttachDriver _driver;
    private readonly IAttachConfigLoader? _configLoader;
    private readonly IRemoteMuxPath _remoteMux;
    private readonly MuxStaleServerGuard? _staleGuard;

    public MuxAttachService(
        IMuxSupervisor supervisor,
        IMuxAttachDriver driver,
        IAttachConfigLoader? configLoader = null,
        IRemoteMuxPath? remoteMux = null,
        MuxStaleServerGuard? staleGuard = null)
    {
        _supervisor = supervisor;
        _driver = driver;
        _configLoader = configLoader;
        _remoteMux = remoteMux ?? new OpenSshRemoteMuxAdapter();
        _staleGuard = staleGuard;
    }

    internal MuxRestartSteps RestartSteps { get; init; } = MuxRestartSteps.Process;

    public Task<int> AttachToPaneAsync(
        string paneId,
        string? sessionOption,
        string? socketOverride,
        bool sessionOptionWasSet,
        CancellationToken ct) =>
        AttachAsync(
            sessionOption,
            cwd: null,
            once: false,
            socketOverride,
            sessionOptionWasSet,
            ct,
            remoteDestination: false,
            focusPaneId: paneId);

    public async Task<int> AttachAsync(
        string session,
        string? cwd,
        bool once,
        string? socketOverride,
        CancellationToken ct) =>
        await AttachAsync(
                session,
                cwd,
                once,
                socketOverride,
                sessionOptionWasSet: true,
                ct,
                remoteDestination: false)
            .ConfigureAwait(false);

    public async Task<int> AttachAsync(
        string? sessionOption,
        string? cwd,
        bool once,
        string? socketOverride,
        bool sessionOptionWasSet,
        CancellationToken ct,
        bool remoteDestination = false,
        string? focusPaneId = null,
        RemoteAttachArgs? remote = null,
        string? connectPlacementId = null)
    {
        AttachClientConfig config;
        if (_configLoader is not null)
        {
            if (!AttachConfigErrors.TryLoad(_configLoader, Console.Error, out config))
                return 1;
        }
        else
        {
            config = AttachClientConfig.Default;
        }

        if (remote is not null)
            config = ForRemoteAttach(config, remote.Keybindings);

        if (!KeysConfigMapper.TryCompile(config.Keys, Console.Error, out _))
            return 1;

        if (_configLoader is not null
            && NestedAttachGuard.IsBlocked(config, Environment.GetEnvironmentVariable(NestedAttachGuard.EnvName)))
        {
            await Console.Error.WriteLineAsync(NestedAttachGuard.Message).ConfigureAwait(false);
            return 1;
        }

        var session = sessionOptionWasSet && !string.IsNullOrWhiteSpace(sessionOption)
            ? sessionOption.Trim()
            : AttachSessionResolver.Resolve(sessionOption, config);

        if (remote is not null)
        {
            return await AttachRemoteAsync(
                    remote,
                    session,
                    once,
                    config,
                    ct)
                .ConfigureAwait(false);
        }

        MuxReadyInfo ready;
        try
        {
            ready = await _supervisor.EnsureReadyAsync(session, cwd, socketOverride, ct)
                .ConfigureAwait(false);
            if (_staleGuard is not null
                && await _staleGuard.TryRestartAsync(ready, once).ConfigureAwait(false))
            {
                ready = await _supervisor.EnsureReadyAsync(session, cwd, socketOverride, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (MuxAttachException ex)
        {
            await Console.Error.WriteLineAsync($"hypa attach: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }

        var request = new MuxAttachRequest(
            once,
            Console.IsInputRedirected,
            Console.IsOutputRedirected,
            config,
            _configLoader,
            remoteDestination,
            FocusPaneId: focusPaneId,
            ConnectPlacementId: connectPlacementId);
        var exit = await _driver.RunAsync(ready, request, ct).ConfigureAwait(false);
        while (_driver is IMuxRestartSource source && source.TakeRestartRequest() is { } restart)
        {
            if (!await RestartSteps.Stop(restart).ConfigureAwait(false))
                return 1;

            // exec does not return on success. The installed hypa starts its own mux.
            if (RestartSteps.ExecAttach(restart.Session))
                return 0;

            // Source builds re-attach in this process. The supervisor starts the mux.
            try
            {
                ready = await _supervisor.EnsureReadyAsync(restart.Session, cwd, socketOverride, ct)
                    .ConfigureAwait(false);
            }
            catch (MuxAttachException ex)
            {
                await Console.Error.WriteLineAsync($"hypa attach: {ex.Message}").ConfigureAwait(false);
                return 1;
            }

            exit = await _driver.RunAsync(
                    ready,
                    request with { FocusPaneId = null, ConnectPlacementId = null },
                    ct)
                .ConfigureAwait(false);
        }

        return exit;
    }

    internal static AttachClientConfig ForRemoteAttach(
        AttachClientConfig config,
        RemoteKeybindingsMode mode)
    {
        var keys = mode == RemoteKeybindingsMode.Server
            ? AttachKeysConfig.Default
            : config.Keys;
        return config with { Keys = keys with { Commands = [] } };
    }

    private async Task<int> AttachRemoteAsync(
        RemoteAttachArgs remote,
        string session,
        bool once,
        AttachClientConfig config,
        CancellationToken ct)
    {
        if (remote.LiveHandoff && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            await Console.Error.WriteLineAsync(
                    "hypa: --handoff is experimental and Unix-only.")
                .ConfigureAwait(false);
            return 2;
        }

        var unix = Console.IsInputRedirected == false
            && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var remoteOpen = new RemoteMuxOpenRequest
        {
            ExplicitTarget = remote.Target,
            Session = session,
            ManageSshConfig = config.Remote.ManageSshConfig,
            LiveHandoff = remote.LiveHandoff,
            LiveHandoffEnabled = true,
            OperatorConsent = false,
            Interactive = unix,
            Keybindings = remote.Keybindings,
        };
        var outcome = await _remoteMux.OpenAsync(remoteOpen, ct).ConfigureAwait(false);
        if (!outcome.Ok
            && outcome.Reason == RemoteMuxReasons.RestartRequired
            && unix
            && remote.LiveHandoff)
        {
            if (!RemoteRestartConsent.TryPrompt(
                    Console.In,
                    Console.Error,
                    outcome.Detail ?? RemoteRestartPolicy.ConsentCopy()))
            {
                return 2;
            }

            outcome = await _remoteMux.OpenAsync(
                    remoteOpen with { OperatorConsent = true },
                    ct)
                .ConfigureAwait(false);
        }

        if (!outcome.Ok || outcome.Path is null)
        {
            await Console.Error.WriteLineAsync(
                    "hypa --remote: " + (outcome.Detail ?? "remote mux path failed"))
                .ConfigureAwait(false);
            return 2;
        }

        await using var endpoint = new SshAttachEndpoint(outcome.Path);
        var ready = new MuxReadyInfo(
            outcome.Path.Session,
            outcome.Path.LocalSocketPath,
            PingJson: "{}",
            endpoint);
        var request = new MuxAttachRequest(
            once,
            Console.IsInputRedirected,
            Console.IsOutputRedirected,
            config,
            _configLoader,
            RemoteDestination: true,
            RemoteAttach: true,
            RemoteKeybindings: remote.Keybindings,
            LiveHandoff: remote.LiveHandoff);
        return await _driver.RunAsync(ready, request, ct).ConfigureAwait(false);
    }
}
