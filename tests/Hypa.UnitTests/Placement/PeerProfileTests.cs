using System.Text.Json;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Xunit;

namespace Hypa.UnitTests.Placement;

public sealed class PeerProfileTests
{
    [Fact]
    public void TryCreate_accepts_shared_contract_fields()
    {
        Assert.True(PeerProfile.TryCreate(
            "abc123",
            "Build host",
            "user@host",
            "agents",
            enabled: true,
            PeerProviders.Ssh,
            out var profile,
            out var error));
        Assert.Null(error);
        Assert.Equal("abc123", profile.Id);
        Assert.Equal("Build host", profile.Label);
        Assert.Equal("user@host", profile.Target);
        Assert.Equal("agents", profile.Session);
        Assert.True(profile.Enabled);
        Assert.Equal(PeerProviders.Ssh, profile.Provider);
    }

    [Fact]
    public void TryCreate_rejects_password_and_leading_dash()
    {
        Assert.False(PeerProfile.TryCreate(
            "id1", "lab", "user:secret@host", "default", true, PeerProviders.Ssh, out _, out var password));
        Assert.Equal("SSH target must not contain a password", password);

        Assert.False(PeerProfile.TryCreate(
            "id1", "lab", "-oProxyCommand=x", "default", true, PeerProviders.Ssh, out _, out var dash));
        Assert.Equal("--remote target must not start with '-'", dash);
    }

    [Fact]
    public void TryCreate_rejects_control_and_empty_label()
    {
        Assert.False(PeerProfile.TryCreate(
            "id1", "bad\nlabel", "host", "default", true, PeerProviders.Ssh, out _, out _));
        Assert.False(PeerProfile.TryCreate(
            "id1", "   ", "host", "default", true, PeerProviders.Ssh, out _, out _));
    }

    [Fact]
    public void Session_grammar_matches_ascii_token()
    {
        Assert.True(PeerProfile.TryValidateSession("default", out var ok, out _));
        Assert.Equal("default", ok);
        Assert.False(PeerProfile.TryValidateSession("has space", out _, out _));
        Assert.False(PeerProfile.TryValidateSession("", out _, out _));
        Assert.False(PeerProfile.TryValidateSession(new string('a', 65), out _, out _));
    }

    [Fact]
    public void Provider_rejects_unknown_discriminator()
    {
        Assert.True(PeerProviders.IsKnown(PeerProviders.Ssh));
        Assert.True(PeerProviders.IsKnown(PeerProviders.Quic));
        Assert.False(PeerProviders.IsKnown("ws"));
        Assert.True(PeerProfile.TryCreate(
            "id1", "lab", "192.168.1.10", "default", true, PeerProviders.Quic, out var quic, out _));
        Assert.Equal(PeerProviders.Quic, quic.Provider);
        Assert.False(PeerProfile.TryCreate(
            "id1", "   ", "192.168.1.10", "default", true, PeerProviders.Quic, out _, out var quicLabel));
        Assert.Equal("peer placement label cannot be empty", quicLabel);
        Assert.False(PeerProfile.TryCreate(
            "id1", "lab", "host", "default", true, "ws", out _, out var error));
        Assert.Equal("peer provider must be ssh or quic", error);
    }

    [Fact]
    public void Json_round_trip_keeps_contract_fields_without_secrets()
    {
        var dto = new PeerProfileDto
        {
            Id = "deadbeef",
            Label = "lab",
            Target = "user@host",
            Session = "agents",
            Enabled = false,
            Provider = PeerProviders.Ssh,
        };
        var json = JsonSerializer.Serialize(dto, PlacementJsonContext.Default.PeerProfileDto);
        Assert.Contains("\"provider\":\"ssh\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN", json, StringComparison.Ordinal);
        var copy = JsonSerializer.Deserialize(json, PlacementJsonContext.Default.PeerProfileDto);
        Assert.NotNull(copy);
        Assert.Equal(dto, copy);
    }
}
