using System.CommandLine;
using System.Text.Json;
using Hypa.Infrastructure.Config;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class ConfigCommand(ConfigService service)
{
    public Command Build()
    {
        var configCmd = new Command("config", "Manage Hypa configuration.");
        configCmd.AddCommand(BuildShow());
        return configCmd;
    }

    private Command BuildShow()
    {
        var showCmd = new Command("show", "Display the resolved configuration as JSON.");
        showCmd.SetHandler(async (context) =>
        {
            var ct = context.GetCancellationToken();
            var result = await service.GetConfigAsync(ct);
            if (result.IsOk)
            {
                var json = JsonSerializer.Serialize(result.Value, HypaConfigJsonContext.Default.HypaConfig);
                Console.WriteLine(json);
            }
            else
            {
                Console.Error.WriteLine($"error: {result.Error.Code}: {result.Error.Message}");
                context.ExitCode = 1;
            }
        });
        return showCmd;
    }
}
