namespace Hypa.Cli.Attach.Latency;

/// <summary>
/// Gets the input seq of each batch that the sender puts on the wire. The
/// keys of a batch carry the input marks of their stamps. The latency meter
/// calls the observer; the echo predictor uses it to bind its predictions to
/// a seq.
/// </summary>
internal interface IInputSeqObserver
{
    /// <summary>
    /// The sender sent the keys with marks up to <paramref name="maxMark"/>
    /// to <paramref name="paneId"/> under <paramref name="seq"/>. A null seq
    /// means that the peer has no input ack.
    /// </summary>
    void OnSeqAssigned(string paneId, long? seq, long maxMark);

    /// <summary>Open keys were dropped: a retarget, a discard or a failed send. They get no ack.</summary>
    void OnInputDropped();
}
