using System.CommandLine;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class CompressCommand(CompressService compressService, IFileSystem fileSystem)
{
    public Command Build()
    {
        var cmd = new Command("compress", "Compress explicit text from stdin or a file.");
        var kindOpt = new Option<string?>("--kind", "Output kind: shell-output, log, code, generic.");
        var fileOpt = new Option<string?>("--file", "Read input from a file instead of stdin.");
        var maxTokensOpt = new Option<int?>("--max-tokens", "Maximum output tokens.");
        cmd.AddOption(kindOpt);
        cmd.AddOption(fileOpt);
        cmd.AddOption(maxTokensOpt);
        cmd.SetHandler(async context =>
        {
            var kind = context.ParseResult.GetValueForOption(kindOpt);
            var file = context.ParseResult.GetValueForOption(fileOpt);
            var maxTokens = context.ParseResult.GetValueForOption(maxTokensOpt);
            string input;

            try
            {
                input = string.IsNullOrWhiteSpace(file)
                    ? await Console.In.ReadToEndAsync(context.GetCancellationToken())
                    : fileSystem.ReadAllText(file);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"SUMMARY\nError: {ex.Message}");
                context.ExitCode = 1;
                return;
            }

            var result = await compressService.CompressAsync(input, kind, command: null, maxTokens, context.GetCancellationToken());
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
