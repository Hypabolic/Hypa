using System.Collections.Immutable;

namespace Hypa.AgentRuntime.Domain.ProgramStatus;

/// <summary>
/// Immutable set of program status records of one pane.
/// Each method returns a new store. A call that changes nothing returns the same store.
/// Records have a nullable id. The root record has a null id.
/// <c>build/test</c> is a child of <c>build</c>. A parent does not have to exist.
/// </summary>
public sealed class ProgramStatusStore
{
    public static ProgramStatusStore Empty { get; } = new([], 0, false);

    private ProgramStatusStore(ImmutableArray<ProgramStatusRecord> records, long lastSeq, bool realReportSeen)
    {
        Records = records;
        LastSeq = lastSeq;
        RealReportSeen = realReportSeen;
    }

    /// <summary>
    /// Build a store from saved data. The saved data is not trusted, so this method checks it.
    /// It drops a record with an invalid id, the state <c>clear</c>, an invalid <c>app</c>,
    /// or a <c>msg</c> or <c>title</c> over its limit. It keeps the last record of a repeated id.
    /// It keeps at most <see cref="MaxRecords"/> records: the most recently updated ones.
    /// <see cref="LastSeq"/> is never lower than the highest record sequence.
    /// </summary>
    public static ProgramStatusStore Restore(
        IEnumerable<ProgramStatusRecord>? records,
        long lastSeq,
        bool realReportSeen)
    {
        var byId = new Dictionary<string, ProgramStatusRecord>(StringComparer.Ordinal);
        ProgramStatusRecord? root = null;
        foreach (var record in records ?? [])
        {
            if (!IsRestorable(record))
                continue;
            if (record.Id is null)
                root = record;
            else
                byId[record.Id] = record;
        }

        var kept = new List<ProgramStatusRecord>(byId.Count + 1);
        if (root is not null)
            kept.Add(root);
        kept.AddRange(byId.Values);
        if (kept.Count > MaxRecords)
        {
            kept = kept.OrderByDescending(r => r.UpdatedSeq).Take(MaxRecords).ToList();
        }

        if (kept.Count == 0 && !realReportSeen && lastSeq <= 0)
            return Empty;

        var highest = kept.Count == 0 ? 0 : kept.Max(r => r.UpdatedSeq);
        return new ProgramStatusStore([.. kept], Math.Max(Math.Max(lastSeq, highest), 0), realReportSeen);
    }

    private static bool IsRestorable(ProgramStatusRecord record) =>
        record.State is not ProgramStatusState.Clear
        && Enum.IsDefined(record.State)
        && record.UpdatedSeq >= 0
        && (record.Id is null || ProgramStatusParser.IsValidId(record.Id))
        && (record.App is null || ProgramStatusParser.IsValidApp(record.App))
        && (record.Msg is null || System.Text.Encoding.UTF8.GetByteCount(record.Msg) <= ProgramStatusParser.MaxMsgDecodedBytes)
        && (record.Title is null || System.Text.Encoding.UTF8.GetByteCount(record.Title) <= ProgramStatusParser.MaxTitleDecodedBytes)
        && (record.Kind is null || Enum.IsDefined(record.Kind.Value))
        && (record.Progress is null or (>= 0 and <= 100));

    public ImmutableArray<ProgramStatusRecord> Records { get; }

    /// <summary>Sequence number given to the latest accepted change.</summary>
    public long LastSeq { get; }

    public bool IsEmpty => Records.IsEmpty;

    /// <summary>
    /// True after the first real OSC 7501 report. From then on the store ignores mapped progress
    /// until a full reset. The spec asks for this, so a mapped report cannot wipe the kind and
    /// message of a real report.
    /// </summary>
    public bool RealReportSeen { get; }

    /// <summary>Most records one pane keeps.</summary>
    public const int MaxRecords = 256;

    /// <summary>
    /// Apply one validated report. <c>clear</c> with an id removes that record and every record below it.
    /// <c>clear</c> without an id removes every record. Any other state replaces the record of its id.
    /// A new record past <see cref="MaxRecords"/> removes the least recently updated record first.
    /// A report that equals the stored record has no effect.
    /// A real report also marks the store: <see cref="RealReportSeen"/> becomes true.
    /// A mapped progress report (<see cref="ProgramStatusReportSource.ConEmuProgress"/>) has no effect
    /// when <see cref="RealReportSeen"/> is true. Its <c>clear</c> removes the root record only.
    /// </summary>
    public ProgramStatusStore Apply(
        ProgramStatusReport report,
        ProgramStatusReportSource source = ProgramStatusReportSource.Osc7501)
    {
        if (source is ProgramStatusReportSource.ConEmuProgress)
        {
            if (RealReportSeen)
                return this;
            if (report.State is ProgramStatusState.Clear)
            {
                var others = Records.Where(r => r.Id is not null).ToImmutableArray();
                return others.Length == Records.Length ? this : new ProgramStatusStore(others, LastSeq, RealReportSeen);
            }

            return ApplyUnmarked(report);
        }

        var next = ApplyUnmarked(report);
        return next.RealReportSeen ? next : new ProgramStatusStore(next.Records, next.LastSeq, true);
    }

