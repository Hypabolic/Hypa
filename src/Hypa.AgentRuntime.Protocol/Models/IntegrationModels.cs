using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>integration.install</c> and <c>integration.uninstall</c> params.</summary>
public sealed record IntegrationTargetParams
{
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary>Install or uninstall each target whose command is on PATH.</summary>
    [JsonPropertyName("detected")]
    public bool Detected { get; init; }

    /// <summary>Return the consent text and write nothing.</summary>
    [JsonPropertyName("plan")]
    public bool Plan { get; init; }
}

/// <summary>One official integration in <c>integration.list</c>.</summary>
public sealed record IntegrationInfoDto
{
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("installed_version")]
    public int? InstalledVersion { get; init; }

    [JsonPropertyName("expected_version")]
    public int ExpectedVersion { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("skill_state")]
    public string? SkillState { get; init; }

    /// <summary>Consent lines for this target. Paths come from the install layout.</summary>
    [JsonPropertyName("consent")]
    public IReadOnlyList<string>? Consent { get; init; }
}

/// <summary><c>integration.list</c> result.</summary>
public sealed record IntegrationListResult
{
    [JsonPropertyName("integrations")]
    public IReadOnlyList<IntegrationInfoDto>? Integrations { get; init; }
}

/// <summary>One target inside a detected install or uninstall result.</summary>
public sealed record IntegrationDetectedOutcome
{
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<string>? Messages { get; init; }
}

/// <summary><c>integration.install</c> and <c>integration.uninstall</c> result.</summary>
public sealed record IntegrationActionResult
{
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<string>? Messages { get; init; }

    /// <summary>Per-target success or failure for a detected install or uninstall.</summary>
    [JsonPropertyName("outcomes")]
    public IReadOnlyList<IntegrationDetectedOutcome>? Outcomes { get; init; }
}
