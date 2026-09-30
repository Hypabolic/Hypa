namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Agent harness recipe for pane replace. V0 certifies Pi continuity;
/// the manifest is spawn plumbing, not a BYO product claim.
/// </summary>
public sealed record OccupantManifest
{
    public required string Id { get; init; }

    /// <summary>Full argv. Index 0 is the executable.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>Working directory template. Default <c>{workspace}</c>.</summary>
    public string Cwd { get; init; } = "{workspace}";

    /// <summary>Environment overlay templates (for example HOME = {cube_home}).</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Native transcript source id for pane spawn (for example <c>pi</c>).</summary>
    public required string TranscriptSource { get; init; }

    /// <summary>Native transcript root template under cube HOME.</summary>
    public required string TranscriptRoot { get; init; }
}
