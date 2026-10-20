namespace Hypa.Cli.Attach.Prediction;

/// <summary>
/// How a prediction confirms. A glyph prediction changes the glyph of a
/// cell. A cursor prediction changes no glyph: a space or an erase over a
/// blank cell, or a horizontal arrow.
/// </summary>
internal enum EchoPredictionKind
{
    Glyph,
    Cursor,
}

/// <summary>
/// One predicted key. It keeps positions and the expected glyph only, never
/// a reference to the cells of a frame: the cells unpacker writes into the
/// storage of the previous frame.
/// </summary>
internal sealed class EchoPrediction
{
    public required long Order { get; init; }

    /// <summary>The input mark of the key. The sender binds a seq to it.</summary>
    public required long Mark { get; init; }

    public long? Seq { get; set; }

    public required EchoPredictionKind Kind { get; init; }

    public required int Col { get; init; }

    public required int Row { get; init; }

    /// <summary>The cell that the client expects. A glyph prediction stamps it.</summary>
    public required AssembledCell Expected { get; init; }

    /// <summary>For a cursor prediction: the frame cell must be blank or a space.</summary>
    public bool CellMustBeBlank { get; init; }

    public required PredictedCursor CursorAfter { get; init; }

    /// <summary>When the key was observed, on the predictor clock.</summary>
    public required long CreatedAt { get; init; }

    /// <summary>When the client first knew that <c>input_ack</c> covers <see cref="Seq"/>.</summary>
    public long? AckSeenAt { get; set; }

    /// <summary>True after a host paint showed this prediction.</summary>
    public bool Shown { get; set; }
}

/// <summary>
/// A prediction epoch, as in mosh. A new epoch is tentative: the client
/// shows none of its predictions until one of them confirms. A mismatch
/// deletes the whole epoch.
/// </summary>
internal sealed class PredictionEpoch
{
    public PredictionEpoch(long id) => Id = id;

    public long Id { get; }

    /// <summary>True after one prediction of the epoch confirmed.</summary>
    public bool Confirmed { get; set; }

    /// <summary>The live predictions, in key order.</summary>
    public List<EchoPrediction> Predictions { get; } = [];
}
