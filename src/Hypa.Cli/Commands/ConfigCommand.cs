using System.CommandLine;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.Cli.Attach.Config;
using Hypa.Infrastructure.Config;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class ConfigCommand(ConfigService service, IAttachConfigLoader? attachConfig = null)
{
    public Command Build()
    {
        var configCmd = new Command("config", "Manage Hypa configuration.");
        configCmd.Add(BuildShow());
        configCmd.Add(BuildCheck());
        configCmd.Add(BuildResetKeys());
        return configCmd;
    }

    private Command BuildShow()
    {
        var showCmd = new Command("show", "Display the resolved configuration as JSON.");
        showCmd.SetAction(async (parseResult, ct) =>
        {
            var result = await service.GetConfigAsync(ct);
            if (result.IsOk)
            {
                var json = JsonSerializer.Serialize(result.Value, HypaConfigJsonContext.Default.HypaConfig);
                Console.WriteLine(json);
            }
            else
            {
                Console.Error.WriteLine($"error: {result.Error.Code}: {result.Error.Message}");
                return 1;
            }

            return 0;
        });
        return showCmd;
    }

    private Command BuildCheck()
    {
        var cmd = new Command("check", "Validate attach config.toml without starting mux.");
        cmd.SetAction((_, _) =>
        {
            var loader = attachConfig ?? new FileAttachConfigLoader();
            var loaded = loader.Load();
            if (!loaded.IsOk)
            {
                Console.WriteLine("config: issues found");
                foreach (var error in loaded.Errors)
                    Console.WriteLine(error.ToString());
                return Task.FromResult(1);
            }

            if (!KeysConfigMapper.TryCompile(loaded.Value.Keys, Console.Out, out _))
                return Task.FromResult(1);

            Console.WriteLine("config: ok");
            return Task.FromResult(0);
        });
        return cmd;
    }

    private Command BuildResetKeys()
    {
        var cmd = new Command("reset-keys", "Back up attach config.toml and restore built-in keys.");
        cmd.SetAction((_, _) =>
        {
            var loader = attachConfig ?? new FileAttachConfigLoader();
            var result = loader.ResetKeys();
            if (!result.IsOk)
            {
                Console.Error.WriteLine("config: issues found");
                foreach (var error in result.Errors)
                    Console.Error.WriteLine(error.ToString());
                return Task.FromResult(1);
            }

            Console.WriteLine(result.Value.Message);
            return Task.FromResult(0);
        });
        return cmd;
    }
}
