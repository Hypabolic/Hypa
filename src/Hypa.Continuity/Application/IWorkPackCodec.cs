using System.Text.Json.Serialization;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>WorkPack write/read (C-12).</summary>
public interface IWorkPackCodec
{
    ContinuityOutcome Write(
        string spoolDirectory,
        WorkPackManifestDto manifest,
        string harnessDir,
        string workspaceDir,
        out string? packPath);

    ContinuityOutcome Read(
        string packPath,
        string extractRoot,
        out WorkPackManifestDto? manifest);

    ContinuityOutcome VerifySidecar(string packPath);

    string HashPackFile(string packPath);
}

public sealed record WorkPackManifestDto
{
    public int Schema { get; set; } = 1;
    public string WorkId { get; set; } = "";
    public long Generation { get; set; }
    public string CreatedAt { get; set; } = "";
    public WorkPackHarnessDto Harness { get; set; } = new();
    public WorkPackWorkspaceDto Workspace { get; set; } = new();
    public WorkPackSourceDto Source { get; set; } = new();
}

public sealed record WorkPackHarnessDto
{
    public string AdapterId { get; set; } = "";
    public string HarnessVersion { get; set; } = "";
    public string ConversationId { get; set; } = "";
}

public sealed record WorkPackWorkspaceDto
{
    public string Head { get; set; } = "";
    public string Branch { get; set; } = "";
    public string TrackedDiffSha256 { get; set; } = "";
    public string UntrackedSha256 { get; set; } = "";
}

public sealed record WorkPackSourceDto
{
    public string MuxEndpoint { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
}

[JsonSerializable(typeof(WorkPackManifestDto))]
[JsonSerializable(typeof(WorkPackHarnessDto))]
[JsonSerializable(typeof(WorkPackWorkspaceDto))]
[JsonSerializable(typeof(WorkPackSourceDto))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class WorkPackManifestJsonContext : JsonSerializerContext;
