namespace Hypa.Connectivity.Domain;

/// <summary>One attach attempt. Reconnect mints a new id.</summary>
public readonly record struct AttachAttemptId
{
    public string Value { get; } = "";

    private AttachAttemptId(string value) => Value = value;

    public override string ToString() => Value;

    public static AttachAttemptId New()
    {
        if (!TryParse("att_" + Guid.NewGuid().ToString("N"), out var id))
            throw new InvalidOperationException("attach attempt id mint failed");
        return id;
    }

    public static bool TryParse(string? value, out AttachAttemptId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("att_", StringComparison.Ordinal))
            return false;
        if (trimmed.Length is < 8 or > 128)
            return false;
        if (ConnectivityIdRules.LooksLikePathOrListen(trimmed))
            return false;

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        id = new AttachAttemptId(trimmed);
        return true;
    }
}
