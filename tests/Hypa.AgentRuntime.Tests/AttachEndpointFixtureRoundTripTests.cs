using System.Text.Json;
using System.Text.RegularExpressions;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachEndpointFixtureRoundTripTests
{
    private static readonly Regex CamelKey = new(
        """["']([a-z]+[A-Z][A-Za-z0-9]*)["']\s*:""",
        RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(MethodNames))]
    public void Attach_method_fixtures_use_snake_case(string method)
    {
        var reqJson = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(method));
        var resJson = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(method));
        Assert.False(CamelKey.IsMatch(reqJson), $"{method} request has camelCase keys");
        Assert.False(CamelKey.IsMatch(resJson), $"{method} response has camelCase keys");
    }

    [Fact]
    public void Attach_hello_fixture_round_trips_welcome_fields()
    {
        var resJson = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(ProtocolMethods.AttachHello));
        var res = JsonSerializer.Deserialize(resJson, ProtocolJsonContext.Default.RpcResponse);
        Assert.NotNull(res);
        var welcome = JsonSerializer.Deserialize(
            res.Result!.Value.GetRawText(),
            ProtocolJsonContext.Default.AttachEndpointWelcome);
        Assert.NotNull(welcome);
        Assert.Equal(AttachEndpointProtocol.EndpointGeneration, welcome.EndpointGeneration);
        Assert.Equal(AttachEndpointProtocol.SnapshotCodec, welcome.SnapshotCodec);
        Assert.Contains(AttachEndpointProtocol.SurfaceInterestCapability, welcome.Capabilities);
    }

    [Theory]
    [MemberData(nameof(EventNames))]
    public void Attach_event_fixtures_use_snake_case(string eventType)
    {
        var json = FixtureCatalog.Load(FixtureCatalog.EventPath(eventType));
        Assert.False(CamelKey.IsMatch(json), $"{eventType} has camelCase keys");
    }

    public static IEnumerable<object[]> MethodNames =>
        FixtureCatalog.AttachEndpointMethods.Select(method => new object[] { method });

    public static IEnumerable<object[]> EventNames =>
        FixtureCatalog.AttachEndpointEventTypes.Select(eventType => new object[] { eventType });
}
