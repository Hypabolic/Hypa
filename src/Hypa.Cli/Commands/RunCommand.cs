using System.CommandLine;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Rewrite;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Cli.Commands;

public sealed class RunCommand(CommandRunnerService runnerService, IShellLexer shellLexer)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PackageManagerTimeout = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> PackageManagers = ["npm", "pnpm", "yarn", "bun", "npx", "corepack"];

    public void AttachTo(RootCommand root)
    {
        var cOpt = new Option<string?>(["-c"], "Run command through hypa: buffer output, compress, and return.")
        {
            ArgumentHelpName = "command",
        };

        var tOpt = new Option<string[]?>(["-t"], "Run command unmodified; stream directly to terminal.")
        {
            ArgumentHelpName = "args",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
        };
        var timeoutOpt = new Option<int?>(
            ["--timeout-ms"],
            "Override command timeout in milliseconds. Package-manager commands default to 10 minutes; other commands default to 30 seconds.")
        {
            ArgumentHelpName = "milliseconds",
        };

        root.AddOption(cOpt);
        root.AddOption(tOpt);
        root.AddGlobalOption(timeoutOpt);
        root.AddCommand(BuildRawSubcommand(timeoutOpt));

        root.SetHandler(async context =>
        {
            var cVal = context.ParseResult.GetValueForOption(cOpt);
            var tVals = context.ParseResult.GetValueForOption(tOpt);
            var timeoutMs = context.ParseResult.GetValueForOption(timeoutOpt);
            var ct = context.GetCancellationToken();

            if (cVal is not null)
            {
                context.ExitCode = await HandleBufferedAsync(cVal, timeoutMs, ct);
            }
            else if (tVals is { Length: > 0 })
            {
                context.ExitCode = await HandlePassthroughAsync(tVals, timeoutMs, ct);
            }
            // else: no option and no subcommand matched — System.CommandLine prints help.
        });
    }

    private Command BuildRawSubcommand(Option<int?> timeoutOpt)
    {
        var argsArg = new Argument<string[]>("args", "Command and arguments to run unmodified.")
        {
            Arity = ArgumentArity.ZeroOrMore,
        };
        var cmd = new Command("raw", "Run a command unmodified with no compression (alias for -t).");
        cmd.AddArgument(argsArg);
        cmd.SetHandler(async context =>
        {
            var args = context.ParseResult.GetValueForArgument(argsArg);
            var timeoutMs = context.ParseResult.GetValueForOption(timeoutOpt);
            context.ExitCode = await HandlePassthroughAsync(args, timeoutMs, context.GetCancellationToken());
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
