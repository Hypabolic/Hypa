using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachEndpointNegotiationTests
{
    [Fact]
    public void Compatible_hello_matches_welcome_fixture()
    {
        var hello = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointHelloCompatible),
            ProtocolJsonContext.Default.AttachEndpointHello);
        Assert.NotNull(hello);
        Assert.True(AttachEndpointNegotiator.TryNegotiate(hello, out var welcome));
        var expected = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointWelcomeCompatible),
            ProtocolJsonContext.Default.AttachEndpointWelcome);
        Assert.NotNull(expected);
        Assert.Equal(expected.EndpointGeneration, welcome.EndpointGeneration);
        Assert.Equal(expected.ProtocolMajor, welcome.ProtocolMajor);
        Assert.Equal(expected.ProtocolMinor, welcome.ProtocolMinor);
        Assert.Equal(expected.BootId, welcome.BootId);
        Assert.Equal(expected.MuxIdentity, welcome.MuxIdentity);
        Assert.Equal(expected.SnapshotCodec, welcome.SnapshotCodec);
        Assert.Equal(expected.SurfaceCodec, welcome.SurfaceCodec);
        Assert.Equal(expected.InputCodec, welcome.InputCodec);
        Assert.Equal(expected.Methods, welcome.Methods);
        Assert.Equal(expected.Capabilities, welcome.Capabilities);
        Assert.Null(welcome.Error);
        Assert.NotEqual(welcome.EndpointGeneration, welcome.ProtocolMajor + welcome.ProtocolMinor);
    }

    [Fact]
    public void Endpoint_generation_is_independent_of_protocol_major_minor()
    {
        var hello = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointHelloIndependentMinor),
            ProtocolJsonContext.Default.AttachEndpointHello);
        Assert.NotNull(hello);
        Assert.Equal(ProtocolAttachEndpoint.EndpointGeneration, hello.EndpointGeneration);
        Assert.Equal((uint)ProtocolVersion.Major, hello.ProtocolMajor);
        Assert.Equal(99u, hello.ProtocolMinor);
        var welcome = AttachEndpointNegotiator.Negotiate(hello);
        Assert.Null(welcome.Error);
        Assert.Equal(ProtocolAttachEndpoint.EndpointGeneration, welcome.EndpointGeneration);
        Assert.Equal((uint)ProtocolVersion.Major, welcome.ProtocolMajor);
        Assert.Equal((uint)ProtocolVersion.Minor, welcome.ProtocolMinor);
        Assert.NotEqual(99u, welcome.ProtocolMinor);
    }

    [Fact]
    public void Reconnect_generation_cannot_substitute_for_endpoint_generation()
    {
        var hello = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointHelloReconnectGeneration),
            ProtocolJsonContext.Default.AttachEndpointHello);
        Assert.NotNull(hello);
        var identity = new AttachEndpointIdentity
        {
            EndpointId = "local",
            ConnectionGeneration = 7,
            BootId = AttachEndpointNegotiator.FixtureBootId,
        };
        Assert.True(ProtocolAttachEndpoint.ConnectionGenerationSubstitutesEndpoint(
            hello.EndpointGeneration,
            identity.ConnectionGeneration));
        var welcome = AttachEndpointNegotiator.Negotiate(hello, identity);
        Assert.NotNull(welcome.Error);
        Assert.Equal(ProtocolAttachEndpoint.IncompatibleCode, welcome.Error.Code);
        Assert.Contains("connection generation", welcome.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_required_capabilities_fail_closed()
    {
        var hello = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointHelloMissingCapabilities),
            ProtocolJsonContext.Default.AttachEndpointHello);
        Assert.NotNull(hello);
        var welcome = AttachEndpointNegotiator.Negotiate(hello);
        Assert.NotNull(welcome.Error);
        Assert.Equal(ProtocolAttachEndpoint.IncompatibleCode, welcome.Error.Code);
        var fixture = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointMissingCapabilitiesError),
            ProtocolJsonContext.Default.AttachEndpointError);
        Assert.NotNull(fixture);
        Assert.Equal(fixture.Code, welcome.Error.Code);
        Assert.Equal(fixture.Message, welcome.Error.Message);
    }

    [Fact]
    public void Unknown_optional_fields_and_codecs_are_ignored()
    {
        var hello = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointHelloUnknownOptional),
            ProtocolJsonContext.Default.AttachEndpointHello);
        Assert.NotNull(hello);
        Assert.True(AttachEndpointNegotiator.TryNegotiate(hello, out var welcome));
        Assert.Null(welcome.Error);
        Assert.Equal(ProtocolAttachEndpoint.SnapshotCodec, welcome.SnapshotCodec);
    }

    [Fact]
    public void Unknown_required_control_fails_closed()
    {
        var hello = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointHelloUnknownControl),
            ProtocolJsonContext.Default.AttachEndpointHello);
        Assert.NotNull(hello);
        var welcome = AttachEndpointNegotiator.Negotiate(hello);
        Assert.NotNull(welcome.Error);
        Assert.Equal(ProtocolAttachEndpoint.IncompatibleCode, welcome.Error.Code);
    }

    [Fact]
    public void Missing_required_client_id_fails_closed()
    {
        // client_id is a required member, so a hello without it is refused when
        // it is read. It never reaches negotiation.
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointHelloMissingClientId),
            ProtocolJsonContext.Default.AttachEndpointHello));
        Assert.Contains("client_id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_display_enum_maps_to_unknown()
    {
        var sample = JsonSerializer.Deserialize(
            FixtureCatalog.Load(FixtureCatalog.AttachEndpointDisplayUnknown),
            ProtocolJsonContext.Default.AttachDisplayEnumSample);
        Assert.NotNull(sample);
        Assert.Equal(
            ProtocolAttachEndpoint.UnknownDisplay,
            ProtocolAttachEndpoint.DisplayOrUnknown(sample.Display, ProtocolAttachEndpoint.KnownDisplays));
        Assert.Equal(
            ProtocolAttachEndpoint.UnknownDisplay,
            ProtocolAttachEndpoint.DisplayOrUnknown(null));
        Assert.Equal("dark", ProtocolAttachEndpoint.DisplayOrUnknown("dark", ProtocolAttachEndpoint.KnownDisplays));
    }

    [Fact]
    public void Published_names_are_hypa_native()
    {
        Assert.All(ProtocolAttachEndpoint.Methods, name =>
            Assert.StartsWith("hypa.attach.", name, StringComparison.Ordinal));
        Assert.All(ProtocolAttachEndpoint.Codecs, name =>
            Assert.StartsWith("hypa.attach.", name, StringComparison.Ordinal));
        Assert.DoesNotContain(ProtocolAttachEndpoint.Methods, name =>
            name.Contains("shell.", StringComparison.Ordinal));
        Assert.DoesNotContain("agent.attach", ProtocolAttachEndpoint.Methods);
    }
}
