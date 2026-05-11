using System.CommandLine;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Rewrite;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Cli.Commands;

public sealed class RunCommand(CommandRunnerService runnerService, IShellLexer shellLexer)
{
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

        root.AddOption(cOpt);
        root.AddOption(tOpt);
        root.AddCommand(BuildRawSubcommand());

        root.SetHandler(async context =>
        {
            var cVal = context.ParseResult.GetValueForOption(cOpt);
            var tVals = context.ParseResult.GetValueForOption(tOpt);
            var ct = context.GetCancellationToken();

            if (cVal is not null)
            {
                context.ExitCode = await HandleBufferedAsync(cVal, ct);
            }
            else if (tVals is { Length: > 0 })
            {
                context.ExitCode = await HandlePassthroughAsync(tVals, ct);
            }
            // else: no option and no subcommand matched — System.CommandLine prints help.
        });
    }

    private Command BuildRawSubcommand()
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
            context.ExitCode = await HandlePassthroughAsync(args, context.GetCancellationToken());
        });
        return cmd;
    }

    private async Task<int> HandleBufferedAsync(string command, CancellationToken ct)
    {
        var tokens = shellLexer.Lex(command)
            .Where(t => t.Kind is TokenKind.Arg or TokenKind.QuotedArg)
            .Select(t => t.Kind == TokenKind.QuotedArg ? StripQuotes(t.Value) : t.Value)
            .ToArray();

        if (tokens.Length == 0)
        {
            await Console.Error.WriteLineAsync("hypa -c: empty command.");
            return 1;
        }

        var invocation = CommandInvocation.Buffered(tokens[0], tokens[1..], command);
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

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') ||
                                   (value[0] == '"' && value[^1] == '"')))
            return value[1..^1];
        return value;
    }

    private async Task<int> HandlePassthroughAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("hypa -t: no command specified.");
            return 1;
        }

        var invocation = CommandInvocation.Passthrough(args[0], args[1..], string.Join(' ', args));
        var result = await runnerService.RunPassthroughAsync(invocation, ct);

        if (!result.IsOk)
        {
            await Console.Error.WriteLineAsync($"hypa: {result.Error.Message}");
            return 1;
        }

        return result.Value;
    }
}
