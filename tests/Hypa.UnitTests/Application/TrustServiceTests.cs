using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Filters;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class TrustServiceTests
{
    private static readonly string FiltersDir = Path.Combine("/repo", ".hypa", "filters");
    private static readonly string FilterA = Path.Combine(FiltersDir, "a.json");
    private static readonly string FilterB = Path.Combine(FiltersDir, "b.json");

    private static (TrustService service, ITrustStore trustStore, IProjectRootDetector rootDetector, IFileSystem fileSystem, IClock clock)
        Make()
    {
        var trustStore = Substitute.For<ITrustStore>();
        var rootDetector = Substitute.For<IProjectRootDetector>();
        var fileSystem = Substitute.For<IFileSystem>();
        var clock = Substitute.For<IClock>();

        fileSystem.GetCurrentDirectory().Returns("/repo/subdir");
        rootDetector.Detect("/repo/subdir").Returns("/repo");
        clock.UtcNow.Returns(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));

        return (new TrustService(trustStore, rootDetector, fileSystem, clock), trustStore, rootDetector, fileSystem, clock);
    }

    [Fact]
    public async Task GrantFiltersAsync_ReturnsNoRoot_WhenProjectRootMissing()
    {
        var (service, _, rootDetector, _, _) = Make();
        rootDetector.Detect("/repo/subdir").Returns((string?)null);

        var result = await service.GrantFiltersAsync(CancellationToken.None);

        Assert.Contains("No project root detected", result);
    }

    [Fact]
    public async Task GrantFiltersAsync_ReturnsNoFilterDirectory_WhenDirectoryMissing()
    {
        var (service, _, _, fileSystem, _) = Make();
        fileSystem.DirectoryExists(FiltersDir).Returns(false);

        var result = await service.GrantFiltersAsync(CancellationToken.None);

        Assert.Equal($"No filter directory found at {FiltersDir}", result);
    }

    [Fact]
    public async Task GrantFiltersAsync_ReturnsNoFilterFiles_WhenDirectoryHasNoJsonFiles()
    {
        var (service, _, _, fileSystem, _) = Make();
        fileSystem.DirectoryExists(FiltersDir).Returns(true);
        fileSystem.GetFiles(FiltersDir, "*.json").Returns([]);

        var result = await service.GrantFiltersAsync(CancellationToken.None);

        Assert.Equal($"No filter files found in {FiltersDir}", result);
    }

    [Fact]
    public async Task GrantFiltersAsync_GrantsEachFilterWithFileHash()
    {
        var (service, trustStore, _, fileSystem, clock) = Make();
        fileSystem.DirectoryExists(FiltersDir).Returns(true);
        fileSystem.GetFiles(FiltersDir, "*.json").Returns([
            FilterA,
            FilterB,
        ]);
        fileSystem.ReadAllBytes(FilterA).Returns(Encoding.UTF8.GetBytes("alpha"));
        fileSystem.ReadAllBytes(FilterB).Returns(Encoding.UTF8.GetBytes("beta"));

        var result = await service.GrantFiltersAsync(CancellationToken.None);

        Assert.Equal($"Trusted 2 filter file(s) in {FiltersDir}", result);
        await trustStore.Received(1).GrantAsync(Arg.Is<TrustRecord>(r =>
            r.ProjectRoot == "/repo" &&
            r.FilterFilePath == FilterA &&
            r.FileHash == "8ED3F6AD685B959EAD7022518E1AF76CD816F8E8EC7CCDDA1ED4018E8F2223F8" &&
            r.GrantedAt == clock.UtcNow), CancellationToken.None);
        await trustStore.Received(1).GrantAsync(Arg.Is<TrustRecord>(r =>
            r.ProjectRoot == "/repo" &&
            r.FilterFilePath == FilterB &&
            r.FileHash == "F44E64E75F3948E9F73F8DFA94721C4CE8CBB4F265C4790C702B2D41CFBF2753" &&
            r.GrantedAt == clock.UtcNow), CancellationToken.None);
    }
}