    private ProgramStatusStore ApplyUnmarked(ProgramStatusReport report)
    {
        if (report.State is ProgramStatusState.Clear)
        {
            if (report.Id is null)
                return IsEmpty ? this : new ProgramStatusStore([], LastSeq, RealReportSeen);
            var prefix = report.Id + "/";
            var kept = Records
                .Where(r => r.Id is null
                    || !(string.Equals(r.Id, report.Id, StringComparison.Ordinal)
                        || r.Id.StartsWith(prefix, StringComparison.Ordinal)))
                .ToImmutableArray();
            return kept.Length == Records.Length ? this : new ProgramStatusStore(kept, LastSeq, RealReportSeen);
        }

        var seq = LastSeq + 1;
        var record = new ProgramStatusRecord
        {
            Id = report.Id,
            State = report.State,
            Kind = report.Kind,
            Progress = report.Progress,
            App = report.App,
            Title = report.Title,
            Msg = report.Msg,
            UpdatedSeq = seq,
        };

        var index = IndexOf(report.Id);
        if (index >= 0)
        {
            return Records[index].SameContent(record)
                ? this
                : new ProgramStatusStore(Records.SetItem(index, record), seq, RealReportSeen);
        }

        var records = Records;
        if (records.Length >= MaxRecords)
        {
            var oldest = 0;
            for (var i = 1; i < records.Length; i++)
            {
                if (records[i].UpdatedSeq < records[oldest].UpdatedSeq)
                    oldest = i;
            }

            records = records.RemoveAt(oldest);
        }

        return new ProgramStatusStore(records.Add(record), seq, RealReportSeen);
    }

    /// <summary>
    /// Records sorted root first, then by id segment by segment.
    /// A record without <c>app</c> takes the <c>app</c> of its nearest ancestor that has one.
    /// The store keeps records as reported. This method resolves <c>app</c> when read.
    /// </summary>
    public IReadOnlyList<ProgramStatusRecord> Resolved()
    {
        var resolved = new List<ProgramStatusRecord>(Records.Length);
        foreach (var record in Records)
        {
            resolved.Add(record.App is null && record.Id is not null
                ? record with { App = InheritedApp(record.Id) }
                : record);
        }

        resolved.Sort(static (a, b) => CompareIds(a.Id, b.Id));
        return resolved;
    }

    private string? InheritedApp(string id)
    {
        var segments = id.Split('/');
        for (var depth = segments.Length - 1; depth >= 1; depth--)
        {
            var parentId = string.Join('/', segments, 0, depth);
            var parent = Records.FirstOrDefault(r => string.Equals(r.Id, parentId, StringComparison.Ordinal));
            if (parent?.App is { } app)
                return app;
        }

        return Records.FirstOrDefault(r => r.Id is null)?.App;
    }

    private static int CompareIds(string? a, string? b)
    {
        if (a is null || b is null)
            return a is null ? (b is null ? 0 : -1) : 1;
        var x = a.Split('/');
        var y = b.Split('/');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var c = string.CompareOrdinal(x[i], y[i]);
            if (c != 0)
                return c;
        }

        return x.Length.CompareTo(y.Length);
    }

    /// <summary>
    /// Apply a lifecycle event. Transition table:
    /// <list type="table">
    /// <item><term>ProcessExit</term><description>Drop working and blocked records. Keep idle, done, and error.</description></item>
    /// <item><term>PromptStart</term><description>Drop working and blocked records. Keep idle, done, and error.</description></item>
    /// <item><term>FullReset</term><description>Drop every record. Mapped progress counts again (<see cref="RealReportSeen"/> is false).</description></item>
    /// </list>
    /// A soft reset (DECSTR) is not an event. It does not change records.
    /// An event that drops nothing returns the same store.
    /// </summary>
    public ProgramStatusStore On(ProgramStatusLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent is ProgramStatusLifecycleEvent.FullReset)
            return IsEmpty && !RealReportSeen ? this : new ProgramStatusStore([], LastSeq, false);

        var kept = Records
            .Where(r => r.State is not (ProgramStatusState.Working or ProgramStatusState.Blocked))
            .ToImmutableArray();
        return kept.Length == Records.Length ? this : new ProgramStatusStore(kept, LastSeq, RealReportSeen);
    }

    private int IndexOf(string? id)
    {
        for (var i = 0; i < Records.Length; i++)
        {
            if (string.Equals(Records[i].Id, id, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}
