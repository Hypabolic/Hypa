using System.CommandLine;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Cli.Commands;

public sealed class DotnetCommand(CommandRunnerService runnerService)
{
    public Command Build()
    {
        var argsArg = new Argument<string[]>("args")
        {
            Description = "dotnet subcommand and arguments.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var cmd = new Command("dotnet", "Run dotnet with output reduction.");
        cmd.Add(argsArg);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var args = parseResult.GetValue(argsArg) ?? [];
            if (args.Length == 0)
            {
                await Console.Error.WriteLineAsync("hypa dotnet: no arguments provided.");
                return 1;
            }

            var invocation = CommandInvocation.Buffered("dotnet", args, $"dotnet {string.Join(' ', args)}");
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
        });
        return cmd;
    }
}
