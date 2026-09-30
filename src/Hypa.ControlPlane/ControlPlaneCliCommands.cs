using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

/// <summary>
/// Shared mux client switch used by <c>hypa ping|pane|…</c>.
/// Does not start the mux server.
/// </summary>
public static class ControlPlaneCliCommands
{
    public static Task<int> Run(string[] args) =>
        Run(args, attachConfig: null, liveAttach: null);

    public static Task<int> Run(string[] args, IAttachConfigLoader? attachConfig) =>
        Run(args, attachConfig, liveAttach: null);

    public static async Task<int> Run(
        string[] args,
        IAttachConfigLoader? attachConfig,
        ILiveAttachHost? liveAttach)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        ParsedCli parsed;
        try
        {
            parsed = Parse(args);
        }
        catch (ControlPlaneCliUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 4;
        }

        // A mux group with no subcommand, or with --help, prints locally.
        // It does not resolve a session and it does not open the mux socket.
        if (TryWriteMuxGroupHelp(parsed))
            return 0;

        // --current is resolved in this process. A missing variable is usage.
        // Do not open the mux socket.
        if (parsed.Switches.Contains("--current")
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PaneIdEnvironment.HypaPaneId)))
        {
            Console.Error.WriteLine("HYPA_PANE_ID is not set");
            return 4;
        }

        var resolved = TryResolveSession(parsed, attachConfig);
        if (!resolved.IsOk)
        {
            AttachConfigErrors.Write(Console.Error, resolved.Errors);
            return 1;
        }

        parsed = parsed with { Session = resolved.Value };

        if (parsed.Tokens.Count == 0)
        {
            PrintHelp();
            return 4;
        }

        string socketPath;
        try
        {
            socketPath = parsed.SocketOverride ?? UnixSocketServer.ResolveSocketPath(parsed.Session);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var callTimeout = TimeSpan.FromMilliseconds(parsed.TimeoutMs);
        var cmd = parsed.Tokens[0];
        var cargs = parsed.Tokens.Skip(1).ToArray();
        string? liveAttachPane = null;
        try
        {
            await using var client = new ControlPlaneClient(
                socketPath,
                connectTimeout: ControlPlaneClient.DefaultConnectTimeout,
                callTimeout: callTimeout);
            try
            {
                await client.ConnectAsync().ConfigureAwait(false);
            }
            catch (ControlPlaneClientTimeoutException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to connect to {socketPath}: {ex.Message}");
                Console.Error.WriteLine("Is the mux server running? Start it with: hypa mux serve");
                return 2;
            }

            if (cmd == "agent" && cargs.Length > 0 && cargs[0] == "attach")
            {
                liveAttachPane = await ResolveAgentAttachPaneAsync(client, parsed, cargs)
                    .ConfigureAwait(false);
            }
            else
            {
                var result = cmd switch
                {
                    "ping" => await client.CallAsync("ping").ConfigureAwait(false),
                    "snapshot" => await client.CallAsync("session.snapshot").ConfigureAwait(false),
                    "workspace" => await WorkspaceAsync(client, cargs, parsed, args).ConfigureAwait(false),
                    "tab" => await TabAsync(client, cargs, parsed, args).ConfigureAwait(false),
                    "layout" => await LayoutAsync(client, cargs, parsed, args).ConfigureAwait(false),
                    "pane" => await PaneAsync(client, cargs, parsed, args).ConfigureAwait(false),
                    "agent" => await AgentAsync(client, cargs, parsed, args).ConfigureAwait(false),
                    "integration" => await IntegrationAsync(client, cargs, parsed).ConfigureAwait(false),
                    "plugin" => await PluginAsync(client, cargs, parsed).ConfigureAwait(false),
                    "notification" => await NotificationAsync(client, cargs, parsed).ConfigureAwait(false),
                    "terminal" => await TerminalTitleAsync(client, cargs, parsed).ConfigureAwait(false),
                    "events" => await EventsAsync(client, cargs, parsed).ConfigureAwait(false),
                    "rpc" when cargs.Length >= 1 => await client.CallAsync(
                        cargs[0],
                        cargs.Length > 1 ? JsonNode.Parse(cargs[1]) as JsonObject : null).ConfigureAwait(false),
                    _ => throw new ControlPlaneCliUsageException($"Unknown command: {cmd}"),
                };

                if (cmd == "integration" && parsed.Switches.Contains("--dry-run"))
                {
                    PrintIntegrationPlan(result);
                    return 0;
                }

                if (cmd == "integration" && HasPerTargetOutcomes(result))
                {
                    PrintDetectedIntegration(result);
                    return DetectedIntegrationExitCode(
                        result,
                        cargs.Length > 0 ? cargs[0] : "install");
                }

                Console.WriteLine(result.ValueKind == JsonValueKind.Undefined
                    ? "{}"
                    : result.GetRawText());
                return 0;
            }
        }
        catch (ControlPlaneCliUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 4;
        }
        catch (ControlPlaneClientTimeoutException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (ControlPlaneException cpe)
        {
            Console.Error.WriteLine(FormatRpcError(cpe));
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        try
        {
            return await EnterLiveAgentAttachAsync(
                    liveAttachPane!, parsed, liveAttach, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static bool IsClientCommand(string[] args)
    {
        // Mux verbs are only argv[0] after optional global flag pairs.
        var i = 0;
        while (i < args.Length)
        {
            if (args[i] is "--session" or "--socket" or "--timeout-ms")
            {
                i += i + 1 < args.Length ? 2 : 1;
                continue;
            }

            break;
        }

        // Compression / passthrough flags must never become mux verbs.
        // `hypa -c ping` and `hypa -t ping` are retained compression entrypoints.
        // Only the tokens up to the command word count. The command tail belongs to the
        // command, so `hypa pane run P grep -c TOKEN` stays a mux command.
        var lastLeading = Math.Min(i, args.Length - 1);
        for (var k = 0; k <= lastLeading; k++)
        {
            var token = args[k];
            if (token is "-c" or "-t")
                return false;
            if (token.StartsWith("-c", StringComparison.Ordinal) && token.Length > 2
                && !token.StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            if (token.StartsWith("-t", StringComparison.Ordinal) && token.Length > 2
                && !token.StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (i >= args.Length)
            return false;

        return args[i] is "ping" or "snapshot" or "workspace" or "tab" or "layout" or "pane" or "agent" or "integration" or "plugin" or "notification" or "terminal" or "events" or "rpc";
    }

    internal sealed record ParsedCli(
        string Session,
        string? SocketOverride,
        int TimeoutMs,
        IReadOnlyList<string> Tokens,
        IReadOnlyDictionary<string, string> Flags,
        IReadOnlySet<string> Switches,
        string? SessionOption = null,
        IReadOnlyList<string>? EnvAssignments = null,
        string? PaneRunCommand = null);

    private enum PaneRunCapture
    {
        Idle,
        ExpectTarget,
        Command,
    }

    private static readonly HashSet<string> KnownValueFlags = new(StringComparer.Ordinal)
    {
        "--session", "--socket", "--timeout-ms", "--cwd", "--label", "--command",
        "--occupant", "--kind", "--args", "--workspace", "--pane-id", "--source", "--lines", "--run",
        "--placement",
        "--parent-pane-id", "--occupant-token", "--parent-capability", "--lease-id",
        "--attach-client-id", "--target-pane-id",
        "--step", "--until", "--timeout", "--agent-session", "--agent", "--state", "--message",
        "--agent-session-id", "--agent-session-path", "--session-start-source",
        "--tab-id", "--direction", "--ratio", "--mode", "--index",
        "--target", "--target-pane", "--source-pane", "--pane", "--before",
        "--workspace-id", "--token", "--clear-token", "--ttl-ms", "--seq",
        "--title", "--body", "--sound", "--text",
        "--name", "--keys", "--filter", "--sort", "--agent-id",
        "--plugin", "--resource", "--revision",
        "--agent-status",
        "--width", "--height",
        "--match", "--regex",
    };

    private static readonly HashSet<string> KnownSwitchFlags = new(StringComparer.Ordinal)
    {
        "--wait", "--no-pane", "--focus", "--no-focus", "--clear", "--current", "--all", "--dry-run", "-h", "--help",
    };

    internal static ParsedCli Parse(string[] args)
    {
        string? sessionOption = null;
        var socketOverride = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
        var timeoutMs = 30_000;
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        var argValues = new List<string>();
        var envValues = new List<string>();
        var paneRunCommand = new List<string>();
        var paneRun = PaneRunCapture.Idle;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (paneRun == PaneRunCapture.Command)
            {
                if (paneRunCommand.Count == 0 && token == "--")
                    continue;
                paneRunCommand.Add(token);
                continue;
            }

            if (token is "--session" && i + 1 < args.Length)
            {
                sessionOption = args[++i];
                flags[token] = sessionOption;
                continue;
            }

            if (token is "--socket" && i + 1 < args.Length)
            {
                socketOverride = args[++i];
                flags[token] = socketOverride;
                continue;
            }

            if (token is "--timeout-ms" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out timeoutMs) || timeoutMs <= 0)
                    throw new ControlPlaneCliUsageException("--timeout-ms must be a positive integer");
                flags[token] = timeoutMs.ToString();
                continue;
            }

            if (token is "--args" && i + 1 < args.Length)
            {
                argValues.Add(args[++i]);
                continue;
            }

            if (token is "--env")
            {
                if (i + 1 >= args.Length)
                    throw new ControlPlaneCliUsageException("--env requires a value");
                var raw = args[++i];
                _ = ParseEnvAssignment(raw);
                envValues.Add(raw);
                continue;
            }

            if (token is "--focus")
            {
                switches.Add(token);
                flags["--focus"] = "true";
                continue;
            }

            if (token is "--no-focus")
            {
                switches.Add(token);
                flags["--focus"] = "false";
                continue;
            }

            if (KnownValueFlags.Contains(token))
            {
                if (i + 1 >= args.Length)
                    throw new ControlPlaneCliUsageException(token + " requires a value");
                flags[token] = args[++i];
                if (paneRun == PaneRunCapture.ExpectTarget && token == "--pane-id")
                    paneRun = PaneRunCapture.Command;
                continue;
            }

            if (KnownSwitchFlags.Contains(token))
            {
                switches.Add(token);
                if (paneRun == PaneRunCapture.ExpectTarget && token == "--current")
                    paneRun = PaneRunCapture.Command;
                continue;
            }

            if (token == "--" && paneRun == PaneRunCapture.ExpectTarget)
            {
                paneRun = PaneRunCapture.Command;
                continue;
            }

            if (token.StartsWith('-'))
                throw new ControlPlaneCliUsageException("Unknown flag: " + token);

            tokens.Add(token);
            if (paneRun == PaneRunCapture.Idle
                && tokens.Count >= 2
                && tokens[^2] == "pane"
                && tokens[^1] == "run")
            {
                paneRun = PaneRunCapture.ExpectTarget;
            }
            else if (paneRun == PaneRunCapture.ExpectTarget)
            {
                paneRun = PaneRunCapture.Command;
            }
        }

        if (argValues.Count > 0)
            flags["--args-joined"] = string.Join('\u001f', argValues);

        if (switches.Contains("--dry-run")
            && (tokens.Count < 2
                || !string.Equals(tokens[0], "integration", StringComparison.Ordinal)
                || !string.Equals(tokens[1], "install", StringComparison.Ordinal)))
        {
            throw new ControlPlaneCliUsageException("--dry-run applies to integration install");
        }

        if (switches.Contains("--all"))
        {
            var uninstall = tokens.Count >= 2
                && string.Equals(tokens[0], "integration", StringComparison.Ordinal)
                && string.Equals(tokens[1], "uninstall", StringComparison.Ordinal);
            if (!uninstall)
                throw new ControlPlaneCliUsageException("--all applies to integration uninstall");
            if (tokens.Count != 2)
                throw new ControlPlaneCliUsageException("usage: hypa integration uninstall --all");
        }

        var session = AttachSessionResolver.Resolve(
            sessionOption,
            AttachClientConfig.Default);
        return new ParsedCli(
            session,
            socketOverride,
            timeoutMs,
            tokens,
            flags,
            switches,
            sessionOption,
            envValues,
            paneRun == PaneRunCapture.Command ? string.Join(' ', paneRunCommand) : null);
    }

    internal static AttachConfigResult<string> TryResolveSession(
        ParsedCli parsed,
        IAttachConfigLoader? attachConfig)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var loaded = AttachConfigErrors.LoadRequired(attachConfig ?? new FileAttachConfigLoader());
        if (!loaded.IsOk)
            return AttachConfigResult<string>.Fail(loaded.Errors);

        return AttachConfigResult<string>.Ok(
            AttachSessionResolver.Resolve(parsed.SessionOption, loaded.Value));
    }

    internal static async Task<JsonElement> WorkspaceAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed, string[] originalArgs)
    {
        if (args.Length == 0)
            throw new ControlPlaneCliUsageException(
                "workspace requires subcommand: create|list|get|focus|rename|close|move|move-block|report-metadata");

        return args[0] switch
        {
            "list" => await client.CallAsync("workspace.list").ConfigureAwait(false),
            "create" => await client.CallAsync(
                "workspace.create",
                BuildWorkspaceCreateParams(originalArgs)).ConfigureAwait(false),
            "get" => await client.CallAsync("workspace.get", new JsonObject
            {
                ["workspace_id"] = RequirePositional(args, 1, "workspace_id"),
            }).ConfigureAwait(false),
            "focus" => await client.CallAsync("workspace.focus", new JsonObject
            {
                ["workspace_id"] = RequirePositional(args, 1, "workspace_id"),
            }).ConfigureAwait(false),
            "rename" => await client.CallAsync("workspace.rename", new JsonObject
            {
                ["workspace_id"] = RequirePositional(args, 1, "workspace_id"),
                ["label"] = Flag(parsed, "--label")
                    ?? throw new ControlPlaneCliUsageException("--label is required"),
            }).ConfigureAwait(false),
            "close" => await client.CallAsync("workspace.close", new JsonObject
            {
                ["workspace_id"] = RequirePositional(args, 1, "workspace_id"),
            }).ConfigureAwait(false),
            "move" => await client.CallAsync("workspace.move", new JsonObject
            {
                ["workspace_id"] = RequirePositional(args, 1, "workspace_id"),
                ["insert_index"] = int.TryParse(Flag(parsed, "--index"), out var index)
                    ? index
                    : throw new ControlPlaneCliUsageException("--index is required"),
            }).ConfigureAwait(false),
            "move-block" => await client.CallAsync("workspace.move_block", BuildWorkspaceMoveBlockParams(originalArgs)).ConfigureAwait(false),
            "report-metadata" => await client.CallAsync(
                "workspace.report_metadata", BuildWorkspaceReportMetadataParams(args, parsed, originalArgs)).ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException($"Unknown workspace subcommand: {args[0]}"),
        };
    }

    private static async Task<JsonElement> TabAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed, string[] originalArgs)
    {
        if (args.Length == 0)
            throw new ControlPlaneCliUsageException("tab requires subcommand: create|list|get|focus|rename|move|close");

        return args[0] switch
        {
            "list" => await client.CallAsync("tab.list", BuildTabListParams(parsed)).ConfigureAwait(false),
            "create" => await client.CallAsync("tab.create", BuildTabCreateParams(parsed)).ConfigureAwait(false),
            "get" => await client.CallAsync("tab.get", new JsonObject
            {
                ["tab_id"] = RequireTabId(args, parsed),
            }).ConfigureAwait(false),
            "focus" => await client.CallAsync("tab.focus", new JsonObject
            {
                ["tab_id"] = RequireTabId(args, parsed),
            }).ConfigureAwait(false),
            "rename" => await client.CallAsync("tab.rename", new JsonObject
            {
                ["tab_id"] = RequireTabId(args, parsed),
                ["label"] = Flag(parsed, "--label")
                    ?? throw new ControlPlaneCliUsageException("--label is required"),
            }).ConfigureAwait(false),
            "move" => await client.CallAsync("tab.move", new JsonObject
            {
                ["tab_id"] = RequireTabId(args, parsed),
                ["index"] = int.TryParse(Flag(parsed, "--index"), out var idx)
                    ? idx
                    : throw new ControlPlaneCliUsageException("--index is required"),
            }).ConfigureAwait(false),
            "close" => await client.CallAsync("tab.close", new JsonObject
            {
                ["tab_id"] = RequireTabId(args, parsed),
            }).ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException($"Unknown tab subcommand: {args[0]}"),
        };
    }

    internal static async Task<JsonElement> LayoutAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed, string[] _)
    {
        if (args.Length == 0)
            throw new ControlPlaneCliUsageException("layout requires subcommand: export");

        return args[0] switch
        {
            "export" => await client.CallAsync("layout.export", BuildLayoutExportParams(parsed)).ConfigureAwait(false),
            "apply" => throw BuildLayoutApplyUsage(),
            _ => throw new ControlPlaneCliUsageException($"Unknown layout subcommand: {args[0]}"),
        };
    }

    private static async Task<JsonElement> PaneAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed, string[] originalArgs)
    {
        if (args.Length == 0)
            throw new ControlPlaneCliUsageException(
                "pane requires subcommand: create|list|get|current|send|run|read|wait-output|close|split|move|zoom|layout|swap|show|hide|report-agent|report-agent-session|report-metadata|release-agent|clear-agent-authority");

        return args[0] switch
        {
            "list" => await client.CallAsync("pane.list").ConfigureAwait(false),
            "current" => await client.CallAsync(
                ProtocolMethods.PaneCurrent, BuildPaneCurrentParams(parsed, args)).ConfigureAwait(false),
            "create" => await client.CallAsync(
                "pane.create",
                BuildPaneCreateParams(originalArgs)).ConfigureAwait(false),
            "get" => await client.CallAsync("pane.get", new JsonObject
            {
                ["pane_id"] = RequirePaneId(args, parsed),
            }).ConfigureAwait(false),
            "send" => await SendTextWithLeaseAsync(
                client,
                RequirePaneId(args, parsed),
                SendTextRemainder(args, parsed)).ConfigureAwait(false),
            "run" => await RunWithLeaseAsync(
                client,
                RequirePaneId(args, parsed),
                parsed.PaneRunCommand ?? string.Empty).ConfigureAwait(false),
            "read" => await client.CallAsync(
                "pane.read",
                BuildPaneReadParams(parsed, args)).ConfigureAwait(false),
            "wait-output" => await WaitForOutputAsync(client, parsed, args).ConfigureAwait(false),
            "close" => await client.CallAsync("pane.close", new JsonObject
            {
                ["pane_id"] = RequirePaneId(args, parsed),
            }).ConfigureAwait(false),
            "split" => await client.CallAsync("pane.split", BuildPaneSplitParams(parsed, args)).ConfigureAwait(false),
            "move" => await client.CallAsync("pane.move", BuildPaneMoveParams(parsed, args)).ConfigureAwait(false),
            "zoom" => await client.CallAsync("pane.zoom", BuildPaneZoomParams(parsed, args)).ConfigureAwait(false),
            "layout" => await client.CallAsync("pane.layout", BuildLayoutExportParams(parsed)).ConfigureAwait(false),
            "swap" => await client.CallAsync("pane.swap", BuildPaneSwapParams(parsed, args)).ConfigureAwait(false),
            "show" => await client.CallAsync("pane.show", BuildPaneShowParams(parsed, args)).ConfigureAwait(false),
            "hide" => await client.CallAsync("pane.hide", BuildPaneHideParams(parsed, args)).ConfigureAwait(false),
            "report-agent" => await client.CallAsync(
                "pane.report_agent", BuildPaneReportAgentParams(args, parsed)).ConfigureAwait(false),
            "report-agent-session" => await client.CallAsync(
                "pane.report_agent_session", BuildPaneReportAgentSessionParams(args, parsed)).ConfigureAwait(false),
            "report-metadata" => await client.CallAsync(
                "pane.report_metadata", BuildPaneReportMetadataParams(args, parsed, originalArgs)).ConfigureAwait(false),
            "release-agent" => await client.CallAsync(
                "pane.release_agent", BuildPaneReleaseAgentParams(args, parsed)).ConfigureAwait(false),
            "clear-agent-authority" => await client.CallAsync(
                "pane.clear_agent_authority", BuildPaneClearAgentAuthorityParams(args, parsed)).ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException($"Unknown pane subcommand: {args[0]}"),
        };
    }

    internal static Task<JsonElement> PluginAsync(ControlPlaneClient client, string[] args) =>
        PluginAsync(client, args, BundledPluginService.CreateSystem(), Environment.ProcessPath);

    internal static Task<JsonElement> PluginAsync(ControlPlaneClient client, string[] args, ParsedCli parsed) =>
        PluginAsync(client, args, parsed, BundledPluginService.CreateSystem(), Environment.ProcessPath);

    internal static Task<JsonElement> PluginAsync(
        ControlPlaneClient client,
        string[] args,
        IBundledPluginService bundled,
        string? executablePath)
    {
        ArgumentNullException.ThrowIfNull(args);
        var parsed = Parse(PrependDummyVerb(["plugin", .. args]));
        return PluginAsync(client, args, parsed, bundled, executablePath);
    }

    internal static async Task<JsonElement> PluginAsync(
        ControlPlaneClient client,
        string[] args,
        ParsedCli parsed,
        IBundledPluginService bundled,
        string? executablePath)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(bundled);
        if (args.Length == 0)
        {
            throw new ControlPlaneCliUsageException(
                "plugin requires subcommand: link|list|unlink|enable|disable|action|log|pane|bundled|resource");
        }

        return args[0] switch
        {
            "link" => await PluginLinkAsync(client, args[1..]).ConfigureAwait(false),
            "list" => await client.CallAsync(
                ProtocolMethods.PluginList,
                PluginOptionalId(args[1..], parsed, allowBare: true)).ConfigureAwait(false),
            "unlink" => await client.CallAsync(
                ProtocolMethods.PluginUnlink,
                new JsonObject { ["plugin_id"] = RequirePluginId(args, "unlink") }).ConfigureAwait(false),
            "enable" => await client.CallAsync(
                ProtocolMethods.PluginEnable,
                new JsonObject { ["plugin_id"] = RequirePluginId(args, "enable") }).ConfigureAwait(false),
            "disable" => await client.CallAsync(
                ProtocolMethods.PluginDisable,
                new JsonObject { ["plugin_id"] = RequirePluginId(args, "disable") }).ConfigureAwait(false),
            "action" => await PluginActionAsync(client, args[1..], parsed).ConfigureAwait(false),
            "log" or "logs" => await client.CallAsync(
                ProtocolMethods.PluginLogList,
                PluginOptionalId(args[1..], parsed, allowBare: false)).ConfigureAwait(false),
            "pane" => await PluginPaneAsync(client, args[1..], parsed).ConfigureAwait(false),
            "bundled" => await PluginBundledAsync(client, args[1..], bundled, executablePath)
                .ConfigureAwait(false),
            "resource" => await PluginResourceAsync(client, args[1..], parsed).ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException("Unknown plugin subcommand: " + args[0]),
        };
    }

    internal static async Task<JsonElement> PluginBundledAsync(
        ControlPlaneClient client,
        string[] args,
        IBundledPluginService bundled,
        string? executablePath)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(bundled);
        if (args.Length == 0)
        {
            throw new ControlPlaneCliUsageException(
                "plugin bundled requires subcommand: install|uninstall|status");
        }

        return args[0] switch
        {
            "install" => await PluginBundledInstallAsync(client, bundled, executablePath, args[1..]).ConfigureAwait(false),
            "uninstall" => await PluginBundledUninstallAsync(client, bundled, args[1..]).ConfigureAwait(false),
            "status" => await PluginBundledStatusAsync(client, bundled, args[1..]).ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException("Unknown plugin bundled subcommand: " + args[0]),
        };
    }

    private static async Task<JsonElement> PluginBundledInstallAsync(
        ControlPlaneClient client,
        IBundledPluginService bundled,
        string? executablePath,
        string[] rest)
    {
        _ = BundledPluginId(rest);
        var staged = bundled.StageAnnotate(executablePath ?? "");
        if (!staged.IsOk)
            throw BundledPluginFault(staged.Error);
        return await PluginLinkAsync(client, [staged.Value.PluginRoot]).ConfigureAwait(false);
    }

    private static async Task<JsonElement> PluginBundledUninstallAsync(
        ControlPlaneClient client,
        IBundledPluginService bundled,
        string[] rest)
    {
        var pluginId = BundledPluginId(rest);
        var unlinked = await client.CallAsync(
                ProtocolMethods.PluginUnlink,
                new JsonObject { ["plugin_id"] = pluginId })
            .ConfigureAwait(false);
        var removed = bundled.RemoveAnnotate();
        if (!removed.IsOk)
            throw BundledPluginFault(removed.Error);

        var payload = new JsonObject
        {
            ["plugin_id"] = pluginId,
            ["plugin_root"] = removed.Value.PluginRoot,
            ["unlinked"] = unlinked.TryGetProperty("removed", out var flag) && flag.ValueKind == JsonValueKind.True,
            ["removed"] = removed.Value.Removed,
        };
        return JsonElementFromObject(payload);
    }

    private static async Task<JsonElement> PluginBundledStatusAsync(
        ControlPlaneClient client,
        IBundledPluginService bundled,
        string[] rest)
    {
        var pluginId = BundledPluginId(rest);
        var disk = bundled.InspectAnnotate();
        var listed = await client.CallAsync(
                ProtocolMethods.PluginList,
                new JsonObject { ["plugin_id"] = pluginId })
            .ConfigureAwait(false);
        var linked = false;
        var enabled = false;
        if (listed.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in plugins.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("plugin_id", out var id)
                    || id.GetString() != pluginId)
                {
                    continue;
                }

                linked = true;
                enabled = item.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.True;
                break;
            }
        }

        var payload = new JsonObject
        {
            ["plugin_id"] = disk.PluginId,
            ["plugin_root"] = disk.PluginRoot,
            ["staged"] = disk.Staged,
            ["linked"] = linked,
            ["enabled"] = enabled,
        };
        return JsonElementFromObject(payload);
    }

    private static JsonElement JsonElementFromObject(JsonObject payload)
    {
        using var doc = JsonDocument.Parse(payload.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static string BundledPluginId(string[] rest)
    {
        if (rest.Length != 1)
        {
            throw new ControlPlaneCliUsageException(
                "usage: hypa plugin bundled install|uninstall|status <plugin-id>");
        }

        return rest[0] switch
        {
            BundledPluginLayout.AnnotatePluginId => BundledPluginLayout.AnnotatePluginId,
            _ => throw new ControlPlaneCliUsageException("Unknown bundled plugin: " + rest[0]),
        };
    }

    private static Exception BundledPluginFault(PluginError error)
    {
        if (error.Code == PluginError.InvalidCommand)
            return new ControlPlaneCliUsageException(error.Message);
        return new ControlPlaneException(ProtocolErrorCodes.InvalidParams, error.Message, error.Code);
    }

    private static async Task<JsonElement> PluginLinkAsync(ControlPlaneClient client, string[] args)
    {
        if (args.Length == 0)
            throw new ControlPlaneCliUsageException("usage: hypa plugin link <path> [--disabled]");
        var path = args[0];
        var enabled = true;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--disabled")
                enabled = false;
            else if (args[i] is "--enabled" or "--yes")
                enabled = args[i] != "--disabled";
            else
                throw new ControlPlaneCliUsageException("unknown option: " + args[i]);
        }

        return await client.CallAsync(
                ProtocolMethods.PluginLink,
                new JsonObject { ["path"] = path, ["enabled"] = enabled })
            .ConfigureAwait(false);
    }

    private static async Task<JsonElement> PluginActionAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed)
    {
        if (args.Length == 0)
            throw new ControlPlaneCliUsageException("plugin action requires subcommand: list|invoke");
        return args[0] switch
        {
            "list" => await client.CallAsync(
                ProtocolMethods.PluginActionList,
                PluginOptionalId(args[1..], parsed, allowBare: false)).ConfigureAwait(false),
            "invoke" => await PluginActionInvokeAsync(client, args[1..], parsed).ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException("Unknown plugin action subcommand: " + args[0]),
        };
    }

    private static Task<JsonElement> PluginActionInvokeAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed)
    {
        if (args.Length == 0)
            throw new ControlPlaneCliUsageException(
                "usage: hypa plugin action invoke <action_id> [--plugin ID] [--resource ID] [--revision N]");
        var actionId = args[0];
        var pluginId = Flag(parsed, "--plugin");
        var resourceId = Flag(parsed, "--resource");
        long? revision = null;
        if (long.TryParse(Flag(parsed, "--revision"), out var parsedRevision))
            revision = parsedRevision;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--plugin" or "--resource" or "--revision")
            {
                if (i + 1 >= args.Length)
                    throw new ControlPlaneCliUsageException(args[i] + " requires a value");
                i++;
            }
            else
                throw new ControlPlaneCliUsageException("unknown option: " + args[i]);
        }

        var p = new JsonObject { ["action_id"] = actionId };
        if (!string.IsNullOrWhiteSpace(pluginId))
            p["plugin_id"] = pluginId;
        if (!string.IsNullOrWhiteSpace(resourceId))
            p["resource_id"] = resourceId;
        if (revision is not null)
            p["revision"] = revision.Value;
        return client.CallAsync(ProtocolMethods.PluginActionInvoke, p);
    }

    private static async Task<JsonElement> PluginPaneAsync(
        ControlPlaneClient client,
        string[] args,
        ParsedCli parsed)
    {
        if (args.Length == 0)
            throw new ControlPlaneCliUsageException("plugin pane requires subcommand: open|focus|close|send-text");
        return args[0] switch
        {
            "open" => await client.CallAsync(
                    ProtocolMethods.PluginPaneOpen,
                    BuildPluginPaneOpenParams(args[1..], parsed))
                .ConfigureAwait(false),
            "focus" => await client.CallAsync(
                ProtocolMethods.PluginPaneFocus,
                new JsonObject { ["pane_id"] = RequirePositional(args, 1, "usage: hypa plugin pane focus <pane_id>") })
                .ConfigureAwait(false),
            "close" => await client.CallAsync(
                ProtocolMethods.PluginPaneClose,
                new JsonObject { ["pane_id"] = RequirePositional(args, 1, "usage: hypa plugin pane close <pane_id>") })
                .ConfigureAwait(false),
            "send-text" => await client.CallAsync(
                    ProtocolMethods.PluginPaneSendText,
                    BuildPluginPaneSendTextParams(args[1..], parsed))
                .ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException("Unknown plugin pane subcommand: " + args[0]),
        };
    }

    private static async Task<JsonElement> PluginResourceAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed)
    {
        if (args.Length == 0)
        {
            throw new ControlPlaneCliUsageException(
                "plugin resource requires subcommand: list|get|publish|remove");
        }

        return args[0] switch
        {
            "list" => await client.CallAsync(
                ProtocolMethods.PluginResourceList,
                PluginOptionalId(args[1..], parsed, allowBare: true)).ConfigureAwait(false),
            "get" => await client.CallAsync(
                ProtocolMethods.PluginResourceGet,
                new JsonObject
                {
                    ["resource_id"] = RequirePositional(
                        args, 1, "usage: hypa plugin resource get <resource_id>"),
                }).ConfigureAwait(false),
            "publish" => await client.CallAsync(
                ProtocolMethods.PluginResourcePublish,
                BuildPluginResourcePublishParams(args[1..])).ConfigureAwait(false),
            "remove" => await client.CallAsync(
                ProtocolMethods.PluginResourceRemove,
                new JsonObject
                {
                    ["resource_id"] = RequirePositional(
                        args, 1, "usage: hypa plugin resource remove <resource_id>"),
                }).ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException("Unknown plugin resource subcommand: " + args[0]),
        };
    }

    internal static JsonObject BuildPluginResourcePublishParams(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ControlPlaneCliUsageException(
                "usage: hypa plugin resource publish <envelope-json>");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(args[0]);
        }
        catch (JsonException ex)
        {
            throw new ControlPlaneCliUsageException("publish envelope is not JSON: " + ex.Message);
        }

        if (node is not JsonObject obj)
            throw new ControlPlaneCliUsageException("publish envelope must be a JSON object");
        return obj;
    }

    /// <summary>
    /// </summary>
    internal static JsonObject BuildPluginPaneOpenParams(string[] args, ParsedCli parsed)
    {
        if (args.Length != 2)
        {
            throw new ControlPlaneCliUsageException(
                "usage: hypa plugin pane open <plugin_id> <entrypoint> [--placement overlay|popup|split|tab|zoomed] [--width SIZE] [--height SIZE] [--cwd PATH] [--env KEY=VALUE] [--target-pane PANE] [--focus|--no-focus]");
        }

        var p = new JsonObject
        {
            ["plugin_id"] = args[0],
            ["entrypoint"] = args[1],
        };
        var placement = Flag(parsed, "--placement");
        if (!string.IsNullOrWhiteSpace(placement))
            p["placement"] = placement;
        AssignPopupDimension(p, "width", Flag(parsed, "--width"));
        AssignPopupDimension(p, "height", Flag(parsed, "--height"));
        var cwd = Flag(parsed, "--cwd");
        if (!string.IsNullOrWhiteSpace(cwd))
            p["cwd"] = cwd;
        var workspace = Flag(parsed, "--workspace");
        if (!string.IsNullOrWhiteSpace(workspace))
            p["workspace_id"] = workspace;
        var targetPane = Flag(parsed, "--target-pane");
        if (!string.IsNullOrWhiteSpace(targetPane))
            p["target_pane_id"] = targetPane;
        var direction = Flag(parsed, "--direction");
        if (!string.IsNullOrWhiteSpace(direction))
            p["direction"] = direction;
        p["focus"] = Flag(parsed, "--focus") != "false";
        var env = ParseEnvAssignments(parsed);
        if (env.Count > 0)
            p["env"] = env;
        return p;
    }

    internal static JsonObject BuildPluginPaneSendTextParams(string[] args, ParsedCli parsed)
    {
        if (args.Length != 1)
        {
            throw new ControlPlaneCliUsageException(
                "usage: hypa plugin pane send-text <pane_id> --text TEXT");
        }

        var text = Flag(parsed, "--text");
        if (text is null)
            throw new ControlPlaneCliUsageException("--text is required");

        return new JsonObject
        {
            ["pane_id"] = args[0],
            ["text"] = text,
        };
    }

    private static void AssignPopupDimension(JsonObject payload, string name, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var cells))
            payload[name] = cells;
        else
            payload[name] = raw;
    }

    private static JsonObject ParseEnvAssignments(ParsedCli parsed)
    {
        var env = new JsonObject();
        var assignments = parsed.EnvAssignments;
        if (assignments is null || assignments.Count == 0)
            return env;

        foreach (var raw in assignments)
        {
            var (key, value) = ParseEnvAssignment(raw);
            env[key] = value;
        }

        return env;
    }

    /// <summary>
    /// KEY=VALUE, empty key forbidden, no NUL.
    /// </summary>
    internal static (string Key, string Value) ParseEnvAssignment(string raw)
    {
        var split = raw.IndexOf('=');
        if (split < 0)
            throw new ControlPlaneCliUsageException("env must use KEY=VALUE");
        var key = raw[..split];
        var value = raw[(split + 1)..];
        if (key.Length == 0)
            throw new ControlPlaneCliUsageException("env key must not be empty");
        if (key.Contains('\0') || value.Contains('\0'))
            throw new ControlPlaneCliUsageException("env must not contain NUL bytes");
        return (key, value);
    }

    private static JsonObject? PluginOptionalId(string[] args, ParsedCli parsed, bool allowBare)
    {
        string? pluginId = Flag(parsed, "--plugin");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--plugin")
            {
                if (i + 1 >= args.Length)
                    throw new ControlPlaneCliUsageException("--plugin requires a value");
                pluginId = args[++i];
            }
            else if (allowBare && pluginId is null && !args[i].StartsWith('-'))
                pluginId = args[i];
            else if (args[i] is "--json")
                continue;
            else
                throw new ControlPlaneCliUsageException("unknown option: " + args[i]);
        }

        return pluginId is null ? null : new JsonObject { ["plugin_id"] = pluginId };
    }

    private static string RequirePluginId(string[] args, string action)
    {
        if (args.Length != 2)
            throw new ControlPlaneCliUsageException("usage: hypa plugin " + action + " <plugin_id>");
        return args[1];
    }

    private static string RequirePositional(string[] args, int index, string usage)
    {
        if (args.Length <= index)
            throw new ControlPlaneCliUsageException(usage);
        return args[index];
    }

    private static async Task<JsonElement> IntegrationAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed)
    {
        if (args.Length == 0)
            throw new ControlPlaneCliUsageException("integration requires subcommand: install|uninstall|status");

        var planOnly = parsed.Switches.Contains("--dry-run");
        if (planOnly && args[0] != "install")
            throw new ControlPlaneCliUsageException("--dry-run applies to install");
        var removeAll = parsed.Switches.Contains("--all");
        return args[0] switch
        {
            "install" => await client.CallAsync(
                ProtocolMethods.IntegrationInstall,
                InstallParams(args, planOnly)).ConfigureAwait(false),
            "uninstall" => await client.CallAsync(
                ProtocolMethods.IntegrationUninstall,
                UninstallParams(args, removeAll)).ConfigureAwait(false),
            "status" => await client.CallAsync(ProtocolMethods.IntegrationList).ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException("Unknown integration subcommand: " + args[0]),
        };
    }

    private static JsonObject InstallParams(string[] args, bool planOnly)
    {
        var parameters = args.Length == 1
            ? new JsonObject { ["detected"] = true }
            : new JsonObject { ["target"] = RequireIntegrationTarget(args, "install") };
        if (planOnly)
            parameters["plan"] = true;
        return parameters;
    }

    private static JsonObject UninstallParams(string[] args, bool removeAll)
    {
        if (removeAll)
            return new JsonObject { ["detected"] = true };
        return new JsonObject { ["target"] = RequireIntegrationTarget(args, "uninstall") };
    }

    /// <summary>Prints the same consent text the integrations page shows.</summary>
    private static void PrintIntegrationPlan(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("outcomes", out var outcomes)
            && outcomes.ValueKind == JsonValueKind.Array)
        {
            var first = true;
            foreach (var item in outcomes.EnumerateArray())
            {
                if (!first)
                    Console.WriteLine();
                first = false;
                WritePlanMessages(item);
            }

            return;
        }

        WritePlanMessages(result);
    }

    private static void WritePlanMessages(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in messages.EnumerateArray())
            {
                var text = line.GetString();
                if (!string.IsNullOrEmpty(text))
                    Console.WriteLine(text);
            }
        }
    }

    private static bool HasPerTargetOutcomes(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("outcomes", out var outcomes)
        && outcomes.ValueKind == JsonValueKind.Array;

    private static void PrintDetectedIntegration(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in messages.EnumerateArray())
            {
                var text = line.GetString();
                if (!string.IsNullOrEmpty(text))
                    Console.WriteLine(text);
            }

            return;
        }

        Console.WriteLine(result.ValueKind == JsonValueKind.Undefined ? "{}" : result.GetRawText());
    }

    /// <summary>
    /// Exit 1 is the runtime-error code. A multi-target command prints every target,
    /// then uses that code when any target failed.
    /// </summary>
    private static int DetectedIntegrationExitCode(JsonElement result, string action)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("outcomes", out var outcomes)
            || outcomes.ValueKind != JsonValueKind.Array)
            return 0;

        foreach (var item in outcomes.EnumerateArray())
        {
            if (item.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                Console.Error.WriteLine(
                    "error " + ProtocolErrorCodes.InternalError + ": integration " + action + " failed");
                return 1;
            }
        }

        return 0;
    }

    private static string RequireIntegrationTarget(string[] args, string action)
    {
        if (args.Length != 2)
        {
            var usage = "usage: hypa integration " + action
                + " <pi|omp|claude|codex|copilot|devin|droid|kimi|opencode|kilo|hermes|qodercli|qwen|cursor|mastracode|antigravity-cli|grok>";
            if (action == "uninstall")
                usage += "\nusage: hypa integration uninstall --all";
            throw new ControlPlaneCliUsageException(usage);
        }

        var target = args[1];
        if (!OfficialIntegrationTargets.TryParse(target, out _))
            throw new ControlPlaneCliUsageException("unknown integration target: " + target);
        return target;
    }

    private static async Task<JsonElement> NotificationAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed)
    {
        if (args.Length == 0 || args[0] is not "show")
            throw new ControlPlaneCliUsageException("notification requires subcommand: show");
        if (!parsed.Flags.TryGetValue("--title", out var title) || string.IsNullOrWhiteSpace(title))
            throw new ControlPlaneCliUsageException("notification show requires --title TEXT");

        var p = new JsonObject { ["title"] = title };
        if (parsed.Flags.TryGetValue("--body", out var body) && !string.IsNullOrWhiteSpace(body))
            p["body"] = body;
        if (parsed.Flags.TryGetValue("--source", out var source) && !string.IsNullOrWhiteSpace(source))
            p["source"] = source;
        if (parsed.Flags.TryGetValue("--sound", out var sound) && !string.IsNullOrWhiteSpace(sound))
            p["sound"] = sound;
        if (parsed.Flags.TryGetValue("--pane-id", out var paneId) && !string.IsNullOrWhiteSpace(paneId))
            p["pane_id"] = paneId;
        return await client.CallAsync("notification.show", p).ConfigureAwait(false);
    }

    private static async Task<JsonElement> TerminalTitleAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed)
    {
        if (args.Length < 2 || args[0] is not "title")
            throw new ControlPlaneCliUsageException("terminal requires subcommand: title set|clear");

        return args[1] switch
        {
            "set" => await client.CallAsync(
                "client.window_title.set",
                new JsonObject { ["title"] = RequireTitle(parsed) }).ConfigureAwait(false),
            "clear" => await client.CallAsync("client.window_title.clear").ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException("terminal title requires subcommand: set|clear"),
        };
    }

    private static async Task<JsonElement> EventsAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed)
    {
        if (args.Length == 0 || args[0] is not "wait")
            throw new ControlPlaneCliUsageException("events requires subcommand: wait");

        var body = BuildEventsWaitParams(parsed, args);
        var waitMs = body["timeout_ms"]!.GetValue<int>();
        var callCap = TimeSpan.FromMilliseconds(Math.Max(parsed.TimeoutMs, waitMs + 1_000));
        return await client.CallAsync(ProtocolMethods.EventsWait, body, timeout: callCap)
            .ConfigureAwait(false);
    }

    private static string RequireTitle(ParsedCli parsed)
    {
        if (!parsed.Flags.TryGetValue("--title", out var title) || string.IsNullOrWhiteSpace(title))
            throw new ControlPlaneCliUsageException("terminal title set requires --title TEXT");
        return title;
    }

    private static async Task<JsonElement> AgentAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed, string[] originalArgs)
    {
        if (args.Length == 0)
            throw new ControlPlaneCliUsageException(
                "agent requires subcommand: list|status|wait|prompt|read|start|explain|rename|focus|send-keys|attach|view");

        var wait = ResolveAgentWaitFlags(parsed);
        var callCap = TimeSpan.FromMilliseconds(Math.Max(parsed.TimeoutMs, wait.TimeoutMs + 1_000));

        return args[0] switch
        {
            "list" => await client.CallAsync("agent.list").ConfigureAwait(false),
            "status" or "get" => await client.CallAsync("agent.get", new JsonObject
            {
                ["pane_id"] = RequirePaneId(args, parsed),
            }).ConfigureAwait(false),
            "wait" => await client.CallAsync(
                "agent.wait",
                BuildAgentWaitParams(parsed, args),
                timeout: callCap).ConfigureAwait(false),
            "prompt" => await PromptWithLeaseAsync(
                client,
                RequirePaneId(args, parsed),
                CommandRemainder(args, parsed),
                wait.Wait,
                wait.Until,
                wait.TimeoutMs,
                callCap).ConfigureAwait(false),
            "read" => await client.CallAsync(
                "agent.read",
                BuildAgentReadParams(parsed, args)).ConfigureAwait(false),
            "start" => await client.CallAsync("agent.start", BuildAgentStartParams(parsed, args)).ConfigureAwait(false),
            "explain" => await client.CallAsync(
                ProtocolMethods.AgentExplain,
                BuildAgentTargetParams(parsed, args)).ConfigureAwait(false),
            "rename" => await client.CallAsync(
                ProtocolMethods.AgentRename,
                BuildAgentRenameParams(parsed, args)).ConfigureAwait(false),
            "focus" => await client.CallAsync(
                ProtocolMethods.AgentFocus,
                BuildAgentTargetParams(parsed, args)).ConfigureAwait(false),
            "send-keys" => await client.CallAsync(
                ProtocolMethods.AgentSendKeys,
                BuildAgentSendKeysParams(parsed, args, originalArgs)).ConfigureAwait(false),
            "view" => await AgentViewAsync(client, args, parsed).ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException($"Unknown agent subcommand: {args[0]}"),
        };
    }

    private static string RequireTabId(string[] args, ParsedCli parsed)
    {
        if (parsed.Flags.TryGetValue("--tab-id", out var flagged) && !string.IsNullOrWhiteSpace(flagged))
            return flagged;
        return RequirePositional(args, 1, "tab_id");
    }

    private static string RequirePaneId(string[] args, ParsedCli parsed)
    {
        var current = TryCurrentPaneId(parsed);
        if (parsed.Flags.TryGetValue("--pane-id", out var flagged) && !string.IsNullOrWhiteSpace(flagged))
        {
            if (current is not null)
                throw new ControlPlaneCliUsageException("--current cannot be combined with a pane id");
            return flagged;
        }

        if (current is not null)
            return current;
        return RequirePositional(args, 1, "pane_id");
    }

    /// <summary>
    /// <c>--current</c> reads <c>HYPA_PANE_ID</c> in this process.
    /// Returns null when the flag is absent.
    /// </summary>
    private static string? TryCurrentPaneId(ParsedCli parsed)
    {
        if (!parsed.Switches.Contains("--current"))
            return null;
        var env = Environment.GetEnvironmentVariable(PaneIdEnvironment.HypaPaneId);
        if (string.IsNullOrWhiteSpace(env))
            throw new ControlPlaneCliUsageException("HYPA_PANE_ID is not set");
        return env.Trim();
    }

    private static bool PaneSelectorSkipsPositional(ParsedCli parsed) =>
        parsed.Switches.Contains("--current")
        || parsed.Flags.ContainsKey("--pane-id")
        || parsed.Flags.ContainsKey("--agent-id");

    private static string SendTextRemainder(string[] args, ParsedCli parsed) =>
        CommandRemainder(args, parsed);

    private static string CommandRemainder(string[] args, ParsedCli parsed)
    {
        var start = PaneSelectorSkipsPositional(parsed) ? 1 : 2;
        return string.Join(' ', args.Skip(start).Where(t => !t.StartsWith('-')));
    }

    private static IReadOnlyList<string> RequirePositionals(string[] args, int startIndex)
    {
        var list = new List<string>();
        for (var i = startIndex; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
                continue;
            list.Add(args[i]);
        }

        return list;
    }

    internal static JsonObject BuildWorkspaceCreateParams(string[] args)
    {
        var parsed = Parse(PrependDummyVerb(args));
        var obj = new JsonObject
        {
            ["label"] = FlagOrArg(parsed, args, "--label"),
            ["command"] = FlagOrArg(parsed, args, "--command") ?? "",
            ["args"] = ToJsonArray(RepeatableArg(args, "--args")),
            ["create_pane"] = !HasFlag(args, "--no-pane") && !parsed.Switches.Contains("--no-pane"),
        };
        var cwd = FlagOrArg(parsed, args, "--cwd");
        if (!string.IsNullOrWhiteSpace(cwd))
            obj["cwd"] = cwd;
        return obj;
    }

    internal static JsonObject BuildPaneReadParams(ParsedCli parsed, string[] tokens) =>
        new()
        {
            ["pane_id"] = RequirePaneId(tokens, parsed),
            ["source"] = ProtocolPaneReadSources.NormalizeCli(
                Flag(parsed, "--source") ?? ProtocolPaneReadSources.Recent),
            ["lines"] = int.TryParse(Flag(parsed, "--lines"), out var n) ? n : 100,
        };

    internal static JsonObject BuildAgentWaitParams(ParsedCli parsed, string[] tokens)
    {
        var wait = ResolveAgentWaitFlags(parsed);
        return new JsonObject
        {
            ["pane_id"] = RequirePaneId(tokens, parsed),
            ["until"] = wait.Until,
            ["timeout_ms"] = wait.TimeoutMs,
        };
    }

    internal static JsonObject BuildEventsWaitParams(ParsedCli parsed, string[] tokens)
    {
        var status = Flag(parsed, "--agent-status");
        if (string.IsNullOrWhiteSpace(status))
            throw new ControlPlaneCliUsageException("events wait requires --agent-status");

        var timeoutMs = int.TryParse(Flag(parsed, "--timeout"), out var t) ? t : 60_000;
        return new JsonObject
        {
            ["match_event"] = new JsonObject
            {
                ["event"] = EventsWaitMatch.PaneAgentStatusChanged,
                ["pane_id"] = RequirePaneId(tokens, parsed),
                ["agent_status"] = status,
            },
            ["timeout_ms"] = timeoutMs,
        };
    }

    internal static JsonObject BuildAgentReadParams(ParsedCli parsed, string[] tokens) =>
        new()
        {
            ["pane_id"] = RequirePaneId(tokens, parsed),
            ["source"] = ProtocolPaneReadSources.NormalizeCli(
                Flag(parsed, "--source") ?? ProtocolPaneReadSources.Recent),
        };

    internal static (bool Wait, string Until, int TimeoutMs) ResolveAgentWaitFlags(ParsedCli parsed)
    {
        var timeoutMs = int.TryParse(Flag(parsed, "--timeout"), out var t) ? t : 60_000;
        return (
            Switch(parsed, "--wait"),
            Flag(parsed, "--until") ?? "blocked",
            timeoutMs);
    }

    private static string? Flag(ParsedCli parsed, string name) =>
        parsed.Flags.TryGetValue(name, out var v) ? v : null;

    private static bool Switch(ParsedCli parsed, string name) =>
        parsed.Switches.Contains(name);

    internal static JsonObject BuildTabCreateParams(ParsedCli parsed) =>
        new()
        {
            ["workspace_id"] = Flag(parsed, "--workspace")
                ?? throw new ControlPlaneCliUsageException("--workspace is required"),
            ["label"] = Flag(parsed, "--label"),
            ["command"] = Flag(parsed, "--command") ?? "",
            ["args"] = parsed.Flags.TryGetValue("--args-joined", out var joined)
                ? ToJsonArray(joined.Split('\u001f'))
                : new JsonArray(),
            ["create_pane"] = !parsed.Switches.Contains("--no-pane"),
            ["focus"] = !parsed.Switches.Contains("--no-focus"),
        };

    internal static JsonObject BuildTabListParams(ParsedCli parsed)
    {
        var obj = new JsonObject();
        var ws = Flag(parsed, "--workspace");
        if (ws is not null)
            obj["workspace_id"] = ws;
        return obj;
    }

    internal static JsonObject BuildLayoutExportParams(ParsedCli parsed)
    {
        var obj = new JsonObject();
        var tab = Flag(parsed, "--tab-id");
        var pane = Flag(parsed, "--pane-id");
        var current = TryCurrentPaneId(parsed);
        if (current is not null && !string.IsNullOrWhiteSpace(pane))
            throw new ControlPlaneCliUsageException("--current cannot be combined with a pane id");
        pane ??= current;
        if (tab is not null)
            obj["tab_id"] = tab;
        if (pane is not null)
            obj["pane_id"] = pane;
        return obj;
    }

    internal static JsonObject BuildPaneCurrentParams(ParsedCli parsed, string[] args)
    {
        var obj = new JsonObject();
        var current = TryCurrentPaneId(parsed);
        string? flagged = parsed.Flags.TryGetValue("--pane-id", out var flag) && !string.IsNullOrWhiteSpace(flag)
            ? flag
            : null;
        if (current is not null && flagged is not null)
            throw new ControlPlaneCliUsageException("--current cannot be combined with a pane id");
        var positional = current is null && args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
        var id = current ?? flagged ?? positional;
        if (id is null)
        {
            var env = Environment.GetEnvironmentVariable(PaneIdEnvironment.HypaPaneId);
            if (!string.IsNullOrWhiteSpace(env))
                id = env.Trim();
        }

        if (id is not null)
            obj["caller_pane_id"] = id;
        return obj;
    }

    internal static JsonObject BuildPaneWaitOutputParams(ParsedCli parsed, string[] args)
    {
        var match = Flag(parsed, "--match");
        var regex = Flag(parsed, "--regex");
        if (string.IsNullOrEmpty(match) && string.IsNullOrEmpty(regex))
            throw new ControlPlaneCliUsageException("missing required --match or --regex");
        if (!string.IsNullOrEmpty(match) && !string.IsNullOrEmpty(regex))
            throw new ControlPlaneCliUsageException("--match and --regex are mutually exclusive");

        var obj = new JsonObject
        {
            ["pane_id"] = RequirePaneId(args, parsed),
            ["source"] = ProtocolPaneReadSources.NormalizeCli(
                Flag(parsed, "--source") ?? ProtocolPaneReadSources.Recent),
        };
        if (!string.IsNullOrEmpty(match))
            obj["match"] = match;
        if (!string.IsNullOrEmpty(regex))
            obj["match_regex"] = regex;
        if (Flag(parsed, "--lines") is { } linesText)
        {
            if (!int.TryParse(linesText, out var lines) || lines <= 0)
                throw new ControlPlaneCliUsageException("--lines must be a positive integer");
            obj["lines"] = lines;
        }

        if (Flag(parsed, "--timeout") is { } timeoutText)
        {
            if (!int.TryParse(timeoutText, out var timeoutMs) || timeoutMs < 0)
                throw new ControlPlaneCliUsageException("--timeout must be a non-negative integer");
            obj["timeout_ms"] = timeoutMs;
        }

        return obj;
    }

    internal static JsonObject BuildPaneSplitParams(ParsedCli parsed, string[] args)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = RequirePaneId(args, parsed),
            ["direction"] = Flag(parsed, "--direction")
                ?? throw new ControlPlaneCliUsageException("--direction is required"),
            ["ratio"] = double.TryParse(Flag(parsed, "--ratio"), out var r) ? r : 0.5,
            ["command"] = Flag(parsed, "--command") ?? "",
            ["label"] = Flag(parsed, "--label"),
            ["focus"] = !parsed.Switches.Contains("--no-focus"),
        };
        if (parsed.Flags.TryGetValue("--args-joined", out var joined))
            obj["args"] = ToJsonArray(joined.Split('\u001f'));
        return obj;
    }

    internal static JsonObject BuildPaneMoveParams(ParsedCli parsed, string[] args) =>
        new()
        {
            ["pane_id"] = RequirePaneId(args, parsed),
            ["destination"] = Flag(parsed, "--direction") is "new_tab" ? "new_tab" : "tab",
            ["tab_id"] = Flag(parsed, "--tab-id"),
            ["workspace_id"] = Flag(parsed, "--workspace"),
            ["split"] = Flag(parsed, "--direction"),
        };

    internal static JsonObject BuildPaneSwapParams(ParsedCli parsed, string[] args)
    {
        var direction = Flag(parsed, "--direction");
        var target = Flag(parsed, "--target") ?? Flag(parsed, "--target-pane");
        if (string.IsNullOrWhiteSpace(direction) && string.IsNullOrWhiteSpace(target))
        {
            throw new ControlPlaneCliUsageException(
                "--direction or --target is required");
        }

        var obj = new JsonObject();
        var pane = Flag(parsed, "--pane-id")
            ?? Flag(parsed, "--source-pane")
            ?? Flag(parsed, "--pane");
        var current = TryCurrentPaneId(parsed);
        if (current is not null && !string.IsNullOrWhiteSpace(pane))
            throw new ControlPlaneCliUsageException("--current cannot be combined with a pane id");
        if (pane is null && current is null && args.Length > 1 && !args[1].StartsWith('-'))
            pane = args[1];
        pane ??= current;

        if (!string.IsNullOrWhiteSpace(pane))
            obj["pane_id"] = pane;
        if (!string.IsNullOrWhiteSpace(direction))
            obj["direction"] = direction;
        if (!string.IsNullOrWhiteSpace(target))
            obj["target_pane_id"] = target;
        return obj;
    }

    internal static JsonObject BuildPaneZoomParams(ParsedCli parsed, string[] args)
    {
        var obj = new JsonObject
        {
            ["mode"] = Flag(parsed, "--mode") ?? "toggle",
        };
        if (parsed.Switches.Contains("--current")
            || parsed.Flags.ContainsKey("--pane-id")
            || args.Length > 1)
            obj["pane_id"] = RequirePaneId(args, parsed);
        return obj;
    }

    internal static JsonObject BuildAgentStartParams(ParsedCli parsed, string[] args)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = RequirePaneId(args, parsed),
        };
        var kind = Flag(parsed, "--kind");
        var occupant = Flag(parsed, "--occupant");
        var command = Flag(parsed, "--command");
        if (kind is not null && occupant is not null)
            throw new ControlPlaneCliUsageException("kind and occupant are mutually exclusive");
        if (kind is not null && command is not null)
            throw new ControlPlaneCliUsageException("kind and command are mutually exclusive");
        if (kind is not null)
        {
            if (!AgentKindCatalog.TryResolve(kind, out _))
                throw new ControlPlaneCliUsageException(AgentKindCatalog.UnsupportedKindMessage(kind));
            obj["kind"] = kind;
        }

        if (occupant is not null)
            obj["occupant"] = occupant;
        if (command is not null)
            obj["command"] = command;
        if (parsed.Flags.TryGetValue("--args-joined", out var joined))
            obj["args"] = ToJsonArray(joined.Split('\u001f'));
        return obj;
    }

    internal static JsonObject BuildAgentTargetParams(ParsedCli parsed, string[] args)
    {
        var obj = new JsonObject();
        var current = TryCurrentPaneId(parsed);
        var flaggedPane = Flag(parsed, "--pane-id");
        if (current is not null && !string.IsNullOrWhiteSpace(flaggedPane))
            throw new ControlPlaneCliUsageException("--current cannot be combined with a pane id");
        var pane = current ?? flaggedPane ?? (current is null ? TryPositional(args, 1) : null);

        var agent = Flag(parsed, "--agent-id");
        if (!string.IsNullOrWhiteSpace(pane))
            obj["pane_id"] = pane;
        if (!string.IsNullOrWhiteSpace(agent))
            obj["agent_id"] = agent;
        if (!obj.ContainsKey("pane_id") && !obj.ContainsKey("agent_id"))
            throw new ControlPlaneCliUsageException("pane_id is required");
        return obj;
    }

    internal static JsonObject BuildAgentRenameParams(ParsedCli parsed, string[] args)
    {
        var obj = BuildAgentTargetParams(parsed, args);
        if (Switch(parsed, "--clear"))
            return obj;
        var name = Flag(parsed, "--name");
        if (string.IsNullOrWhiteSpace(name))
        {
            var start = PaneSelectorSkipsPositional(parsed) ? 1 : 2;
            name = TryPositional(args, start);
        }

        if (string.IsNullOrWhiteSpace(name))
            throw new ControlPlaneCliUsageException("name is required (or pass --clear)");
        obj["name"] = name;
        return obj;
    }

    internal static JsonObject BuildAgentSendKeysParams(
        ParsedCli parsed, string[] args, string[] originalArgs)
    {
        var obj = BuildAgentTargetParams(parsed, args);
        var keys = RepeatableArg(originalArgs, "--keys");
        if (keys.Count == 0)
        {
            var start = PaneSelectorSkipsPositional(parsed) ? 1 : 2;
            keys = RequirePositionals(args, start);
        }

        obj["keys"] = ToJsonArray(keys);
        return obj;
    }

    internal static JsonObject BuildAgentViewSetParams(ParsedCli parsed)
    {
        var source = Flag(parsed, "--source")
            ?? throw new ControlPlaneCliUsageException("--source is required");
        var obj = new JsonObject { ["source"] = source };
        var label = Flag(parsed, "--label");
        if (label is not null)
            obj["label"] = label;
        var filter = Flag(parsed, "--filter");
        if (filter is not null)
        {
            try
            {
                obj["filter"] = JsonNode.Parse(filter)
                    ?? throw new ControlPlaneCliUsageException("--filter must be JSON");
            }
            catch (JsonException)
            {
                throw new ControlPlaneCliUsageException("--filter must be JSON");
            }
        }

        var sort = Flag(parsed, "--sort");
        if (sort is not null)
        {
            try
            {
                obj["sort"] = JsonNode.Parse(sort)
                    ?? throw new ControlPlaneCliUsageException("--sort must be JSON");
            }
            catch (JsonException)
            {
                throw new ControlPlaneCliUsageException("--sort must be JSON");
            }
        }

        return obj;
    }

    internal static JsonObject BuildAgentViewClearParams(ParsedCli parsed)
    {
        var obj = new JsonObject();
        var source = Flag(parsed, "--source");
        if (source is not null)
            obj["source"] = source;
        return obj;
    }

    internal static JsonObject BuildTerminalControlParams(
        string paneId, string leaseId, string subscriptionId) =>
        new()
        {
            ["pane_id"] = paneId,
            ["lease_id"] = leaseId,
            ["subscription_id"] = subscriptionId,
        };

    /// <summary>
    /// Methods the live attach session uses after CLI resolve.
    /// <c>run_terminal_attach</c>. Do not treat
    /// <c>terminal.control</c> JSON as the CLI process result.
    /// </summary>
    internal static IReadOnlyList<string> AgentAttachMethodSequence { get; } =
    [
        ProtocolMethods.AgentGet,
        ProtocolMethods.EventsSubscribe,
        ProtocolMethods.RuntimeLeaseClaim,
        ProtocolMethods.TerminalObserve,
        ProtocolMethods.TerminalControl,
    ];

    internal static string ReadAgentAttachPaneId(JsonElement info)
    {
        if (!info.TryGetProperty("pane_id", out var paneEl)
            || paneEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(paneEl.GetString()))
        {
            throw new ControlPlaneCliUsageException("agent attach failed: response did not include pane_id");
        }

        return paneEl.GetString()!;
    }

    internal static async Task<int> EnterLiveAgentAttachAsync(
        string paneId,
        ParsedCli parsed,
        ILiveAttachHost? liveAttach,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        if (liveAttach is null)
        {
            Console.Error.WriteLine("agent attach requires the live attach client");
            return 1;
        }

        return await liveAttach.AttachToPaneAsync(
                paneId,
                parsed.SessionOption,
                parsed.SocketOverride,
                sessionOptionWasSet: parsed.SessionOption is not null,
                ct)
            .ConfigureAwait(false);
    }

    private static async Task<string> ResolveAgentAttachPaneAsync(
        ControlPlaneClient client, ParsedCli parsed, string[] args)
    {
        var info = await client.CallAsync(
            ProtocolMethods.AgentGet, BuildAgentTargetParams(parsed, args)).ConfigureAwait(false);
        return ReadAgentAttachPaneId(info);
    }

    private static async Task<JsonElement> AgentViewAsync(
        ControlPlaneClient client, string[] args, ParsedCli parsed)
    {
        if (args.Length < 2)
            throw new ControlPlaneCliUsageException("agent view requires set or clear");
        return args[1] switch
        {
            "set" => await client.CallAsync(
                ProtocolMethods.AgentViewSet, BuildAgentViewSetParams(parsed)).ConfigureAwait(false),
            "clear" => await client.CallAsync(
                ProtocolMethods.AgentViewClear, BuildAgentViewClearParams(parsed)).ConfigureAwait(false),
            _ => throw new ControlPlaneCliUsageException($"Unknown agent view subcommand: {args[1]}"),
        };
    }

    private static string? TryPositional(string[] args, int index)
    {
        var list = RequirePositionals(args, index);
        return list.Count > 0 ? list[0] : null;
    }

    internal static ControlPlaneCliUsageException BuildLayoutApplyUsage() =>
        new("layout apply requires hypa rpc layout.apply '{...}' for the portable root tree");

    internal static JsonObject BuildPaneCreateParams(string[] args)
    {
        var parsed = Parse(PrependDummyVerb(args));
        var obj = new JsonObject
        {
            ["workspace_id"] = FlagOrArg(parsed, args, "--workspace"),
            ["command"] = FlagOrArg(parsed, args, "--command") ?? "",
            ["args"] = ToJsonArray(RepeatableArg(args, "--args")),
            ["label"] = FlagOrArg(parsed, args, "--label"),
            ["run_id"] = FlagOrArg(parsed, args, "--run"),
            ["step_id"] = FlagOrArg(parsed, args, "--step"),
            ["agent_session_id"] = FlagOrArg(parsed, args, "--agent-session"),
            ["placement"] = FlagOrArg(parsed, args, "--placement"),
        };
        ApplyPlacementCredentials(obj, parsed, copyParentPaneId: true);
        return obj;
    }

    internal static JsonObject BuildPaneShowParams(ParsedCli parsed, string[] args)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = ResolvePlacementPaneId(args, parsed),
            ["mode"] = Flag(parsed, "--mode") ?? "tiled",
            ["focus"] = !parsed.Switches.Contains("--no-focus"),
        };
        var tab = Flag(parsed, "--tab-id");
        if (tab is not null)
            obj["tab_id"] = tab;
        var target = Flag(parsed, "--target-pane-id") ?? Flag(parsed, "--target-pane");
        if (target is not null)
            obj["target_pane_id"] = target;
        var direction = Flag(parsed, "--direction");
        if (direction is not null)
            obj["direction"] = direction;
        if (double.TryParse(Flag(parsed, "--ratio"), out var ratio))
            obj["ratio"] = ratio;
        var attach = Flag(parsed, "--attach-client-id");
        if (attach is not null)
            obj["attach_client_id"] = attach;
        if (long.TryParse(Flag(parsed, "--seq"), out var seq))
            obj["seq"] = seq;
        ApplyPlacementCredentials(obj, parsed, copyParentPaneId: false);
        return obj;
    }

    internal static JsonObject BuildPaneHideParams(ParsedCli parsed, string[] args)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = ResolvePlacementPaneId(args, parsed),
        };
        var attach = Flag(parsed, "--attach-client-id");
        if (attach is not null)
            obj["attach_client_id"] = attach;
        if (long.TryParse(Flag(parsed, "--seq"), out var seq))
            obj["seq"] = seq;
        ApplyPlacementCredentials(obj, parsed, copyParentPaneId: false);
        return obj;
    }

    internal static void ApplyPlacementCredentials(
        JsonObject obj,
        ParsedCli parsed,
        bool copyParentPaneId)
    {
        var parentCap = Flag(parsed, "--parent-capability");
        var lease = Flag(parsed, "--lease-id");
        var suppressToken = !string.IsNullOrWhiteSpace(parentCap)
            || !string.IsNullOrWhiteSpace(lease);

        if (!string.IsNullOrWhiteSpace(parentCap) && !copyParentPaneId)
            obj["parent_capability"] = parentCap;
        if (!string.IsNullOrWhiteSpace(lease))
            obj["lease_id"] = lease;

        var occupant = Flag(parsed, "--occupant-token");
        if (string.IsNullOrWhiteSpace(occupant) && !suppressToken)
            occupant = Environment.GetEnvironmentVariable(PaneIdEnvironment.HypaPaneToken);
        if (!string.IsNullOrWhiteSpace(occupant))
            obj["occupant_token"] = occupant;

        if (!copyParentPaneId)
            return;

        var parent = Flag(parsed, "--parent-pane-id");
        if (string.IsNullOrWhiteSpace(parent)
            && !string.IsNullOrWhiteSpace(occupant)
            && !suppressToken)
        {
            parent = Environment.GetEnvironmentVariable(PaneIdEnvironment.HypaPaneId);
        }

        if (!string.IsNullOrWhiteSpace(parent))
            obj["parent_pane_id"] = parent;
    }

    internal static string ResolvePlacementPaneId(string[] args, ParsedCli parsed)
    {
        var current = TryCurrentPaneId(parsed);
        var flagged = parsed.Flags.TryGetValue("--pane-id", out var flag) && !string.IsNullOrWhiteSpace(flag)
            ? flag
            : null;
        if (current is not null && flagged is not null)
            throw new ControlPlaneCliUsageException("--current cannot be combined with a pane id");
        if (current is not null)
            return current;
        if (flagged is not null)
            return flagged;
        if (args.Length > 1 && !args[1].StartsWith('-'))
            return args[1];
        var env = Environment.GetEnvironmentVariable(PaneIdEnvironment.HypaPaneId);
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        throw new ControlPlaneCliUsageException("pane_id is required");
    }

    private static string[] PrependDummyVerb(string[] args)
    {
        var copy = new string[args.Length + 1];
        copy[0] = "noop";
        Array.Copy(args, 0, copy, 1, args.Length);
        return copy;
    }

    private static string? FlagOrArg(ParsedCli parsed, string[] args, string name) =>
        parsed.Flags.TryGetValue(name, out var v) ? v : Arg(args, name);

    internal static IReadOnlyList<string> RepeatableArg(string[] args, string name)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != name)
                continue;
            if (i + 1 >= args.Length)
                break;
            list.Add(args[++i]);
        }

        return list;
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> items)
    {
        // AOT-safe: JsonValue.Create + JsonArray(JsonNode[]) — avoid Add<T>.
        var nodes = new JsonNode[items.Count];
        for (var i = 0; i < items.Count; i++)
            nodes[i] = JsonValue.Create(items[i])!;
        return new JsonArray(nodes);
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name) => args.Contains(name);

    private static string RequirePositionalArg(string[] args, int index, string name)
    {
        var list = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith('-') && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                i++;
                continue;
            }

            if (args[i].StartsWith('-'))
                continue;
            list.Add(args[i]);
        }

        var pos = index - 1;
        if (pos < 0 || pos >= list.Count)
            throw new ControlPlaneCliUsageException($"{name} is required");
        return list[pos];
    }

    internal static JsonObject BuildWorkspaceMoveBlockParams(string[] originalArgs)
    {
        var ids = FlagValues(originalArgs, "--workspace-id");
        if (ids.Count == 0)
            throw new ControlPlaneCliUsageException("--workspace-id is required");
        var obj = new JsonObject { ["workspace_ids"] = new JsonArray(ids.Select(id => JsonValue.Create(id)).ToArray()) };
        var before = FlagValue(originalArgs, "--before");
        if (before is not null)
            obj["before_workspace_id"] = before;
        return obj;
    }

    internal static JsonObject BuildWorkspaceReportMetadataParams(
        string[] args, ParsedCli parsed, string[] originalArgs)
    {
        var obj = new JsonObject
        {
            ["workspace_id"] = RequirePositionalArg(args, 1, "workspace_id"),
            ["source"] = Flag(parsed, "--source")
                ?? throw new ControlPlaneCliUsageException("--source is required"),
        };
        var tokens = new JsonObject();
        foreach (var value in FlagValues(originalArgs, "--token"))
        {
            var separator = value.IndexOf('=');
            if (separator <= 0)
                throw new ControlPlaneCliUsageException("--token requires key=value");
            tokens[value[..separator]] = value[(separator + 1)..];
        }
        foreach (var key in FlagValues(originalArgs, "--clear-token"))
            tokens[key] = null;
        if (tokens.Count == 0)
            throw new ControlPlaneCliUsageException("--token or --clear-token is required");
        obj["tokens"] = tokens;
        if (int.TryParse(Flag(parsed, "--ttl-ms"), out var ttl))
            obj["ttl_ms"] = ttl;
        if (long.TryParse(Flag(parsed, "--seq"), out var seq))
            obj["seq"] = seq;
        return obj;
    }

    internal static JsonObject BuildPaneReportAgentParams(string[] args, ParsedCli parsed)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = RequirePaneId(args, parsed),
            ["source"] = Flag(parsed, "--source")
                ?? throw new ControlPlaneCliUsageException("--source is required"),
            ["agent"] = Flag(parsed, "--agent")
                ?? throw new ControlPlaneCliUsageException("--agent is required"),
            ["state"] = Flag(parsed, "--state")
                ?? throw new ControlPlaneCliUsageException("--state is required"),
        };
        var message = Flag(parsed, "--message");
        if (message is not null)
            obj["message"] = message;
        var sessionId = Flag(parsed, "--agent-session-id");
        var sessionPath = Flag(parsed, "--agent-session-path");
        if (sessionId is not null)
            obj["agent_session_id"] = sessionId;
        if (sessionPath is not null)
            obj["agent_session_path"] = sessionPath;
        var start = Flag(parsed, "--session-start-source");
        if (start is not null)
            obj["session_start_source"] = start;
        if (long.TryParse(Flag(parsed, "--seq"), out var seq))
            obj["seq"] = seq;
        return obj;
    }

    internal static JsonObject BuildPaneReportAgentSessionParams(string[] args, ParsedCli parsed)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = RequirePaneId(args, parsed),
            ["source"] = Flag(parsed, "--source")
                ?? throw new ControlPlaneCliUsageException("--source is required"),
            ["agent"] = Flag(parsed, "--agent")
                ?? throw new ControlPlaneCliUsageException("--agent is required"),
        };
        var sessionId = Flag(parsed, "--agent-session-id");
        var sessionPath = Flag(parsed, "--agent-session-path");
        if (sessionId is not null)
            obj["agent_session_id"] = sessionId;
        if (sessionPath is not null)
            obj["agent_session_path"] = sessionPath;
        if (sessionId is null && sessionPath is null)
            throw new ControlPlaneCliUsageException("--agent-session-id or --agent-session-path is required");
        var start = Flag(parsed, "--session-start-source");
        if (start is not null)
            obj["session_start_source"] = start;
        if (long.TryParse(Flag(parsed, "--seq"), out var seq))
            obj["seq"] = seq;
        return obj;
    }

    internal static JsonObject BuildPaneReportMetadataParams(
        string[] args, ParsedCli parsed, string[] originalArgs)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = RequirePaneId(args, parsed),
            ["source"] = Flag(parsed, "--source")
                ?? throw new ControlPlaneCliUsageException("--source is required"),
        };
        var tokens = new JsonObject();
        foreach (var value in FlagValues(originalArgs, "--token"))
        {
            var separator = value.IndexOf('=');
            if (separator <= 0)
                throw new ControlPlaneCliUsageException("--token requires key=value");
            tokens[value[..separator]] = value[(separator + 1)..];
        }
        foreach (var key in FlagValues(originalArgs, "--clear-token"))
            tokens[key] = null;
        if (tokens.Count == 0)
            throw new ControlPlaneCliUsageException("--token or --clear-token is required");
        obj["tokens"] = tokens;
        if (int.TryParse(Flag(parsed, "--ttl-ms"), out var ttl))
            obj["ttl_ms"] = ttl;
        if (long.TryParse(Flag(parsed, "--seq"), out var seq))
            obj["seq"] = seq;
        return obj;
    }

    internal static JsonObject BuildPaneReleaseAgentParams(string[] args, ParsedCli parsed)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = RequirePaneId(args, parsed),
            ["source"] = Flag(parsed, "--source")
                ?? throw new ControlPlaneCliUsageException("--source is required"),
            ["agent"] = Flag(parsed, "--agent")
                ?? throw new ControlPlaneCliUsageException("--agent is required"),
        };
        if (long.TryParse(Flag(parsed, "--seq"), out var seq))
            obj["seq"] = seq;
        return obj;
    }

    internal static JsonObject BuildPaneClearAgentAuthorityParams(string[] args, ParsedCli parsed)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = RequirePaneId(args, parsed),
        };
        var source = Flag(parsed, "--source");
        if (source is not null)
            obj["source"] = source;
        if (long.TryParse(Flag(parsed, "--seq"), out var seq))
            obj["seq"] = seq;
        return obj;
    }

    private static string? FlagValue(string[] args, string name) => FlagValues(args, name).LastOrDefault();

    private static IReadOnlyList<string> FlagValues(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                values.Add(args[i + 1]);
        return values;
    }

    private static async Task<string> ClaimInputLeaseAsync(ControlPlaneClient client, string paneId)
    {
        var result = await client.CallAsync("runtime.lease.claim", new JsonObject
        {
            ["pane_id"] = paneId,
            ["scope"] = "input",
            ["ttl_ms"] = 30_000,
        }).ConfigureAwait(false);

        var outcome = result.GetProperty("outcome").GetString();
        if (outcome is "granted" or "already_held")
        {
            var leaseId = result.GetProperty("lease_id").GetString();
            if (!string.IsNullOrEmpty(leaseId))
                return leaseId;
        }

        throw new InvalidOperationException(
            $"Could not claim input lease for pane {paneId}: outcome={outcome}");
    }

    private static async Task<JsonElement> SendTextWithLeaseAsync(
        ControlPlaneClient client,
        string paneId,
        string text)
    {
        var leaseId = await ClaimInputLeaseAsync(client, paneId).ConfigureAwait(false);
        return await client.CallAsync("pane.send_text", new JsonObject
        {
            ["pane_id"] = paneId,
            ["text"] = text,
            ["lease_id"] = leaseId,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// One <c>pane.send_input</c> after the input lease.
    /// The server writes the command text, then the enter key.
    /// </summary>
    private static async Task<JsonElement> RunWithLeaseAsync(
        ControlPlaneClient client,
        string paneId,
        string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ControlPlaneCliUsageException("usage: hypa pane run <pane_id> <command>");
        var leaseId = await ClaimInputLeaseAsync(client, paneId).ConfigureAwait(false);
        return await client.CallAsync(ProtocolMethods.PaneSendInput, new JsonObject
        {
            ["pane_id"] = paneId,
            ["text"] = command,
            ["keys"] = new JsonArray("enter"),
            ["lease_id"] = leaseId,
        }).ConfigureAwait(false);
    }

    private static async Task<JsonElement> WaitForOutputAsync(
        ControlPlaneClient client,
        ParsedCli parsed,
        string[] args)
    {
        var body = BuildPaneWaitOutputParams(parsed, args);
        var callCap = PaneWaitOutputClientDeadline(parsed, body);
        return await client.CallAsync(ProtocolMethods.PaneWaitForOutput, body, timeout: callCap)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// No client deadline when the server <c>timeout_ms</c> is omitted.
    /// An explicit server budget uses a longer client deadline.
    /// </summary>
    internal static TimeSpan PaneWaitOutputClientDeadline(ParsedCli parsed, JsonObject body)
    {
        if (body["timeout_ms"] is JsonValue timeoutNode
            && timeoutNode.TryGetValue<int>(out var serverTimeoutMs))
        {
            var localMs = Math.Max((long)parsed.TimeoutMs, (long)serverTimeoutMs + 1_000);
            var maxMs = (long)TimeSpan.MaxValue.TotalMilliseconds;
            if (localMs > maxMs)
                localMs = maxMs;
            return TimeSpan.FromMilliseconds(localMs);
        }

        return Timeout.InfiniteTimeSpan;
    }

    private static string FormatRpcError(ControlPlaneException cpe) =>
        string.IsNullOrEmpty(cpe.ErrorCode)
            ? $"error {cpe.Code}: {cpe.Message}"
            : $"error {cpe.Code}: {cpe.Message} ({cpe.ErrorCode})";

    private static async Task<JsonElement> PromptWithLeaseAsync(
        ControlPlaneClient client,
        string paneId,
        string message,
        bool wait,
        string until,
        int timeoutMs,
        TimeSpan? callTimeout = null)
    {
        var leaseId = await ClaimInputLeaseAsync(client, paneId).ConfigureAwait(false);
        var parameters = new JsonObject
        {
            ["pane_id"] = paneId,
            ["message"] = message,
            ["lease_id"] = leaseId,
        };
        if (wait)
        {
            parameters["wait"] = new JsonObject
            {
                ["until"] = until,
                ["timeout_ms"] = timeoutMs,
            };
        }

        return await client.CallAsync("agent.prompt", parameters, timeout: callTimeout).ConfigureAwait(false);
    }

    private static readonly string[][] MuxGroupPaths =
    [
        ["plugin", "action"],
        ["plugin", "bundled"],
        ["plugin", "pane"],
        ["plugin", "resource"],
        ["terminal", "title"],
        ["agent", "view"],
        ["workspace"],
        ["tab"],
        ["layout"],
        ["pane"],
        ["agent"],
        ["integration"],
        ["plugin"],
        ["notification"],
        ["terminal"],
        ["events"],
    ];

    private static bool TryWriteMuxGroupHelp(ParsedCli parsed)
    {
        var help = parsed.Switches.Contains("--help") || parsed.Switches.Contains("-h");
        var group = MatchMuxGroup(parsed.Tokens, exact: !help);
        if (group is null)
            return false;

        Console.Write(FormatMuxGroupHelp(group));
        return true;
    }

    private static string[]? MatchMuxGroup(IReadOnlyList<string> tokens, bool exact)
    {
        string[]? best = null;
        foreach (var group in MuxGroupPaths)
        {
            if (group.Length > tokens.Count)
                continue;

            var prefix = true;
            for (var i = 0; i < group.Length; i++)
            {
                if (!string.Equals(tokens[i], group[i], StringComparison.Ordinal))
                {
                    prefix = false;
                    break;
                }
            }

            if (!prefix)
                continue;
            if (exact && tokens.Count != group.Length)
                continue;
            if (best is null || group.Length > best.Length)
                best = group;
        }

        return best;
    }

    internal static string FormatMuxGroupHelp(IReadOnlyList<string> group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var name = string.Join(' ', group);
        var prefix = name + " ";
        var body = new StringBuilder();
        body.Append("hypa ").Append(name).AppendLine();
        body.AppendLine();
        body.Append("Usage: hypa [--session NAME] [--socket PATH] [--timeout-ms MS] ");
        body.Append(name).AppendLine(" <subcommand>");
        body.AppendLine();
        body.AppendLine("Commands:");
        foreach (var raw in GetHelpText().Split('\n'))
        {
            var trimmed = raw.Trim().TrimEnd('\r');
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            body.Append("  ").Append(trimmed).AppendLine();
        }

        body.AppendLine();
        body.AppendLine("Flags:");
        body.AppendLine("  --session NAME");
        body.AppendLine("  --socket PATH");
        body.AppendLine("  --timeout-ms MS");
        body.AppendLine("  -h, --help");
        return body.ToString();
    }

    internal static string GetHelpText() => """
            hypa — thin client for the Hypa workspace mux control plane

            Usage:
              hypa [--session NAME] [--socket PATH] [--timeout-ms MS] <command>

            Commands:
              ping
              snapshot
              workspace create [--cwd PATH] [--label L] [--command CMD] [--args ARG]... [--no-pane]
              workspace list
              workspace get <workspace_id>
              workspace focus <workspace_id>
              workspace rename <workspace_id> --label L
              workspace close <workspace_id>
              workspace move <workspace_id> --index N
              workspace move-block --workspace-id ID [--workspace-id ID ...] [--before ID]
              workspace report-metadata <workspace_id> --source S --token k=v [--token k=v] [--clear-token k] [--ttl-ms N] [--seq N]
              tab create --workspace ID [--label L] [--command CMD] [--args ARG]... [--no-pane] [--no-focus]
              tab list [--workspace ID]
              tab get|focus|close <tab_id>
              tab rename <tab_id> --label L
              tab move <tab_id> --index N
              layout export [--tab-id ID] [--pane-id ID]
              rpc layout.apply '{...}'   (portable root; the only apply path)
              pane create [--workspace ID] [--command CMD] [--args ARG]... [--label L] [--placement hidden|tiled] [--run ID] [--step ID] [--parent-pane-id ID] [--occupant-token TOKEN] [--lease-id ID]
              pane list
              pane get <pane_id>
              pane current [--current|--pane-id ID]
              pane send <pane_id> <text...>   (claims input lease first)
              pane send --pane-id ID <text...>
              pane run <pane_id> [--] <command...>   (input lease, then text and Enter)
              pane run --current [--] <command...>
              pane read <pane_id> [--source visible|recent|recent-unwrapped|detection] [--lines N]
              pane wait-output <pane_id> (--match TEXT | --regex PATTERN) [--source S] [--timeout MS]
              pane close <pane_id>
              pane split <pane_id> --direction right|down [--ratio N] [--command CMD] [--args ARG]... [--label L] [--focus|--no-focus]
              pane move <pane_id> --tab-id ID [--direction right|down]
              pane zoom [--pane-id ID] [--mode toggle|on|off]
              pane layout [--tab-id ID]
              pane swap [--pane-id ID] [--direction left|right|up|down | --target ID]
              pane show [--pane-id ID] --mode tiled|overlay --attach-client-id ID [--no-focus] [--seq N] [--tab-id ID] [--target-pane-id ID] [--direction right|down] [--ratio N] [--occupant-token TOKEN | --parent-capability CAP | --lease-id ID]
              pane hide [--pane-id ID] [--seq N] [--occupant-token TOKEN | --parent-capability CAP | --lease-id ID]
              pane report-agent <pane_id> --source S --agent A --state working|blocked|idle|unknown|done [--message M] [--agent-session-id ID|--agent-session-path PATH] [--session-start-source startup|resume|clear|compact|branch|new|fork|select] [--seq N]
              pane report-agent-session <pane_id> --source S --agent A --agent-session-id ID|--agent-session-path PATH [--session-start-source startup|resume|clear|compact|branch|new|fork|select] [--seq N]
              pane report-metadata <pane_id> --source S --token k=v [--token k=v] [--clear-token k] [--ttl-ms N] [--seq N]
              pane release-agent <pane_id> --source S --agent A [--seq N]
              pane clear-agent-authority <pane_id> [--source S] [--seq N]
              agent list
              agent status <pane_id>
              agent start <pane_id> [--kind KIND | --occupant ID | --command CMD] [--args ARG]...
              agent wait <pane_id> [--until blocked|done|idle|working] [--timeout MS]
              agent prompt <pane_id> <message...> [--wait] [--until S] [--timeout MS]
              agent read <pane_id>
              agent explain <target>
              agent rename <target> <name> | agent rename <target> --clear
              agent focus <target>
              agent send-keys <target> KEY... | --keys KEY
              agent attach <target>
              agent view set --source S [--label L] [--filter JSON] [--sort JSON]
              agent view clear [--source S]
              events wait <pane_id> --agent-status blocked|done|idle|working|unknown [--timeout MS]
              integration install [--dry-run]
              integration install <pi|omp|claude|codex|copilot|devin|droid|kimi|opencode|kilo|hermes|qodercli|qwen|cursor|mastracode|antigravity-cli|grok> [--dry-run]
              integration uninstall <pi|omp|claude|codex|copilot|devin|droid|kimi|opencode|kilo|hermes|qodercli|qwen|cursor|mastracode|antigravity-cli|grok>
              integration uninstall --all
              integration status
              plugin link <path> [--disabled]
              plugin list [--plugin ID]
              plugin unlink <plugin_id>
              plugin enable <plugin_id>
              plugin disable <plugin_id>
              plugin action list [--plugin ID]
              plugin action invoke <action_id> [--plugin ID] [--resource ID] [--revision N]
              plugin log list [--plugin ID]
              plugin pane open <plugin_id> <entrypoint> [--placement overlay|popup|split|tab|zoomed] [--width SIZE] [--height SIZE] [--cwd PATH] [--env KEY=VALUE] [--target-pane PANE] [--focus|--no-focus]
              plugin pane focus <pane_id>
              plugin pane close <pane_id>
              plugin pane send-text <pane_id> --text TEXT
              plugin bundled install <plugin-id>
              plugin bundled uninstall <plugin-id>
              plugin bundled status <plugin-id>
              plugin resource list [--plugin ID]
              plugin resource get <resource_id>
              plugin resource publish <envelope-json>
              plugin resource remove <resource_id>
              notification show --title TEXT [--body TEXT] [--source core] [--sound none|done|request] [--pane-id ID]
              terminal title set --title TEXT
              terminal title clear
              rpc <method> [json-params]

            Status bar chips: session, workspace, pane, agent.
            Window title tokens: {hostname} {workspace} {tab} {pane} {terminal_title}.
            hypa --skill prints the agent skill.

            --command is argv0. A space in CMD does not wrap a shell.
            Empty --command starts terminal.default_shell or $SHELL (login on macOS).
            Explicit wrap: --command /bin/bash --args -lc --args "…"
            --timeout-ms is the client call budget (default 30000).
            --timeout on wait, prompt, and events.wait is the RPC wait budget (default 60000).
            When events.wait exceeds that budget, the server returns error_code timeout.
            The message is timed out waiting for event match.
            Words after the pane run target are the command.
            A word that starts with - stays in that command.
            A -- after the pane target ends Hypa option parsing.
            pane wait-output --timeout is the server match budget.
            When you omit --timeout, the client waits until the server replies.
            An explicit --timeout uses a client deadline longer than that server budget.
            When that budget ends with no match, the server returns error_code timeout.
            The message is timed out waiting for output match.
            The regex is checked on each output line.
            --current selects the pane in HYPA_PANE_ID.
            A missing HYPA_PANE_ID is a usage error and does not open the mux.

            Exit codes:
              0  RPC success
              1  Protocol / RPC error (stderr: error <code>: <catalog message>)
              2  Socket path / connect failure
              3  Client timeout
              4  Parse / usage

            These commands do not start the mux server. Use `hypa` or `hypa mux serve`.
            """;

    private static void PrintHelp() => Console.WriteLine(GetHelpText());
}
