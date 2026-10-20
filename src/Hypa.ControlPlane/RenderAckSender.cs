using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

/// <summary>
/// The client side of render credit. The render loop notes the frames of each
/// host paint. One background write sends them as one <c>render.ack</c>
/// notification. The slot keeps only the highest generation of each pane, so
/// the acks that come while a write runs go in the next notification. The
/// render loop never waits for the write and never takes a control gate.
/// </summary>
internal sealed class RenderAckSender
{
    private readonly object _gate = new();
    private readonly Func<string, CancellationToken, Task> _writeLine;
    private readonly Dictionary<string, (long Generation, int RowEnd)> _pending = new(StringComparer.Ordinal);
    private bool _running;
    private bool _stopped;
    private long _sent;

    public RenderAckSender(Func<string, CancellationToken, Task> writeLine)
    {
        _writeLine = writeLine ?? throw new ArgumentNullException(nameof(writeLine));
    }

    /// <summary>Notifications that this sender wrote. Diagnostic.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>
    /// Notes the frames of one host paint. A note with no frame sends nothing.
    /// The ack does not name the focused pane: the mux takes the focus from
    /// the input of the connection, in the order that it reads that input.
    /// </summary>
    public void Note(IReadOnlyList<RenderAck> painted)
    {
        ArgumentNullException.ThrowIfNull(painted);
        lock (_gate)
        {
            if (_stopped)
                return;
            foreach (var ack in painted)
                KeepNewestUnlocked(ack.PaneId, ack.Generation, ack.RowEnd);

            if (_pending.Count == 0 || _running)
                return;
            _running = true;
        }

        _ = Task.Run(SendLoopAsync);
    }

    /// <summary>Keeps the newest line of the pane: the highest generation, then the highest end row.</summary>
    private void KeepNewestUnlocked(string paneId, long generation, int rowEnd)
    {
        if (string.IsNullOrEmpty(paneId) || generation <= 0)
            return;
        if (!_pending.TryGetValue(paneId, out var prior)
            || generation > prior.Generation
            || (generation == prior.Generation && rowEnd > prior.RowEnd))
        {
            _pending[paneId] = (generation, rowEnd);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _pending.Clear();
        }
    }

    /// <summary>The NDJSON line of one <c>render.ack</c>, newline not included.</summary>
    internal static string FormatLine(RenderAckParams ack)
    {
        var body = JsonSerializer.Serialize(ack, ProtocolJsonContext.Default.RenderAckParams);
        var line = new StringBuilder(body.Length + 48);
        line.Append("{\"method\":\"").Append(ProtocolMethods.RenderAck).Append("\",\"params\":");
        line.Append(body).Append('}');
        return line.ToString();
    }

    private async Task SendLoopAsync()
    {
        while (true)
        {
            RenderAckParams ack;
            lock (_gate)
            {
                if (_stopped || _pending.Count == 0)
                {
                    _running = false;
                    return;
                }

                var acks = new List<RenderAckEntry>(_pending.Count);
                foreach (var (paneId, (generation, rowEnd)) in _pending)
                {
                    acks.Add(new RenderAckEntry
                    {
                        PaneId = paneId,
                        Generation = generation,
                        RowEnd = rowEnd == RenderRows.All ? null : rowEnd,
                    });
                }
                _pending.Clear();
                ack = new RenderAckParams { Acks = acks };
            }

            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _writeLine(FormatLine(ack), budget.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
            }
            catch (OperationCanceledException)
            {
                // A long write held the connection. Keep these acks for the
                // next note, so a later paint sends them.
                lock (_gate)
                {
                    foreach (var entry in ack.Acks!)
                        KeepNewestUnlocked(entry.PaneId, entry.Generation, entry.RowEnd ?? RenderRows.All);

                    _running = false;
                }

                return;
            }
            catch (Exception ex) when (ex is IOException
                or ObjectDisposedException
                or InvalidOperationException
                or NotSupportedException)
            {
                // The link is gone. The mux gives the credit back after its
                // ack timeout, so a lost ack only waits.
                lock (_gate)
                {
                    _stopped = true;
                    _running = false;
                    _pending.Clear();
                }

                return;
            }
        }
    }
}
