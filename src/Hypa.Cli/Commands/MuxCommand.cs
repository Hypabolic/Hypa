using System.CommandLine;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentServer;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;

namespace Hypa.Cli.Commands;

public sealed class MuxCommand(IAttachConfigLoader? attachConfig = null)
{
    public Command Build()
    {
        var cmd = new Command(
            "mux",
            "Workspace mux server operations.");
        cmd.Add(BuildServe());
        cmd.Add(BuildStop());
        cmd.Add(BuildLiveHandoff());
        cmd.Add(BuildReloadConfig());
        return cmd;
    }

    private Command BuildLiveHandoff()
    {
        var sessionOpt = new Option<string?>("--session") { Description = "Mux session name." };
        var socketOpt = new Option<string?>("--socket") { Description = "Unix socket path. Wins over --session." };
        var cmd = new Command(
            "live-handoff",
            "Replace the mux via server.live_handoff. Experimental. Unix-only.");
        cmd.Add(sessionOpt);
        cmd.Add(socketOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                await Console.Error.WriteLineAsync("server.live_handoff is Unix-only.")
                    .ConfigureAwait(false);
                return 2;
            }

            var sessionResult = parseResult.GetResult(sessionOpt);
            var sessionExplicit = sessionResult is { Tokens.Count: > 0 };
            if (!TryResolveConfiguredSession(
                    sessionExplicit ? parseResult.GetValue(sessionOpt) : null,
                    sessionExplicit,
                    out var session))
            {
                return 1;
            }

            var socket = parseResult.GetValue(socketOpt);
            return await LiveHandoffAsync(session, socket, sessionExplicit).ConfigureAwait(false);
        });
        return cmd;
    }

    internal static async Task<int> LiveHandoffAsync(
        string session,
        string? socketOverride,
        bool sessionExplicit)
    {
        string socketPath;
        try
        {
            socketPath = ResolveStopSocket(session, socketOverride, sessionExplicit);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        try
        {
            await using var client = new ControlPlaneClient(socketPath);
            await client.ConnectAsync().ConfigureAwait(false);
            var result = await client.CallAsync(ProtocolMethods.ServerLiveHandoff).ConfigureAwait(false);
            Console.WriteLine(result.GetRawText());
            return result.TryGetProperty("ok", out var ok) && ok.GetBoolean() ? 0 : 1;
        }
        catch (ControlPlaneException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Failed to connect to {socketPath}: {ex.Message}")
                .ConfigureAwait(false);
            return 2;
        }
    }

    private static Command BuildServe()
    {
        var sessionOpt = new Option<string?>("--session") { Description = "Mux session name." };
        var cwdOpt = new Option<string?>("--cwd") { Description = "Workspace working directory. When omitted, terminal.new_cwd applies." };
        var stateDirOpt = new Option<string?>("--state-dir") { Description = "Override runtime state directory." };
        var cmd = new Command("serve", "Run the mux host in the foreground.");
        cmd.Add(sessionOpt);
        cmd.Add(cwdOpt);
        cmd.Add(stateDirOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var forwarded = MuxInvocation.ForwardServeArgs(GetCliArgs());
            return await AgentRuntimeHostBuilder.Run(forwarded).ConfigureAwait(false);
        });
        return cmd;
    }

    private Command BuildReloadConfig()
    {
        var sessionOpt = new Option<string?>("--session") { Description = "Mux session name. Ignored when --socket is set. When set with a different HYPA_RUNTIME_SOCKET, reload fails." };
        var socketOpt = new Option<string?>("--socket") { Description = "Unix socket path. Wins over --session and HYPA_RUNTIME_SOCKET." };
        var cmd = new Command("reload-config", "Reload attach config on the mux (server.reload_config).")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        cmd.Add(sessionOpt);
        cmd.Add(socketOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (parseResult.UnmatchedTokens.Count > 0)
            {
                await Console.Error.WriteLineAsync("mux reload-config does not take extra arguments.")
                    .ConfigureAwait(false);
                return 4;
            }

            var sessionResult = parseResult.GetResult(sessionOpt);
            var sessionExplicit = sessionResult is { Tokens.Count: > 0 };
            var session = ResolveReloadSession(
                sessionExplicit ? parseResult.GetValue(sessionOpt) : null,
                sessionExplicit);
            var socket = parseResult.GetValue(socketOpt);
            return await ReloadConfigAsync(session, socket, sessionExplicit).ConfigureAwait(false);
        });
        return cmd;
    }

    internal static Task<int> ReloadConfigAsync(string session) =>
        ReloadConfigAsync(session, socketOverride: null, sessionExplicit: false);

    internal static async Task<int> ReloadConfigAsync(
        string session,
        string? socketOverride,
        bool sessionExplicit)
    {
        string socketPath;
        try
        {
            socketPath = ResolveStopSocket(session, socketOverride, sessionExplicit);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        try
        {
            await using var client = new ControlPlaneClient(socketPath);
            await client.ConnectAsync().ConfigureAwait(false);
            var result = await client.CallAsync(ProtocolMethods.ServerReloadConfig).ConfigureAwait(false);
            Console.WriteLine(result.GetRawText());
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "failed", StringComparison.Ordinal))
            {
                return 1;
            }

            return 0;
        }
        catch (ControlPlaneException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Failed to connect to {socketPath}: {ex.Message}")
                .ConfigureAwait(false);
            return 2;
        }
    }

    private Command BuildStop()
    {
        var sessionOpt = new Option<string?>("--session") { Description = "Mux session name. Ignored when --socket is set. When set with a different HYPA_RUNTIME_SOCKET, stop fails." };
        var socketOpt = new Option<string?>("--socket") { Description = "Unix socket path. Wins over --session and HYPA_RUNTIME_SOCKET." };
        var cmd = new Command("stop", "Stop the mux server for a session (server.stop, SIGTERM fallback).");
        cmd.Add(sessionOpt);
        cmd.Add(socketOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var sessionResult = parseResult.GetResult(sessionOpt);
            var sessionExplicit = sessionResult is { Tokens.Count: > 0 };
            if (!TryResolveConfiguredSession(
                    sessionExplicit ? parseResult.GetValue(sessionOpt) : null,
                    sessionExplicit,
                    out var session))
            {
                return 1;
            }

            var socket = parseResult.GetValue(socketOpt);
            return await StopAsync(session, socket, sessionExplicit).ConfigureAwait(false);
        });
        return cmd;
    }

    internal static Task<int> StopAsync(string session) =>
        StopAsync(session, socketOverride: null, sessionExplicit: false);

    internal static Task<int> StopAsync(string session, string? socketOverride) =>
        StopAsync(session, socketOverride, sessionExplicit: false);

    internal static async Task<int> StopAsync(string session, string? socketOverride, bool sessionExplicit)
    {
        string socketPath;
        try
        {
            socketPath = ResolveStopSocket(session, socketOverride, sessionExplicit);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        return await MuxServerStopper.StopAsync(session, socketPath, Console.Out, Console.Error)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Running mux for reload-config: --session, else HYPA_SESSION, else default.
    /// Does not read session.name from the edited file. That key is startup-only.
    /// </summary>
    internal static string ResolveReloadSession(string? sessionOption, bool sessionExplicit)
    {
        if (sessionExplicit && !string.IsNullOrWhiteSpace(sessionOption))
            return sessionOption.Trim();
        var env = AttachSessionResolver.EnvironmentSession();
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();
        return AttachSessionResolver.DefaultName;
    }

    /// <summary>
    /// Precedence: --socket, else HYPA_RUNTIME_SOCKET, else the session path.
    /// An explicit --session that names a different path than the env socket
    /// is an error (no silent retarget).
    /// </summary>
    internal static string ResolveStopSocket(string session, string? socketOverride, bool sessionExplicit)
    {
        if (!string.IsNullOrWhiteSpace(socketOverride))
            return Path.GetFullPath(socketOverride);

        var env = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
        var sessionPath = UnixSocketServer.ResolveSocketPath(session, honorEnvironment: false);
        if (string.IsNullOrWhiteSpace(env))
            return sessionPath;

        var envPath = Path.GetFullPath(env);
        if (sessionExplicit && !string.Equals(envPath, sessionPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"HYPA_RUNTIME_SOCKET ({envPath}) does not match session '{session}' ({sessionPath}). " +
                "Pass --socket to select a socket, or omit --session to use the environment socket.");
        }

        return envPath;
    }

    private bool TryResolveConfiguredSession(string? sessionOption, bool sessionExplicit, out string session)
    {
        if (!AttachConfigErrors.TryLoad(attachConfig, Console.Error, out var config))
        {
            session = "";
            return false;
        }

        session = AttachSessionResolver.Resolve(
            sessionExplicit ? sessionOption : null,
            config);
        return true;
    }

    private static string[] GetCliArgs()
    {
        var all = Environment.GetCommandLineArgs();
        return all.Length <= 1 ? [] : all[1..];
    }
}
