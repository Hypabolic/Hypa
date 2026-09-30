using System.CommandLine;
using Hypa.Cli.Mux;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Rewrite;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Cli.Commands;

public sealed class RunCommand(
    CommandRunnerService runnerService,
    IShellLexer shellLexer,
    MuxAttachService attachService,
    Hypa.AgentRuntime.Application.IAttachConfigLoader? attachConfig = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PackageManagerTimeout = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> PackageManagers = ["npm", "pnpm", "yarn", "bun", "npx", "corepack"];

    public void AttachTo(RootCommand root)
    {
        var cOpt = new Option<string?>("-c")
        {
            Description = "Run command through hypa: buffer output, compress, and return.",
            HelpName = "command",
        };

        var tOpt = new Option<string[]?>("-t")
        {
            Description = "Run command unmodified; stream directly to terminal.",
            HelpName = "args",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
        };
        var timeoutOpt = new Option<int?>("--timeout-ms")
        {
            Description =
                "Override command timeout in milliseconds. Package-manager commands default to 10 minutes; other commands default to 30 seconds.",
            HelpName = "milliseconds",
        };
        var defaultConfigOpt = new Option<bool>("--default-config")
        {
            Description = "Print the default attach config.toml and exit.",
        };
        var sessionOpt = new Option<string?>("--session")
        {
            Description = "Mux session name for bare attach.",
        };
        var remoteDestinationOpt = new Option<bool>("--remote-destination")
        {
            Description = AttachCommand.RemoteDestinationOptionDescription,
        };
        var remoteOpt = new Option<string?>("--remote")
        {
            Description = AttachCommand.RemoteOptionDescription,
        };
        var remoteKeybindingsOpt = new Option<string?>("--remote-keybindings")
        {
            Description = AttachCommand.RemoteKeybindingsOptionDescription,
        };
        var handoffOpt = new Option<bool>("--handoff")
        {
            Description = AttachCommand.HandoffOptionDescription,
        };

        root.Add(defaultConfigOpt);
        root.Add(cOpt);
        root.Add(tOpt);
        root.Add(sessionOpt);
        root.Add(remoteDestinationOpt);
        root.Add(remoteOpt);
        root.Add(remoteKeybindingsOpt);
        root.Add(handoffOpt);
        timeoutOpt.Recursive = true;

        root.Add(timeoutOpt);
        root.Add(BuildRawSubcommand(timeoutOpt));

        root.SetAction(async (parseResult, ct) =>
        {
            var cVal = parseResult.GetValue(cOpt);
            var tVals = parseResult.GetValue(tOpt);
            var timeoutMs = parseResult.GetValue(timeoutOpt);
            if (parseResult.GetValue(defaultConfigOpt))
            {
                var loader = attachConfig ?? new Hypa.AgentRuntime.Infrastructure.Config.FileAttachConfigLoader();
                Console.Write(loader.DefaultToml());
                return 0;
            }

            if (cVal is not null)
            {
                return await HandleBufferedAsync(cVal, timeoutMs, ct);
            }
            else if (tVals is { Length: > 0 })
            {
                return await HandlePassthroughAsync(tVals, timeoutMs, ct);
            }
            else
            {
                var sessionResult = parseResult.GetResult(sessionOpt);
                var sessionExplicit = sessionResult is { Tokens.Count: > 0 };
                var remoteDestination = parseResult.GetValue(remoteDestinationOpt);
                if (!AttachCommand.TryReadRemote(
                        parseResult.GetValue(remoteOpt),
                        parseResult.GetValue(remoteKeybindingsOpt),
                        parseResult.GetValue(handoffOpt),
                        out var remote,
                        out var remoteError))
                {
                    await Console.Error.WriteLineAsync("hypa: " + remoteError).ConfigureAwait(false);
                    return 2;
                }

                return await attachService
                    .AttachAsync(
                        parseResult.GetValue(sessionOpt),
                        cwd: null,
                        once: false,
                        socketOverride: null,
                        sessionExplicit,
                        ct,
                        remoteDestination,
                        remote: remote)
                    .ConfigureAwait(false);
            }
        });
    }

    private Command BuildRawSubcommand(Option<int?> timeoutOpt)
    {
        var argsArg = new Argument<string[]>("args")
        {
            Description = "Command and arguments to run unmodified.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var cmd = new Command("raw", "Run a command unmodified with no compression (alias for -t).");
        cmd.Add(argsArg);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var args = parseResult.GetValue(argsArg) ?? [];
            var timeoutMs = parseResult.GetValue(timeoutOpt);
            return await HandlePassthroughAsync(args, timeoutMs, ct);
        });
        return cmd;
    }

    private async Task<int> HandleBufferedAsync(string command, int? timeoutMs, CancellationToken ct)
    {
        if (!TryResolveTimeoutOverride(timeoutMs, out var timeout, out var error))
        {
            await Console.Error.WriteLineAsync(error);
            return 1;
        }

        var lexed = shellLexer.Lex(command);
        var verb = ShellVerb.Extract(lexed);
        var usesShellSyntax =
            lexed.Any(t => t.Kind is TokenKind.Operator or TokenKind.Pipe or TokenKind.Redirect or TokenKind.Shellism)
            || ShellExpansion.ContainsExpansion(lexed)
            || ShellExpansion.ContainsTildeExpansion(lexed)
            || ShellExpansion.ContainsGlobOrBraceExpansion(lexed)
            || ShellVerb.HasAssignmentPrefix(lexed)
            || (verb is not null && ShellBuiltins.IsStateful(verb));

        var invocation = usesShellSyntax
            ? CreateBufferedShellInvocation(command)
            : CreateBufferedProcessInvocation(command, lexed);

        if (invocation is null)
        {
            await Console.Error.WriteLineAsync("hypa -c: empty command.");
            return 1;
        }

        invocation = invocation with { Timeout = timeout ?? ResolveDefaultTimeout(invocation, lexed) };
        var result = await runnerService.RunBufferedAsync(invocation, CompressionOptions.Default, ct);

        if (!result.IsOk)
        {
            await Console.Error.WriteLineAsync($"hypa: {result.Error.Message}");
            return 1;
        }

        Console.Write(result.Value.Text);
        if (!result.Value.Text.EndsWith('\n'))
            Console.WriteLine();

        return result.Value.ExitCode;
    }

    private static CommandInvocation? CreateBufferedProcessInvocation(
        string command,
        IReadOnlyList<ShellToken> lexed)
    {
        var tokens = lexed
        .Where(t => t.Kind is TokenKind.Arg or TokenKind.QuotedArg)
        .Select(t => t.Kind == TokenKind.QuotedArg ? StripQuotes(t.Value) : t.Value)
        .ToArray();

        if (tokens.Length == 0)
            return null;

        return CommandInvocation.Buffered(tokens[0], tokens[1..], command);
    }

    private static CommandInvocation CreateBufferedShellInvocation(string command) =>
        OperatingSystem.IsWindows()
            ? CommandInvocation.Buffered("cmd.exe", ["/d", "/s", "/c", command], command)
            : CommandInvocation.Buffered("sh", ["-c", command], command);

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') ||
                                   (value[0] == '"' && value[^1] == '"')))
            return value[1..^1];
        return value;
    }

    private async Task<int> HandlePassthroughAsync(string[] args, int? timeoutMs, CancellationToken ct)
    {
        if (!TryResolveTimeoutOverride(timeoutMs, out var timeout, out var error))
        {
            await Console.Error.WriteLineAsync(error);
            return 1;
        }

        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("hypa -t: no command specified.");
            return 1;
        }

        var invocation = CommandInvocation.Passthrough(args[0], args[1..], string.Join(' ', args));
        invocation = invocation with { Timeout = timeout ?? ResolveDefaultTimeout(invocation, args) };
        var result = await runnerService.RunPassthroughAsync(invocation, ct);

        if (!result.IsOk)
        {
            await Console.Error.WriteLineAsync($"hypa: {result.Error.Message}");
            return 1;
        }

        return result.Value;
    }

    private static bool TryResolveTimeoutOverride(int? timeoutMs, out TimeSpan? timeout, out string error)
    {
        timeout = null;
        error = string.Empty;

        if (timeoutMs is null)
            return true;

        if (timeoutMs <= 0)
        {
            error = "hypa: --timeout-ms must be greater than 0.";
            return false;
        }

        timeout = TimeSpan.FromMilliseconds(timeoutMs.Value);
        return true;
    }

    private static TimeSpan ResolveDefaultTimeout(CommandInvocation invocation, IReadOnlyList<ShellToken> lexed) =>
        IsPackageManagerInvocation(invocation.Executable) || IsPackageManagerLexedCommand(lexed)
            ? PackageManagerTimeout
            : DefaultTimeout;

    private static TimeSpan ResolveDefaultTimeout(CommandInvocation invocation, IReadOnlyList<string> args) =>
        IsPackageManagerInvocation(invocation.Executable) || (args.Count > 0 && IsPackageManagerInvocation(args[0]))
            ? PackageManagerTimeout
            : DefaultTimeout;

    private static bool IsPackageManagerLexedCommand(IReadOnlyList<ShellToken> lexed)
    {
        var firstArg = lexed.FirstOrDefault(t => t.Kind is TokenKind.Arg or TokenKind.QuotedArg);
        if (firstArg is null)
            return false;

        var value = firstArg.Kind == TokenKind.QuotedArg ? StripQuotes(firstArg.Value) : firstArg.Value;
        return IsPackageManagerInvocation(value);
    }

    private static bool IsPackageManagerInvocation(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        return PackageManagers.Contains(name);
    }
}
