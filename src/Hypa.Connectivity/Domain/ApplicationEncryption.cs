namespace Hypa.Connectivity.Domain;

/// <summary>
/// Application-layer encryption. TLS to the relay is not this gate.
/// </summary>
public static class ApplicationEncryption
{
    public static bool IsRequired => true;

    public static bool Protects(StreamFrameKind kind) =>
        kind is StreamFrameKind.Control or StreamFrameKind.Binary or StreamFrameKind.Resize;

    public static int CiphertextLength(int plaintextLength) =>
        plaintextLength + StreamLimits.ApplicationAeadTagBytes;

    public static int MaxPlaintextBytes(StreamFrameKind kind, StreamBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var max = budget.MaxPayloadBytes(kind);
        if (!Protects(kind))
            return max;

        return Math.Max(0, max - StreamLimits.ApplicationAeadTagBytes);
    }
}

/// <summary>
/// Relay storage rule. v0 streams the join. It does not keep a WorkPack.
/// </summary>
public static class RelayDurability
{
    public static bool StoresWorkPack => false;

    public static bool StreamsJoinOnly => true;
}

/// <summary>Uncompressed P-256 join ephemeral public key on the bootstrap.</summary>
public static class JoinEphPublicKeys
{
    public const int SizeBytes = 65;
    public const byte UncompressedPrefix = 0x04;

    public static bool TryDecode(string? value, out byte[] raw)
    {
        raw = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            raw = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return raw.Length == SizeBytes && raw[0] == UncompressedPrefix;
    }

    public static string Encode(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != SizeBytes || raw[0] != UncompressedPrefix)
            throw new ArgumentException("ephemeral public key must be an uncompressed P-256 point", nameof(raw));

        return Convert.ToBase64String(raw);
    }

    public static ConnectivityOutcome Require(string? value)
    {
        if (!TryDecode(value, out _))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "peer ephemeral public key is required");
        }

        return ConnectivityOutcome.Success();
    }
}

/// <summary>HMAC-SHA256 of the uncompressed ephemeral public point. Not the join secret.</summary>
public static class JoinEphPublicMacs
{
    public const int SizeBytes = 32;

    public static bool TryDecode(string? value, out byte[] raw)
    {
        raw = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            raw = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return raw.Length == SizeBytes;
    }
}

/// <summary>
/// Shared join secret. Endpoints keep it. The relay wire JSON must not carry it.
/// </summary>
public readonly record struct JoinSecret
{
    public const int SizeBytes = 32;

    public byte[] Value { get; } = [];

    private JoinSecret(byte[] value) => Value = value;

    public bool IsEmpty => Value is null || Value.Length == 0;

    public static JoinSecret Empty { get; } = new([]);

    public static JoinSecret Create() => new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(SizeBytes));

    public static bool TryParse(string? value, out JoinSecret secret)
    {
        secret = Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (raw.Length != SizeBytes)
            return false;

        secret = new JoinSecret(raw);
        return true;
    }

    public string Encode()
    {
        if (IsEmpty)
            throw new InvalidOperationException("join secret is empty");

        return Convert.ToBase64String(Value);
    }
}
