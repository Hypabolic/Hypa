using System.CommandLine;
using Hypa.Cli.Commands;
using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Runtime.Application.Services;
using Xunit;

namespace Hypa.UnitTests.Cli;

public class CodeParseWorkerCommandTests
{
    [Fact]
    public void Worker_command_builds_and_accepts_every_option()
    {
        var registry = new CodeStructureProviderRegistry(
        [
            new MarkdownStructureProvider(),
            new RegexFallbackCodeStructureProvider(),
        ]);
        var command = new CodeParseWorkerCommand(registry).Build();

        var result = command.Parse(
        [
            "--language", "markdown",
            "--path", "/tmp/a.md",
            "--relative-path", "a.md",
            "--project-root", "/tmp",
            "--git-blob-oid", "abc",
            "--timeout-ms", "1000",
            "--quiet",
        ]);

        Assert.Empty(result.Errors);
    }
}
