using System.CommandLine;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class ReadCommand(FileReadService fileReadService)
{
    public Command Build()
    {
        var cmd = new Command("read", "Read a file in a context-aware mode.");
        var pathArg = new Argument<string>("path", "File path to read.");
        var modeOpt = new Option<string?>("--mode", "Read mode: smart, full, outline, signatures, pruned.");
        var maxTokensOpt = new Option<int?>("--max-tokens", "Maximum tokens to return.");
        cmd.AddArgument(pathArg);
        cmd.AddOption(modeOpt);
        cmd.AddOption(maxTokensOpt);
        cmd.SetHandler(async context =>
        {
            var path = context.ParseResult.GetValueForArgument(pathArg);
            var mode = context.ParseResult.GetValueForOption(modeOpt);
            var maxTokens = context.ParseResult.GetValueForOption(maxTokensOpt);
            var result = await fileReadService.ReadAsync(path, mode, maxTokens, context.GetCancellationToken());

            if (!result.IsOk)
            {
                Console.Error.WriteLine($"SUMMARY\nError: {result.Error.Message}");
                context.ExitCode = 1;
                return;
            }

            Console.Out.WriteLine(result.Value.Text);
            context.ExitCode = 0;
        });
        return cmd;
    }
}
