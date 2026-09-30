namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Client-owned prefix ASCII switch.
/// <c>src/client/shell/input_source.rs:3-40</c> <c>wants_ascii_input</c> /
/// <c>reconcile_input_source</c>.
/// </summary>
public sealed class PrefixAsciiInputCoordinator
{
    private readonly List<bool> _pending = [];

    public bool Enabled { get; set; }

    /// <summary>
    /// Host TTY focus. <c>false</c> keeps the restore token until focus returns.
    /// </summary>
    public bool? OuterFocused { get; set; }

    public bool AsciiActive { get; private set; }

    public void Reconcile(bool wantsAscii)
    {
        if (OuterFocused == false)
            return;
        var desired = Enabled && wantsAscii;
        if (desired == AsciiActive)
            return;
        AsciiActive = desired;
        _pending.Add(desired);
    }

    public IReadOnlyList<bool> TakePending()
    {
        if (_pending.Count == 0)
            return [];
        var taken = _pending.ToArray();
        _pending.Clear();
        return taken;
    }

    public void Apply(IPrefixAsciiInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var active in TakePending())
        {
            if (active)
                source.SwitchToAscii();
            else
                source.Restore();
        }
    }

    public void ReconcileAndApply(bool wantsAscii, IPrefixAsciiInputSource source)
    {
        Reconcile(wantsAscii);
        Apply(source);
    }
}
