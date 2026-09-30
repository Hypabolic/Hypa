using System.Text.Json.Serialization;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>Git workspace capture / apply (C-10 / C-11).</summary>
public interface IWorkspacePacker
{
    ContinuityOutcome Capture(string workspacePath, string destWorkspaceDir);

    ContinuityOutcome Apply(string packWorkspaceDir, string destWorkspacePath);

    ContinuityOutcome CheckEquivalence(string packWorkspaceDir, string destWorkspacePath);
}

public sealed record WorkspaceManifestDto
{
    public int Schema { get; set; } = 1;
    public string Head { get; set; } = "";
    public string Branch { get; set; } = "";
    public bool Detached { get; set; }
    public string GitVersion { get; set; } = "";
    public List<string> StatusPorcelainV2 { get; set; } = [];
    public string TrackedDiffSha256 { get; set; } = "";
    public string UntrackedSha256 { get; set; } = "";
    public List<string> UntrackedPaths { get; set; } = [];
    /// <summary>Untracked paths omitted by the secret deny-list. Never silent.</summary>
    public List<string> SkippedPaths { get; set; } = [];
}

[JsonSerializable(typeof(WorkspaceManifestDto))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true)]
public partial class WorkspaceManifestJsonContext : JsonSerializerContext;
