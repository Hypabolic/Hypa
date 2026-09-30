using System.CommandLine;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class CompressCommand(CompressService compressService, IFileSystem fileSystem)
{
    public Command Build()
    {
        var cmd = new Command("compress", "Compress explicit text from stdin or a file.");
        var kindOpt = new Option<string?>("--kind") { Description = "Output kind: shell-output, log, code, generic." };
        var fileOpt = new Option<string?>("--file") { Description = "Read input from a file instead of stdin." };
        var maxTokensOpt = new Option<int?>("--max-tokens") { Description = "Maximum output tokens." };
        cmd.Add(kindOpt);
        cmd.Add(fileOpt);
        cmd.Add(maxTokensOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var kind = parseResult.GetValue(kindOpt);
            var file = parseResult.GetValue(fileOpt);
            var maxTokens = parseResult.GetValue(maxTokensOpt);
            string input;

            try
            {
                input = string.IsNullOrWhiteSpace(file)
                    ? await Console.In.ReadToEndAsync(ct)
                    : fileSystem.ReadAllText(file);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"SUMMARY\nError: {ex.Message}");
                return 1;
            }

            var result = await compressService.CompressAsync(input, kind, command: null, maxTokens, ct);
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
