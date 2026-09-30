using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.Terminal;
using Hypa.Terminal.Vt;

namespace Hypa.AgentRuntime.Tests.Support;

/// <summary>
/// Builds typed attach snapshots for packer tests. Test code only.
/// Normalizes the structured snapshot first, so the frame carries the
/// same scalars that the compact wire JSON holds.
/// </summary>
internal static class SnapshotPackFixtures
{
    public static VtAttachSnapshot FromStructured(VtStructuredSnapshot snap, string paneId = "p1")
    {
        var normalized = VtSnapshotNormalizer.Normalize(snap);
        var frame = PaneRuntime.ToVtFrame(paneId, normalized, 0, 0, viewportOrigin: 0);
        return new VtAttachSnapshot(
            frame,
            new VtScrollRegion(normalized.ScrollRegion.Top, normalized.ScrollRegion.Bottom),
            normalized.SchemaVersion);
    }

    public static VtAttachSnapshot FromJson(string paneId, string json) =>
        FromStructured(VtSnapshotNormalizer.FromJson(json), paneId);

    public static IReadOnlyList<byte[]> Pack(
        string paneId,
        VtStructuredSnapshot snap,
        IEventPayloadRedactor redactor,
        int maxLineBytes = AttachSnapshotPacker.MaxNdjsonLineBytes,
        long generation = 0,
        IReadOnlyList<int>? dirtyRows = null,
        int occupantGeneration = 0) =>
        AttachSnapshotPacker.Pack(
            paneId, FromStructured(snap, paneId), redactor,
            maxLineBytes, generation, dirtyRows, occupantGeneration);

    public static IReadOnlyList<string> PackAsStrings(
        string paneId,
        VtStructuredSnapshot snap,
        IEventPayloadRedactor redactor,
        int maxLineBytes = AttachSnapshotPacker.MaxNdjsonLineBytes,
        long generation = 0,
        IReadOnlyList<int>? dirtyRows = null,
        int occupantGeneration = 0)
    {
        var parts = Pack(paneId, snap, redactor, maxLineBytes, generation, dirtyRows, occupantGeneration);
        var strings = new string[parts.Count];
        for (var i = 0; i < parts.Count; i++)
            strings[i] = Encoding.UTF8.GetString(parts[i]);
        return strings;
    }

    public static string PayloadString(byte[] payload) => Encoding.UTF8.GetString(payload);
}
