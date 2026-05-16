using System.CommandLine;
using Hypa.Cli.DI;
using Hypa.Infrastructure.DI;
using Hypa.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder()
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Warning);
        logging.AddFilter("System.Net.Http", LogLevel.Warning);
    })
    .ConfigureServices((_, services) =>
    {
        services.AddInfrastructure();
        services.AddCli();
    })
    .Build();

await host.Services.GetRequiredService<SqliteSchemaInitializer>().InitAsync(CancellationToken.None);

var rootCommand = host.Services.GetRequiredService<RootCommand>();
return await rootCommand.InvokeAsync(args);
