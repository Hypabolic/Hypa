using System.CommandLine;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Cli.Commands;

public sealed class DockerCommand(CommandRunnerService runnerService)
{
    public Command Build()
    {
        var argsArg = new Argument<string[]>("args", "docker subcommand and arguments.")
        {
            Arity = ArgumentArity.ZeroOrMore,
        };
        var cmd = new Command("docker", "Run docker with output reduction.");
        cmd.AddArgument(argsArg);
        cmd.SetHandler(async context =>
        {
            var args = context.ParseResult.GetValueForArgument(argsArg);
            if (args.Length == 0)
            {
                await Console.Error.WriteLineAsync("hypa docker: no arguments provided.");
                context.ExitCode = 1;
                return;
            }

            var invocation = CommandInvocation.Buffered("docker", args, $"docker {string.Join(' ', args)}");
            var result = await runnerService.RunBufferedAsync(invocation, CompressionOptions.Default, context.GetCancellationToken());

            if (!result.IsOk)
            {
                await Console.Error.WriteLineAsync($"hypa: {result.Error.Message}");
                context.ExitCode = 1;
                return;
            }

            Console.Write(result.Value.Text);
            if (!result.Value.Text.EndsWith('\n'))
                Console.WriteLine();

            context.ExitCode = result.Value.ExitCode;
        });
        return cmd;
    }
}
