using Hypa.Infrastructure.Filters;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Filters;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Filters;

public sealed class FileSystemFilterRepositoryTests : IDisposable
{
    private readonly string _projectRoot = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }

    [Fact]
    public void GetById_UsesActualProjectLocalFilename_ForTrustCheck()
    {
        var filtersDir = Path.Combine(_projectRoot, ".hypa", "filters");
        Directory.CreateDirectory(filtersDir);
        var filterPath = Path.Combine(filtersDir, "my-filter.json");
        File.WriteAllText(filterPath, """
            {
              "id": "myFilter",
              "description": "Filter with filename that differs from id.",
              "appliesTo": [],
              "stages": [
                { "kind": "StripAnsi" }
              ]
            }
            """);

        var hash = FileSystemFilterRepository.ComputeHash(filterPath);
        var trustStore = Substitute.For<ITrustStore>();
        var rootDetector = Substitute.For<IProjectRootDetector>();
        rootDetector.Detect(Arg.Any<string>()).Returns(_projectRoot);
        trustStore.IsTrusted(_projectRoot, filterPath, hash).Returns(true);

        var repository = new FileSystemFilterRepository(trustStore, rootDetector);

        var filter = repository.GetById("myFilter", FilterScope.ProjectLocal);

        Assert.NotNull(filter);
        Assert.Equal("myFilter", filter.Id);
        trustStore.Received(1).IsTrusted(_projectRoot, filterPath, hash);
    }
}
