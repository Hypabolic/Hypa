using Hypa.Infrastructure.Rewrite;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

public sealed class CommandRewriteRegistryTests
{
    private static readonly RewriteContext DefaultContext = new(
        IsHypaDisabled: false,
        ExcludeCommands: [],
        GenericWrapperEnabled: true);

    private static readonly RewriteContext NoWrapperContext = new(
        IsHypaDisabled: false,
        ExcludeCommands: [],
        GenericWrapperEnabled: false);

    private static CommandRewriteRegistry BuildRegistry()
    {
        var lexer = new ShellLexer();
        var wrapper = new GenericWrapperStrategy();
        var strategies = new ICommandRewriteStrategy[]
        {
            new GitRewriteStrategy(),
            new DotnetRewriteStrategy(),
            new PackageManagerRewriteStrategy(),
            new TscRewriteStrategy(),
            new DockerRewriteStrategy(),
            new KubectlRewriteStrategy(),
            wrapper,
        };
        return new CommandRewriteRegistry(lexer, strategies, wrapper);
    }

    // --- First-class rewrites ---

    [Theory]
    [InlineData("git status", "hypa git status")]
    [InlineData("git diff HEAD~1", "hypa git diff HEAD~1")]
    [InlineData("git log --oneline", "hypa git log --oneline")]
    [InlineData("git --no-pager diff --staged", "hypa git --no-pager diff --staged")]
    [InlineData("git -P diff --staged", "hypa git -P diff --staged")]
    [InlineData("git --paginate diff --staged", "hypa git --paginate diff --staged")]
    public void Git_SupportedSubcommand_ReturnsRewritten(string input, string expected)
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite(input, DefaultContext);
        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal(expected, result.Command);
    }

    [Theory]
    [InlineData("dotnet build", "hypa dotnet build")]
    [InlineData("dotnet test", "hypa dotnet test")]
    public void Dotnet_SupportedSubcommand_ReturnsRewritten(string input, string expected)
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite(input, DefaultContext);
        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal(expected, result.Command);
    }

    [Theory]
    [InlineData("docker ps", "hypa docker ps")]
    [InlineData("docker logs my-container", "hypa docker logs my-container")]
    public void Docker_SupportedSubcommand_ReturnsRewritten(string input, string expected)
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite(input, DefaultContext);
        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal(expected, result.Command);
    }

    [Theory]
    [InlineData("kubectl get pods", "hypa kubectl get pods")]
    [InlineData("kubectl describe pod my-pod", "hypa kubectl describe pod my-pod")]
    public void Kubectl_SupportedSubcommand_ReturnsRewritten(string input, string expected)
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite(input, DefaultContext);
        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal(expected, result.Command);
    }

    // --- Generic wrapper ---

    [Theory]
    [InlineData("custom-check --json", "hypa -c \"custom-check --json\"")]
    [InlineData("pnpm install", "hypa -c \"pnpm install\"")]
    [InlineData("tsc --watch", "hypa -c \"tsc --watch\"")]
    public void UnknownCommand_WithGenericWrapper_ReturnsGenericWrapper(string input, string expected)
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite(input, DefaultContext);
        Assert.Equal(RewriteOutcome.GenericWrapper, result.Outcome);
        Assert.Equal(expected, result.Command);
    }

    // --- Passthrough cases ---

    [Theory]
    [InlineData("vim file.cs")]
    [InlineData("git push origin main")]
    [InlineData("kubectl logs -f pod/foo")]
    public void Passthrough_WhenNoStrategyMatchesAndWrapperDisabled(string input)
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite(input, NoWrapperContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void Pipe_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("cat file | grep foo", DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void ShellExpansion_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("echo $(pwd)", DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void ExcludedVerb_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var ctx = DefaultContext with { ExcludeCommands = ["git"] };
        var result = registry.Rewrite("git status", ctx);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void StatefulBuiltin_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("cd /tmp", DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void CompoundCommand_WithCdBuiltin_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("cd /tmp && pwd", DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void CompoundCommand_WithExportBuiltin_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("export FOO=bar && echo hi", DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void EnvPrefixedStatefulBuiltin_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("FOO=bar cd /tmp", DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void CompoundCommand_WithMultipleEnvPrefixesAndCd_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("FOO=bar BAZ=1 cd /tmp && pwd", DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void QuotedStatefulBuiltin_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("'cd' /tmp", DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void CompoundCommand_WithStatefulBuiltinAfterRewrittenSegment_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("git status && cd /tmp", DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void StatefulBuiltinNameInArgument_DoesNotReturnPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("echo cd", DefaultContext);
        Assert.Equal(RewriteOutcome.GenericWrapper, result.Outcome);
    }

    [Fact]
    public void StatefulBuiltinNameInQuotedArgument_DoesNotReturnPassthrough()
    {
        var lexer = new ShellLexer();
        var wrapper = new GenericWrapperStrategy();
        var registry = new CommandRewriteRegistry(lexer, [wrapper], wrapper);

        var result = registry.Rewrite("git commit -m \"cd\"", DefaultContext);

        Assert.Equal(RewriteOutcome.GenericWrapper, result.Outcome);
    }

    [Fact]
    public void UppercaseStatefulBuiltinName_DoesNotReturnPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("CD /tmp", DefaultContext);
        Assert.Equal(RewriteOutcome.GenericWrapper, result.Outcome);
    }

    // --- Redirect passthrough ---

    [Theory]
    [InlineData("git status > out.txt")]
    [InlineData("dotnet test 2>&1")]
    [InlineData("docker logs container >> log.txt")]
    [InlineData("kubectl get pods > pods.txt")]
    public void FirstClassCommand_WithRedirect_ReturnsPassthrough(string input)
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite(input, DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    // --- Shellism passthrough ---

    [Fact]
    public void TrailingBackgroundOperator_ReturnsPassthrough()
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite("git status &", DefaultContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }

    // --- Compound rewriting ---

    [Theory]
    [InlineData("git status && dotnet build", "hypa git status && hypa dotnet build")]
    [InlineData("git status || dotnet build", "hypa git status || hypa dotnet build")]
    [InlineData("git status ; dotnet build", "hypa git status ; hypa dotnet build")]
    public void CompoundCommand_RewritesEachSegment(string input, string expected)
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite(input, DefaultContext);
        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal(expected, result.Command);
    }

    [Theory]
    [InlineData("vim a && nano b")]
    [InlineData("git push && git pull")]
    public void CompoundCommand_AllSegmentsPassthrough_ReturnsPassthrough(string input)
    {
        var registry = BuildRegistry();
        var result = registry.Rewrite(input, NoWrapperContext);
        Assert.Equal(RewriteOutcome.Passthrough, result.Outcome);
    }
}
