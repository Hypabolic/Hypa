namespace Hypa.Connectivity.Domain;

/// <summary>
/// Account or local operator identity. Distinct from device, Placement, and capability.
/// Self-hosted v0 uses local-operator identity. No hosted account is required.
/// </summary>
public readonly record struct OperatorIdentity
{
    public const string LocalSelfHostedValue = "opr_local";

    public string Value { get; } = "";

    private OperatorIdentity(string value) => Value = value;

    public static OperatorIdentity LocalSelfHosted { get; } = new(LocalSelfHostedValue);

    public bool IsLocalSelfHosted =>
        string.Equals(Value, LocalSelfHostedValue, StringComparison.Ordinal);

    public override string ToString() => Value;

    public static bool TryParse(string? value, out OperatorIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("opr_", StringComparison.Ordinal))
            return false;
        if (trimmed.Length is < 8 or > 128)
            return false;
        if (ConnectivityIdRules.LooksLikePathOrListen(trimmed))
            return false;
        if (trimmed.StartsWith("dev_", StringComparison.Ordinal))
            return false;
        if (trimmed.StartsWith("plc_", StringComparison.Ordinal))
            return false;

        identity = new OperatorIdentity(trimmed);
        return true;
    }
}
