using Hypa.Infrastructure.Filters;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Filters;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Filters;

public sealed class FileSystemFilterRepositoryTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void GetAll_LoadsBuiltInAndUserGlobalFilters_WhenCurrentDirectoryIsUnavailable()
    {
        var userGlobalDir = Path.Combine(_tempRoot, "user-global-filters");
        Directory.CreateDirectory(userGlobalDir);
        File.WriteAllText(Path.Combine(userGlobalDir, "global.json"), """
            {
              "id": "globalFilter",
              "description": "User-global filter.",
              "appliesTo": [],
              "stages": [
                { "kind": "StripAnsi" }
              ]
            }
            """);

        var trustStore = Substitute.For<ITrustStore>();
        var rootDetector = Substitute.For<IProjectRootDetector>();

        var repository = new FileSystemFilterRepository(
            trustStore, rootDetector, userGlobalDir, getCurrentDirectory: () => null);

        var filters = repository.GetAll();

        Assert.Contains(filters, f => f.Scope == FilterScope.BuiltIn);
        Assert.Contains(filters, f => f.Id == "globalFilter" && f.Scope == FilterScope.UserGlobal);
        Assert.DoesNotContain(filters, f => f.Scope == FilterScope.ProjectLocal);
        rootDetector.DidNotReceive().Detect(Arg.Any<string>());
    }
}
