using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Reducers;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.GoldenTests;

public sealed class ReducerGoldenTests
{
    private static readonly string FixturesPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "Fixtures", "reducers");

    private static readonly IReadOnlyList<IOutputCompressor> Reducers = BuildReducers();

    private static IReadOnlyList<IOutputCompressor> BuildReducers()
    {
        var counter = new CharDivTokenCounter();
        return
        [
            new GitOutputCompressor(counter),
            new DotnetBuildOutputCompressor(counter),
            new DotnetTestOutputCompressor(counter),
            new TscOutputCompressor(counter),
            new KubectlOutputCompressor(counter),
            new PackageManagerOutputCompressor(counter),
        ];
    }

    [Theory]
    [InlineData("git_status_clean")]
    [InlineData("git_status_dirty")]
    [InlineData("git_status_conflicts")]
    [InlineData("git_log_simple")]
    [InlineData("git_diff_stat")]
    [InlineData("dotnet_build_errors")]
    [InlineData("dotnet_build_success")]
    [InlineData("dotnet_test_failures")]
    [InlineData("tsc_errors")]
    [InlineData("kubectl_get_pods")]
    [InlineData("kubectl_describe_crash")]
    [InlineData("pnpm_install_conflict")]
    public async Task Reduce(string fixtureName)
    {
        var dir = Path.Combine(FixturesPath, fixtureName);
        var input = Normalize(await File.ReadAllTextAsync(Path.Combine(dir, "input.txt")));
        var metaJson = await File.ReadAllTextAsync(Path.Combine(dir, "meta.json"));
        var meta = JsonSerializer.Deserialize(metaJson, ReducerTestJsonContext.Default.ReducerGoldenMeta)!;

        var invocation = CommandInvocation.Buffered(
            meta.Executable,
            meta.Args,
            $"{meta.Executable} {string.Join(' ', meta.Args)}");

        var output = CommandOutput.Captured(input, string.Empty, meta.ExitCode, TimeSpan.Zero);

        var reducer = Reducers.FirstOrDefault(r => r.CanHandle(invocation))
            ?? throw new InvalidOperationException($"No reducer found for fixture '{fixtureName}' (executable={meta.Executable} args={string.Join(',', meta.Args)})");

        var result = reducer.Compress(invocation, output, CompressionOptions.Default);

        Assert.Equal(meta.Reducer, result.ReducerId);

        await Verify(Normalize(result.Text))
            .UseDirectory(dir)
            .UseFileName(fixtureName);
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
}

internal sealed record ReducerGoldenMeta
{
    [JsonPropertyName("reducer")]
    public string Reducer { get; init; } = string.Empty;

    [JsonPropertyName("executable")]
    public string Executable { get; init; } = string.Empty;

    [JsonPropertyName("args")]
    public string[] Args { get; init; } = [];

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; init; }
}

[JsonSerializable(typeof(ReducerGoldenMeta))]
internal sealed partial class ReducerTestJsonContext : JsonSerializerContext { }
