using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Filters;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class FilterServiceTests
{
    private static (FilterService service, IFilterRepository repo, IFilterEngine engine) Make(
        IReadOnlyList<CompiledFilterDefinition>? filters = null)
    {
        var repo = Substitute.For<IFilterRepository>();
        repo.GetAll().Returns(filters ?? []);
        var engine = Substitute.For<IFilterEngine>();
        engine.Apply(Arg.Any<CompiledFilterDefinition>(), Arg.Any<string>())
            .Returns(ci => new FilterResult(ci.ArgAt<string>(1), "none", 0));
        return (new FilterService(repo, engine), repo, engine);
    }

    [Fact]
    public void GetApplicableFilters_ReturnsBuiltInForMatchingExecutable()
    {
        var filter = new CompiledFilterDefinition
        {
            Id = "dotnet-noise",
            AppliesTo = ["dotnet"],
            Scope = FilterScope.BuiltIn,
            Stages = [],
        };
        var (service, _, _) = Make([filter]);
        var result = service.GetApplicableFilters("dotnet");
        Assert.Contains(result, f => f.Id == "dotnet-noise");
    }

    [Fact]
    public void GetApplicableFilters_ExcludesFilterForOtherExecutable()
    {
        var filter = new CompiledFilterDefinition
        {
            Id = "dotnet-noise",
            AppliesTo = ["dotnet"],
            Scope = FilterScope.BuiltIn,
            Stages = [],
        };
        var (service, _, _) = Make([filter]);
        var result = service.GetApplicableFilters("git");
        Assert.Empty(result);
    }

    [Fact]
    public void GetApplicableFilters_IncludesEmptyAppliesTo_ForAnyExecutable()
    {
        var filter = new CompiledFilterDefinition
        {
            Id = "ansi-strip",
            AppliesTo = [],
            Scope = FilterScope.BuiltIn,
            Stages = [],
        };
        var (service, _, _) = Make([filter]);
        var result = service.GetApplicableFilters("git");
        Assert.Contains(result, f => f.Id == "ansi-strip");
    }

    [Fact]
    public void GetApplicableFilters_ReturnsSpecificFiltersBeforeUniversalFilters()
    {
        var universal = new CompiledFilterDefinition
        {
            Id = "ansi-strip",
            AppliesTo = [],
            Scope = FilterScope.BuiltIn,
            Stages = [],
        };
        var specific = new CompiledFilterDefinition
        {
            Id = "dotnet-msbuild-noise",
            AppliesTo = ["dotnet"],
            Scope = FilterScope.BuiltIn,
            Stages = [],
        };
        var (service, _, _) = Make([universal, specific]);

        var result = service.GetApplicableFilters("dotnet");

        Assert.Collection(
            result,
            filter => Assert.Equal("dotnet-msbuild-noise", filter.Id),
            filter => Assert.Equal("ansi-strip", filter.Id));
    }

    [Fact]
    public void GetApplicableFilters_PreservesRepositoryOrderWithinSpecificityGroups()
    {
        var dotnetFirst = new CompiledFilterDefinition { Id = "dotnet-first", AppliesTo = ["dotnet"], Stages = [] };
        var universalFirst = new CompiledFilterDefinition { Id = "universal-first", AppliesTo = [], Stages = [] };
        var dotnetSecond = new CompiledFilterDefinition { Id = "dotnet-second", AppliesTo = ["dotnet"], Stages = [] };
        var universalSecond = new CompiledFilterDefinition { Id = "universal-second", AppliesTo = [], Stages = [] };
        var (service, _, _) = Make([dotnetFirst, universalFirst, dotnetSecond, universalSecond]);

        var result = service.GetApplicableFilters("dotnet");

        Assert.Collection(
            result,
            filter => Assert.Equal("dotnet-first", filter.Id),
            filter => Assert.Equal("dotnet-second", filter.Id),
            filter => Assert.Equal("universal-first", filter.Id),
            filter => Assert.Equal("universal-second", filter.Id));
    }

    [Fact]
    public void GetApplicableFilters_IncludesMatchCommand_WhenCommandMatches()
    {
        var filter = Hypa.Infrastructure.Filters.BuiltInFilters.Compile(new FilterDefinition
        {
            Id = "dotnet-build",
            AppliesTo = ["dotnet"],
            MatchCommand = @"^dotnet\s+build\b",
            Stages = [],
        });
        var (service, _, _) = Make([filter]);

        var result = service.GetApplicableFilters("dotnet", "dotnet build src/App.csproj");

        Assert.Contains(result, f => f.Id == "dotnet-build");
    }

    [Fact]
    public void GetApplicableFilters_ExcludesMatchCommand_WhenCommandDoesNotMatch()
    {
        var filter = Hypa.Infrastructure.Filters.BuiltInFilters.Compile(new FilterDefinition
        {
            Id = "dotnet-build",
            AppliesTo = ["dotnet"],
            MatchCommand = @"^dotnet\s+build\b",
            Stages = [],
        });
        var (service, _, _) = Make([filter]);

        var result = service.GetApplicableFilters("dotnet", "dotnet test");

        Assert.Empty(result);
    }

    [Fact]
    public void TestFilter_ReturnsFilteredText()
    {
        var filter = new CompiledFilterDefinition { Id = "my-filter", AppliesTo = [], Stages = [] };
        var repo = Substitute.For<IFilterRepository>();
        repo.GetAll().Returns([filter]);
        repo.GetById("my-filter", Arg.Any<FilterScope?>()).Returns(filter);
        var engine = Substitute.For<IFilterEngine>();
        engine.Apply(filter, "input text").Returns(new FilterResult("filtered text", "my-filter", 1));
        var service = new FilterService(repo, engine);

        var result = service.TestFilter("my-filter", "input text");

        Assert.Equal("filtered text", result);
    }

    [Fact]
    public void TestFilter_ReturnsNotFound_WhenFilterMissing()
    {
        var (service, _, _) = Make([]);
        var result = service.TestFilter("nonexistent", "input");
        Assert.Contains("not found", result);
    }

    [Fact]
    public void ListFilters_ReturnsAll()
    {
        var filters = new[]
        {
            new CompiledFilterDefinition { Id = "a", Stages = [] },
            new CompiledFilterDefinition { Id = "b", Stages = [] },
        };
        var (service, _, _) = Make(filters);
        var result = service.ListFilters();
        Assert.Equal(2, result.Count);
    }
}
