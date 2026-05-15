using System.CommandLine;
using Hypa.Cli.Commands;
using Hypa.Runtime.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hypa.Cli.DI;

public static class CliServiceExtensions
{
    public static IServiceCollection AddCli(this IServiceCollection services)
    {
        services.AddSingleton<DoctorService>();
        services.AddSingleton<ConfigService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<ArtifactService>();
        services.AddSingleton<CommandRewriteService>();
        services.AddSingleton<CommandRunnerService>();
        services.AddSingleton<FilterService>();
        services.AddSingleton<TrustService>();
        services.AddSingleton<ParseHealthService>();
        services.AddSingleton<CodeIndexService>();
        services.AddSingleton<CodeQueryService>();
        services.AddSingleton<CodeDiagnosticsService>();
        services.AddSingleton<HookService>();
        services.AddSingleton<InitService>();
        services.AddSingleton<UninstallService>();
        services.AddSingleton<DoctorCommand>();
        services.AddSingleton<ConfigCommand>();
        services.AddSingleton<VersionCommand>();
        services.AddSingleton<SessionCommand>();
        services.AddSingleton<ArtifactsCommand>();
        services.AddSingleton<RewriteCommand>();
        services.AddSingleton<RunCommand>();
        services.AddSingleton<GitCommand>();
        services.AddSingleton<DotnetCommand>();
        services.AddSingleton<KubectlCommand>();
        services.AddSingleton<DockerCommand>();
        services.AddSingleton<FiltersCommand>();
        services.AddSingleton<TrustCommand>();
        services.AddSingleton<ParseHealthCommand>();
        services.AddSingleton<CodeCommand>();
        services.AddSingleton<HookCommand>();
        services.AddSingleton<InitCommand>();
        services.AddSingleton<UninstallCommand>();
        services.AddSingleton<SkillCommand>();
        services.AddSingleton<ServeCommand>();

        services.AddSingleton<RootCommand>(sp =>
        {
            var root = new RootCommand("hypa — context-aware AI tooling for Claude Code.");
            root.AddCommand(sp.GetRequiredService<DoctorCommand>().Build());
            root.AddCommand(sp.GetRequiredService<ConfigCommand>().Build());
            root.AddCommand(sp.GetRequiredService<VersionCommand>().Build());
            root.AddCommand(sp.GetRequiredService<SessionCommand>().Build());
            root.AddCommand(sp.GetRequiredService<ArtifactsCommand>().Build());
            root.AddCommand(sp.GetRequiredService<RewriteCommand>().Build());
            root.AddCommand(sp.GetRequiredService<GitCommand>().Build());
            root.AddCommand(sp.GetRequiredService<DotnetCommand>().Build());
            root.AddCommand(sp.GetRequiredService<KubectlCommand>().Build());
            root.AddCommand(sp.GetRequiredService<DockerCommand>().Build());
            root.AddCommand(sp.GetRequiredService<FiltersCommand>().Build());
            root.AddCommand(sp.GetRequiredService<TrustCommand>().Build());
            root.AddCommand(sp.GetRequiredService<ParseHealthCommand>().Build());
            root.AddCommand(sp.GetRequiredService<CodeCommand>().Build());
            root.AddCommand(sp.GetRequiredService<HookCommand>().Build());
            root.AddCommand(sp.GetRequiredService<InitCommand>().Build());
            root.AddCommand(sp.GetRequiredService<UninstallCommand>().Build());
            root.AddCommand(sp.GetRequiredService<SkillCommand>().Build());
            root.AddCommand(sp.GetRequiredService<ServeCommand>().Build());
            sp.GetRequiredService<RunCommand>().AttachTo(root);
            return root;
        });

        return services;
    }
}
