using System.Text.Json;
using System.Text.RegularExpressions;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class HostThemeSetFixtureRoundTripTests
{
    private static readonly Regex CamelKey = new(
        """["']([a-z]+[A-Z][A-Za-z0-9]*)["']\s*:""",
        RegexOptions.Compiled);

    [Fact]
    public void Host_theme_set_request_response_round_trip_snake_case()
    {
        var reqJson = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(ProtocolMethods.ClientHostThemeSet));
        Assert.False(CamelKey.IsMatch(reqJson), "client.host_theme.set request has camelCase keys");
        var req = JsonSerializer.Deserialize(reqJson, ProtocolJsonContext.Default.RpcRequest);
        Assert.NotNull(req);
        Assert.Equal(ProtocolMethods.ClientHostThemeSet, req.Method);
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(req.Method, req.Params, out var err), err);
        var parms = JsonSerializer.Deserialize(
            req.Params!.Value.GetRawText(), ProtocolJsonContext.Default.HostThemeSetParams);
        Assert.NotNull(parms);
        Assert.Equal(204, parms.Fg!.R);
        Assert.Equal(18, parms.Bg!.R);
        Assert.Equal("dark", parms.Appearance);

        var resJson = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(ProtocolMethods.ClientHostThemeSet));
        Assert.False(CamelKey.IsMatch(resJson), "client.host_theme.set response has camelCase keys");
        var res = JsonSerializer.Deserialize(resJson, ProtocolJsonContext.Default.RpcResponse);
        Assert.NotNull(res);
        var result = JsonSerializer.Deserialize(
            res.Result!.Value.GetRawText(), ProtocolJsonContext.Default.HostThemeSetResult);
        Assert.NotNull(result);
        Assert.Equal(204, result.Fg!.R);
        Assert.Equal(86, result.Bg!.B);
        Assert.Equal("dark", result.Appearance);
        Assert.Equal(2, parms.Palette!.Length);
        Assert.Equal(0, parms.Palette[0].I);
        Assert.Equal(255, parms.Palette[1].I);
    }

    [Fact]
    public void Request_and_response_round_trip_palette()
    {
        var reqJson = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(ProtocolMethods.ClientHostThemeSet));
        var req = JsonSerializer.Deserialize(reqJson, ProtocolJsonContext.Default.RpcRequest);
        var parms = JsonSerializer.Deserialize(
            req!.Params!.Value.GetRawText(), ProtocolJsonContext.Default.HostThemeSetParams);
        Assert.NotNull(parms!.Palette);
        Assert.Equal(0, parms.Palette[0].I);
        Assert.Equal(1, parms.Palette[0].R);
        Assert.Equal(255, parms.Palette[1].I);
        Assert.Equal(254, parms.Palette[1].B);

        var resJson = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(ProtocolMethods.ClientHostThemeSet));
        var res = JsonSerializer.Deserialize(resJson, ProtocolJsonContext.Default.RpcResponse);
        var result = JsonSerializer.Deserialize(
            res!.Result!.Value.GetRawText(), ProtocolJsonContext.Default.HostThemeSetResult);
        Assert.NotNull(result!.Palette);
        Assert.Equal(0, result.Palette[0].I);
        Assert.Equal(3, result.Palette[0].B);
        Assert.Equal(255, result.Palette[1].I);
        Assert.Equal(252, result.Palette[1].R);
    }
}
