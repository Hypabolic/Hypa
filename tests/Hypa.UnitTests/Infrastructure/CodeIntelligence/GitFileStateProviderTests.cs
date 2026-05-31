using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.CodeIntelligence;

public sealed class GitFileStateProviderTests
{
    private const string ProjectRoot = "/repo";

    [Fact]
    public async Task GetCleanBlobOidsAsync_ParsesLsFilesOutput_ReturnsOidMap()
    {
        var runner = new FakeCommandRunner()
            .Add(["ls-files", "-s"],
                "100644 oid-one 0\tsrc/One.cs\n100644 oid-two 0\tdocs/Two.md\n")
            .Add(["ls-files", "--modified"], "");
        var provider = new GitFileStateProvider(runner);

        var result = await provider.GetCleanBlobOidsAsync(ProjectRoot, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("oid-one", result["src/One.cs"]);
        Assert.Equal("oid-two", result["docs/Two.md"]);
    }

    [Fact]
    public async Task GetCleanBlobOidsAsync_ExcludesDirtyFiles()
    {
        var runner = new FakeCommandRunner()
            .Add(["ls-files", "-s"],
                "100644 clean-oid 0\tsrc/Clean.cs\n100644 dirty-oid 0\tsrc/Dirty.cs\n")
            .Add(["ls-files", "--modified"], "src/Dirty.cs\n");
        var provider = new GitFileStateProvider(runner);

        var result = await provider.GetCleanBlobOidsAsync(ProjectRoot, CancellationToken.None);

        Assert.NotNull(result);
        var clean = Assert.Single(result);
        Assert.Equal("src/Clean.cs", clean.Key);
        Assert.Equal("clean-oid", clean.Value);
    }

    [Fact]
    public async Task GetCleanBlobOidsAsync_WhenGitUnavailable_ReturnsNull()
    {
        var runner = new FakeCommandRunner()
            .Add(["ls-files", "-s"], "", exitCode: 128);
        var provider = new GitFileStateProvider(runner);

        var result = await provider.GetCleanBlobOidsAsync(ProjectRoot, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCleanBlobOidsAsync_WhenEmptyRepo_ReturnsEmptyDictionary()
    {
        var runner = new FakeCommandRunner()
            .Add(["ls-files", "-s"], "")
            .Add(["ls-files", "--modified"], "");
        var provider = new GitFileStateProvider(runner);

        var result = await provider.GetCleanBlobOidsAsync(ProjectRoot, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetCleanBlobOidAsync_TrackedCleanFile_ReturnsOid()
    {
        var runner = new FakeCommandRunner()
            .Add(["ls-files", "-s", "--", "src/File.cs"], "100644 clean-oid 0\tsrc/File.cs\n")
            .Add(["ls-files", "--modified", "--", "src/File.cs"], "");
        var provider = new GitFileStateProvider(runner);

        var result = await provider.GetCleanBlobOidAsync("/repo/src/File.cs", ProjectRoot, CancellationToken.None);

        Assert.Equal("clean-oid", result);
    }

    [Fact]
    public async Task GetCleanBlobOidAsync_DirtyFile_ReturnsNull()
    {
        var runner = new FakeCommandRunner()
            .Add(["ls-files", "-s", "--", "src/File.cs"], "100644 dirty-oid 0\tsrc/File.cs\n")
            .Add(["ls-files", "--modified", "--", "src/File.cs"], "src/File.cs\n");
        var provider = new GitFileStateProvider(runner);

        var result = await provider.GetCleanBlobOidAsync("/repo/src/File.cs", ProjectRoot, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCleanBlobOidAsync_UntrackedFile_ReturnsNull()
    {
        var runner = new FakeCommandRunner()
            .Add(["ls-files", "-s", "--", "src/File.cs"], "")
            .Add(["ls-files", "--modified", "--", "src/File.cs"], "");
        var provider = new GitFileStateProvider(runner);

        var result = await provider.GetCleanBlobOidAsync("/repo/src/File.cs", ProjectRoot, CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly Dictionary<string, CommandOutput> _outputs = new(StringComparer.Ordinal);

        public FakeCommandRunner Add(IReadOnlyList<string> arguments, string stdout, int exitCode = 0)
        {
            _outputs[Key(arguments)] = CommandOutput.Captured(stdout, "", exitCode, TimeSpan.Zero);
            return this;
        }

        public Task<Result<CommandOutput, Error>> RunAsync(CommandInvocation invocation, CancellationToken ct)
        {
            if (_outputs.TryGetValue(Key(invocation.Arguments), out var output))
                return Task.FromResult(Result<CommandOutput, Error>.Ok(output));

            return Task.FromResult(Result<CommandOutput, Error>.Fail(
                new Error("UNEXPECTED_COMMAND", string.Join(" ", invocation.Arguments))));
        }

        private static string Key(IReadOnlyList<string> arguments) => string.Join('\u001f', arguments);
    }
}
