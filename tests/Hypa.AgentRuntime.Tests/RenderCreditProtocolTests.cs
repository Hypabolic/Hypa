using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The wire shape of render credit: an optional capability, an optional hello
/// field that an old peer does without, and the <c>render.ack</c> notification.
/// </summary>
public sealed class RenderCreditProtocolTests
{
    [Fact]
    public void Render_credit_is_optional_known_and_in_the_schema_and_the_welcome_fixture()
    {
        Assert.Contains(ProtocolAttachEndpoint.RenderCreditCapability, ProtocolAttachEndpoint.OptionalCapabilities);
        Assert.Contains(ProtocolAttachEndpoint.RenderCreditCapability, ProtocolAttachEndpoint.KnownCapabilities);
        Assert.DoesNotContain(ProtocolAttachEndpoint.RenderCreditCapability, ProtocolAttachEndpoint.RequiredCapabilities);

        var schema = ProtocolSchemaCatalog.Create();
        Assert.Contains(ProtocolAttachEndpoint.RenderCreditCapability, schema.Attach.OptionalCapabilities);
        Assert.Equal(
            ProtocolSchemaStatus.Implemented,
            Assert.Single(schema.Methods, m => m.Name == ProtocolMethods.RenderAck).Status);

        var welcome = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointWelcomeCompatible),
            ProtocolJsonContext.Default.AttachEndpointWelcome)!;
        Assert.Contains(ProtocolAttachEndpoint.RenderCreditCapability, welcome.Capabilities);
    }

    [Fact]
    public void The_render_ack_fixture_round_trips_with_source_generation()
    {
        using var doc = JsonDocument.Parse(FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(ProtocolMethods.RenderAck)));
        Assert.Equal(ProtocolMethods.RenderAck, doc.RootElement.GetProperty("method").GetString());
        Assert.False(doc.RootElement.TryGetProperty("id", out _));
        var ack = doc.RootElement.GetProperty("params").Deserialize(ProtocolJsonContext.Default.RenderAckParams)!;
        Assert.Equal([("p_1", 42L), ("p_2", 7L)], ack.Acks!.Select(a => (a.PaneId, a.Generation)));
        Assert.Equal([null, 12], ack.Acks!.Select(a => a.RowEnd));
        var again = JsonSerializer.Serialize(ack, ProtocolJsonContext.Default.RenderAckParams);
        Assert.Equal(doc.RootElement.GetProperty("params").GetRawText(), again);
    }

    [Fact]
    public void A_hello_without_optional_capabilities_writes_no_field_and_an_old_hello_reads_as_none()
    {
        var hello = AttachEndpointNegotiator.CompatibleHello();
        var json = JsonSerializer.Serialize(hello, ProtocolJsonContext.Default.AttachEndpointHello);
        Assert.DoesNotContain("optional_capabilities", json, StringComparison.Ordinal);

        var old = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointHelloCompatible),
            ProtocolJsonContext.Default.AttachEndpointHello)!;
        Assert.Null(old.OptionalCapabilities);
        Assert.True(AttachEndpointNegotiator.TryNegotiate(old, out _));

        var offered = hello with { OptionalCapabilities = [ProtocolAttachEndpoint.RenderCreditCapability, "hypa.attach.future.v9"] };
        var read = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(offered, ProtocolJsonContext.Default.AttachEndpointHello),
            ProtocolJsonContext.Default.AttachEndpointHello)!;
        Assert.Equal(offered.OptionalCapabilities, read.OptionalCapabilities);
        Assert.True(AttachEndpointNegotiator.TryNegotiate(read, out _));
    }
}
