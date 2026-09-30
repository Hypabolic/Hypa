using System.CommandLine;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class ReadCommand(FileReadService fileReadService)
{
    public Command Build()
    {
        var cmd = new Command("read", "Read a file in a context-aware mode.");
        var pathArg = new Argument<string>("path") { Description = "File path to read." };
        var modeOpt = new Option<string?>("--mode") { Description = "Read mode: smart, full, outline, signatures, pruned." };
        var maxTokensOpt = new Option<int?>("--max-tokens") { Description = "Maximum tokens to return." };
        cmd.Add(pathArg);
        cmd.Add(modeOpt);
        cmd.Add(maxTokensOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var path = parseResult.GetValue(pathArg)!;
            var mode = parseResult.GetValue(modeOpt);
            var maxTokens = parseResult.GetValue(maxTokensOpt);
            var result = await fileReadService.ReadAsync(path, mode, maxTokens, ct);

            if (!result.IsOk)
            {
                Console.Error.WriteLine($"SUMMARY\nError: {result.Error.Message}");
                return 1;
            }

            Console.Out.WriteLine(result.Value.Text);
            return 0;
        });
        return cmd;
    }
}
