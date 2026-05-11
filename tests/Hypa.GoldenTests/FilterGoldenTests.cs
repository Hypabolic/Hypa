using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.Infrastructure.Filters;
using Hypa.Runtime.Domain.Filters;
using Xunit;

namespace Hypa.GoldenTests;

public sealed class FilterGoldenTests
{
    private static readonly string FixturesPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "Fixtures", "filters");

    private static readonly FilterEngine Engine = new();

    [Theory]
    [InlineData("ansi_strip")]
    [InlineData("dotnet_msbuild_noise")]
    [InlineData("keep_lines_errors")]
    [InlineData("match_output_build_success")]
    [InlineData("match_output_clean_success")]
    public async Task Filter(string fixtureName)
    {
        var dir = Path.Combine(FixturesPath, fixtureName);
        var input = Normalize(await File.ReadAllTextAsync(Path.Combine(dir, "input.txt")));
        var metaJson = await File.ReadAllTextAsync(Path.Combine(dir, "meta.json"));
        var meta = JsonSerializer.Deserialize(metaJson, FilterTestJsonContext.Default.FilterGoldenMeta)!;

        var filter = ResolveFilter(meta);
        var result = Engine.Apply(filter, input);

        await Verify(Normalize(result.Text))
            .UseDirectory(dir)
            .UseFileName(fixtureName);
    }

    private static CompiledFilterDefinition ResolveFilter(FilterGoldenMeta meta)
    {
        if (!string.IsNullOrEmpty(meta.FilterId))
        {
            return BuiltInFilters.All.FirstOrDefault(f => f.Id == meta.FilterId)
                ?? throw new InvalidOperationException($"No built-in filter found with id='{meta.FilterId}'");
        }

        if (meta.FilterDefinition is not null)
            return BuiltInFilters.Compile(meta.FilterDefinition);

        throw new InvalidOperationException("meta.json must specify either filterId or filterDefinition.");
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
}

internal sealed record FilterGoldenMeta
{
    [JsonPropertyName("filterId")]
    public string? FilterId { get; init; }

    [JsonPropertyName("filterScope")]
    public string? FilterScope { get; init; }

    [JsonPropertyName("filterDefinition")]
    public FilterDefinition? FilterDefinition { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(FilterGoldenMeta))]
[JsonSerializable(typeof(FilterDefinition))]
[JsonSerializable(typeof(FilterStage))]
[JsonSerializable(typeof(FilterStageKind))]
[JsonSerializable(typeof(List<FilterStage>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class FilterTestJsonContext : JsonSerializerContext { }
