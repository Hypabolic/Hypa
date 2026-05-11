using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Compression.Stages;
using Hypa.Infrastructure.Config;
using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Infrastructure.Doctor;
using Hypa.Infrastructure.Filters;
using Hypa.Infrastructure.Parsers;
using Hypa.Infrastructure.ProjectRoot;
using Hypa.Infrastructure.Reducers;
using Hypa.Infrastructure.Rewrite;
using Hypa.Infrastructure.Runner;
using Hypa.Infrastructure.Storage;
using Hypa.Infrastructure.System;
using Hypa.Infrastructure.Trust;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Parsers.Canonical;
using Hypa.Runtime.Domain.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace Hypa.Infrastructure.DI;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IProjectRootDetector, GitProjectRootDetector>();
        services.AddSingleton<IFileSystem, SystemFileSystem>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IConfigLoader, JsonConfigLoader>();

        services.AddSingleton<IDoctorCheck, RuntimeVersionCheck>();
        services.AddSingleton<IDoctorCheck, OsCheck>();
        services.AddSingleton<IDoctorCheck, ConfigPathCheck>();
        services.AddSingleton<IDoctorCheck, ProjectRootCheck>();
        services.AddSingleton<IDoctorCheck, RewriteRegistryCheck>();

        services.AddSingleton<HypaDataOptions>();
        services.AddSingleton<SqliteSchemaInitializer>();
        services.AddSingleton<ISessionRepository, SqliteSessionRepository>();
        services.AddSingleton<IEvidenceLedger, SqliteEvidenceLedger>();
        services.AddSingleton<IArtifactRepository, SqliteArtifactRepository>();
        services.AddSingleton<ISessionResolver, SessionResolver>();

        services.AddSingleton<ICommandRunner, ProcessCommandRunner>();

        services.AddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.AddSingleton<ImportantLineClassifier>();
        services.AddSingleton<ICompressionStage, AnsiStripStage>();
        services.AddSingleton<ICompressionStage, BlankLineCollapseStage>();
        services.AddSingleton<ICompressionStage, ProgressFilterStage>();
        services.AddSingleton<ICompressionStage, DeduplicateStage>();
        services.AddSingleton<TruncationStage>(sp =>
            new TruncationStage(CompressionOptions.Default, sp.GetRequiredService<ImportantLineClassifier>()));
        services.AddSingleton<IOutputCompressor, GitOutputCompressor>();
        services.AddSingleton<IOutputCompressor, DotnetBuildOutputCompressor>();
        services.AddSingleton<IOutputCompressor, DotnetTestOutputCompressor>();
        services.AddSingleton<IOutputCompressor, TscOutputCompressor>();
        services.AddSingleton<IOutputCompressor, KubectlOutputCompressor>();
        services.AddSingleton<IOutputCompressor, PackageManagerOutputCompressor>();
        services.AddSingleton<IOutputCompressor, GenericOutputCompressor>();

        // Filters
        services.AddSingleton<IFilterEngine, FilterEngine>();
        services.AddSingleton<IFilterRepository, FileSystemFilterRepository>();
        services.AddSingleton<FilterSavingsEstimator>();
        services.AddSingleton<ITrustStore, SqliteTrustStore>();
        services.AddSingleton<IParseMetricsRepository, SqliteParseMetricsRepository>();
        services.AddSingleton<ICodeIndexRepository, SqliteCodeIndexRepository>();
        services.AddSingleton<ICodeStructureProvider, TreeSitterCodeStructureProvider>();
        services.AddSingleton<ICodeStructureProvider, RegexFallbackCodeStructureProvider>();

        // Structured parsers and formatters
        services.AddSingleton<IOutputParser<TestRunResult>, DotnetTestParser>();
        services.AddSingleton<IOutputParser<BuildResult>, DotnetBuildParser>();
        services.AddSingleton<IOutputParser<LintResult>, TscParser>();
        services.AddSingleton<ITokenFormatter<TestRunResult>, TestRunResultFormatter>();
        services.AddSingleton<ITokenFormatter<BuildResult>, BuildResultFormatter>();
        services.AddSingleton<ITokenFormatter<LintResult>, LintResultFormatter>();

        services.AddSingleton<IShellLexer, ShellLexer>();
        services.AddSingleton<ICommandRewriteStrategy, GitRewriteStrategy>();
        services.AddSingleton<ICommandRewriteStrategy, DotnetRewriteStrategy>();
        services.AddSingleton<ICommandRewriteStrategy, PackageManagerRewriteStrategy>();
        services.AddSingleton<ICommandRewriteStrategy, TscRewriteStrategy>();
        services.AddSingleton<ICommandRewriteStrategy, DockerRewriteStrategy>();
        services.AddSingleton<ICommandRewriteStrategy, KubectlRewriteStrategy>();
        services.AddSingleton<ICommandRewriteStrategy, GenericWrapperStrategy>();
        services.AddSingleton<GenericWrapperStrategy>();
        services.AddSingleton<ICommandRewriteRegistry, CommandRewriteRegistry>();

        return services;
    }
}
