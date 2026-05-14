using System.CommandLine;
using Hypa.Cli.DI;
using Hypa.Infrastructure.DI;
using Hypa.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder()
    .ConfigureServices((_, services) =>
    {
        services.AddInfrastructure();
        services.AddCli();
    })
    .Build();

await host.Services.GetRequiredService<SqliteSchemaInitializer>().InitAsync(CancellationToken.None);

var rootCommand = host.Services.GetRequiredService<RootCommand>();
return await rootCommand.InvokeAsync(args);
