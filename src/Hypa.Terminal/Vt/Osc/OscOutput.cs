namespace Hypa.Terminal.Vt.Osc;

/// <summary>One result of a handler. The pane runtime applies the results in stream order.</summary>
public abstract record OscEffect;

/// <summary>The ordered results of one chunk. The owner clears it before each chunk and reuses it.</summary>
public sealed class OscOutput
{
    private readonly List<OscEffect> _effects = [];

    public IReadOnlyList<OscEffect> Effects => _effects;

    public int Count => _effects.Count;

    public void Add(OscEffect effect) => _effects.Add(effect);

    public void Clear() => _effects.Clear();
}
