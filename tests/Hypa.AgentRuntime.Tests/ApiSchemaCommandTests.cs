using System.CommandLine;
using Hypa.Cli.Commands;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class ApiSchemaCommandTests
{
    [Fact]
    public async Task Schema_prints_human_summary()
    {
        var (exit, stdout, stderr) = await InvokeAsync(new ApiCommand().Build(), ["schema"]);
        Assert.Equal(0, exit);
        Assert.Contains("Hypa API schema", stdout, StringComparison.Ordinal);
        Assert.Contains("endpoint_generation: 1", stdout, StringComparison.Ordinal);
        Assert.Contains("hypa api schema --json", stdout, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(stderr));
    }

    [Fact]
    public async Task Schema_json_prints_methods_and_implemented_resources()
    {
        var (exit, stdout, _) = await InvokeAsync(new ApiCommand().Build(), ["schema", "--json"]);
        Assert.Equal(0, exit);
        Assert.Contains("\"protocol_name\":\"hypa-runtime\"", stdout, StringComparison.Ordinal);
        Assert.Contains("plugin.resource.list", stdout, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"implemented\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\":\"reserved\"", stdout, StringComparison.Ordinal);
        Assert.Contains("pane.scroll_changed", stdout, StringComparison.Ordinal);
        Assert.Contains("layout.updated", stdout, StringComparison.Ordinal);
        Assert.Contains("recent_unwrapped", stdout, StringComparison.Ordinal);
        Assert.Contains("hypa.attach.hello.v1", stdout, StringComparison.Ordinal);
        Assert.Contains("\"event\":\"terminal.render\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"cells\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"snapshot\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"blit\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"cursor\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("agent.attach", stdout, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> InvokeAsync(
        Command command,
        string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        int exit;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            exit = await command.Parse(args).InvokeAsync();
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }

        return (exit, stdout.ToString(), stderr.ToString());
    }
}
