using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class IntegrationFixtureRoundTripTests
{
    [Fact]
    public void List_response_round_trips_consent()
    {
        var json = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(ProtocolMethods.IntegrationList));
        var response = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.RpcResponse);
        Assert.NotNull(response);
        var listed = JsonSerializer.Deserialize(
            response.Result!.Value.GetRawText(),
            ProtocolJsonContext.Default.IntegrationListResult);
        Assert.NotNull(listed?.Integrations);
        var claude = Assert.Single(listed.Integrations);
        Assert.NotNull(claude.Consent);
        Assert.Contains(claude.Consent, line => line.Contains("settings.json", StringComparison.Ordinal));
        Assert.Contains(claude.Consent, line => line.Contains("/tmp/hooks/hypa-agent-state.sh", StringComparison.Ordinal));

        var wire = JsonSerializer.Serialize(claude, ProtocolJsonContext.Default.IntegrationInfoDto);
        var copy = JsonSerializer.Deserialize(wire, ProtocolJsonContext.Default.IntegrationInfoDto);
        Assert.NotNull(copy?.Consent);
        Assert.Equal(claude.Consent, copy.Consent);
    }

    [Fact]
    public void Install_request_round_trips_plan_and_uninstall_rejects_it()
    {
        var json = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(ProtocolMethods.IntegrationInstall));
        var request = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.RpcRequest);
        Assert.NotNull(request);
        Assert.Equal(ProtocolMethods.IntegrationInstall, request.Method);
        Assert.True(
            ProtocolEnvelopeValidator.TryValidateMethodParams(request.Method, request.Params, out var error),
            error);
        var planned = JsonSerializer.Deserialize(
            request.Params!.Value.GetRawText(),
            ProtocolJsonContext.Default.IntegrationTargetParams);
        Assert.NotNull(planned);
        Assert.Equal("claude", planned.Target);
        Assert.True(planned.Plan);

        var wire = JsonSerializer.Serialize(planned, ProtocolJsonContext.Default.IntegrationTargetParams);
        var copy = JsonSerializer.Deserialize(wire, ProtocolJsonContext.Default.IntegrationTargetParams);
        Assert.NotNull(copy);
        Assert.True(copy.Plan);
        Assert.Equal("claude", copy.Target);

        var omitted = JsonSerializer.Deserialize(
            """{"target":"claude"}""",
            ProtocolJsonContext.Default.IntegrationTargetParams);
        Assert.NotNull(omitted);
        Assert.False(omitted.Plan);
        using (var bare = JsonDocument.Parse("""{"target":"claude"}"""))
        {
            Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
                ProtocolMethods.IntegrationInstall,
                bare.RootElement,
                out _));
        }

        var uninstallJson = FixtureCatalog.Load(
            FixtureCatalog.MethodRequestPath(ProtocolMethods.IntegrationUninstall));
        var uninstall = JsonSerializer.Deserialize(uninstallJson, ProtocolJsonContext.Default.RpcRequest);
        Assert.NotNull(uninstall);
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            uninstall.Method,
            uninstall.Params,
            out var uninstallError), uninstallError);
        var uninstallParams = JsonSerializer.Deserialize(
            uninstall.Params!.Value.GetRawText(),
            ProtocolJsonContext.Default.IntegrationTargetParams);
        Assert.NotNull(uninstallParams);
        Assert.False(uninstallParams.Plan);

        using var rejected = JsonDocument.Parse("""{"target":"claude","plan":true}""");
        Assert.False(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.IntegrationUninstall,
            rejected.RootElement,
            out var rejectedError));
        Assert.Equal("plan applies to install", rejectedError);
    }
}
