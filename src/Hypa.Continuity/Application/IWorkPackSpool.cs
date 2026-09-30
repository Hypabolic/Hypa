using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>Local WorkPack spool pull (C-13).</summary>
public interface IWorkPackSpool
{
    string SpoolDirectory { get; }

    ContinuityOutcome Publish(string packPath, string sidecarPath);

    ContinuityOutcome Pull(string workId, string destDirectory, out string? packPath);
}
