using System.CommandLine;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Hypa.Cli.DI;
using Hypa.Infrastructure.DI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class CompletionCommandTests
{
    private const string ExpectedZshPrefix =
        """
        #compdef hypa

        autoload -U is-at-least

        _hypa() {
            typeset -A opt_args
            typeset -a _arguments_options
            local ret=1

        """;

    [Theory]
    [InlineData("bash")]
    [InlineData("elvish")]
    [InlineData("fish")]
    [InlineData("powershell")]
    [InlineData("zsh")]
    public async Task Completion_prints_script_for_supported_shell(string shell)
    {
        var root = BuildProductRoot();
        var stdout = await InvokeAsync(root, ["completion", shell]);
        Assert.Equal(0, stdout.Exit);
        Assert.False(string.IsNullOrWhiteSpace(stdout.Output));
    }

    [Fact]
    public async Task Completions_alias_prints_script_for_supported_shell()
    {
        var root = BuildProductRoot();
        var stdout = await InvokeAsync(root, ["completions", "zsh"]);
        Assert.Equal(0, stdout.Exit);
        Assert.StartsWith("#compdef hypa", stdout.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_zsh_prefix_matches_snapshot()
    {
        var root = BuildProductRoot();
        var stdout = await InvokeAsync(root, ["completion", "zsh"]);
        Assert.Equal(0, stdout.Exit);
        Assert.StartsWith(ExpectedZshPrefix, stdout.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_unknown_shell_fails_closed()
    {
        var root = BuildProductRoot();
        var result = await InvokeAsync(root, ["completion", "tcsh"]);
        Assert.Equal(2, result.Exit);
        Assert.Contains("unknown shell: tcsh", result.Error, StringComparison.Ordinal);
        Assert.Contains("usage: hypa completion <bash|elvish|fish|powershell|zsh>", result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [SkippableFact]
    public async Task Completion_zsh_script_passes_syntax_check()
    {
        Skip.If(string.IsNullOrEmpty(FindZsh()), "zsh is required");
        var root = BuildProductRoot();
        var stdout = await InvokeAsync(root, ["completion", "zsh"]);
        Assert.Equal(0, stdout.Exit);

        var path = Path.Combine(Path.GetTempPath(), "hypa-completion-" + Guid.NewGuid().ToString("N") + ".zsh");
        try
        {
            await File.WriteAllTextAsync(path, stdout.Output);
            var check = RunZshSyntaxCheck(path);
            Assert.True(check.ExitCode == 0, check.Error);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Completion_zsh_dispatcher_matches_commands_function()
    {
        var root = BuildProductRoot();
        var stdout = await InvokeAsync(root, ["completion", "zsh"]);
        Assert.Equal(0, stdout.Exit);

        var matches = Regex.Matches(stdout.Output, "\":: :(_[A-Za-z0-9_]+)\"");
        Assert.NotEmpty(matches);
        foreach (Match match in matches)
        {
            var functionName = match.Groups[1].Value;
            Assert.Contains(functionName + "() {", stdout.Output, StringComparison.Ordinal);
        }

        Assert.Contains("\":: :_hypa_commands\"", stdout.Output, StringComparison.Ordinal);
        Assert.Contains("_hypa_commands() {", stdout.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\":: :__hypa_commands\"", stdout.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_fish_option_forms_match_long_short_and_slash_names()
    {
        var root = BuildProductRoot();
        var stdout = await InvokeAsync(root, ["completion", "fish"]);
        Assert.Equal(0, stdout.Exit);

        Assert.Contains("complete -c hypa -l 'help'", stdout.Output, StringComparison.Ordinal);
        Assert.Contains("complete -c hypa -s 'h'", stdout.Output, StringComparison.Ordinal);
        Assert.Contains("complete -c hypa -s '?'", stdout.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("complete -c hypa -s 'help'", stdout.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(" -s '/h'", stdout.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(" -s '/?'", stdout.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(" -l '/h'", stdout.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(" -l '/?'", stdout.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(" -a '/h'", stdout.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(" -a '/?'", stdout.Output, StringComparison.Ordinal);
    }

    private static RootCommand BuildProductRoot()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddInfrastructure();
        services.AddCli();
        return services.BuildServiceProvider().GetRequiredService<RootCommand>();
    }

    private static async Task<(int Exit, string Output, string Error)> InvokeAsync(RootCommand root, string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await root.Parse(args).InvokeAsync();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private static string? FindZsh() => File.Exists("/bin/zsh") ? "/bin/zsh" : null;

    private static (int ExitCode, string Error) RunZshSyntaxCheck(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FindZsh()!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        var error = proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("Timed out running zsh -n");
        }

        return (proc.ExitCode, error);
    }
}
