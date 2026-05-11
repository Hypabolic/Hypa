using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Runtime.Application.Ports;

public interface IEvidenceLedger
{
    Task RecordToolCallAsync(ToolCallRecord record, CancellationToken ct);
    Task RecordEvidenceAsync(EvidenceRecord record, CancellationToken ct);
    Task RecordArtifactAsync(ArtifactRef artifact, CancellationToken ct);
    Task RecordCommandMetricsAsync(CommandMetricsRecord record, CancellationToken ct);
}
