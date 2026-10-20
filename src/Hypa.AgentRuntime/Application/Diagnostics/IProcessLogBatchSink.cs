namespace Hypa.AgentRuntime.Application;

/// <summary>
/// A process-log sink that can write a list of records with one flush.
/// A queued writer uses it to write each batch it takes from its queue.
/// </summary>
public interface IProcessLogBatchSink : IProcessLogSink
{
    /// <summary>
    /// Writes the records in their order, then flushes once. Returns the
    /// number of records that the sink could not serialize and lost. The
    /// other records of the batch are still written.
    /// </summary>
    int WriteBatch(IReadOnlyList<ProcessLogRecord> records);
}
