namespace Hypa.Cli.Attach;

/// <summary>
/// Client-local connection generation stamped before transport creation.
/// </summary>
public sealed class AttachEndpointTransportEnvelope
{
    private ulong _generation;
    private bool _invalidated;

    public ulong Generation => _generation;

    public bool IsInvalidated => _invalidated;

    public ulong NextTransportGeneration()
    {
        if (_invalidated)
            throw new InvalidOperationException("connection generation is invalidated");

        return checked(++_generation);
    }

    public void StampServerGeneration(ulong generation) => _generation = generation;

    public void ResetForActivation(ulong generation)
    {
        _invalidated = false;
        _generation = generation;
    }

    public void Invalidate() => _invalidated = true;
}
