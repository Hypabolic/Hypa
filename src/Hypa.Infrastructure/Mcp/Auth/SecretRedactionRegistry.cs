namespace Hypa.Infrastructure.Mcp.Auth;

public sealed class SecretRedactionRegistry
{
    private readonly HashSet<string> _secrets = new();
    private readonly object _lock = new();

    public void Register(string secret)
    {
        if (string.IsNullOrEmpty(secret))
            return;

        lock (_lock)
            _secrets.Add(secret);
    }

    public string Redact(string text)
    {
        lock (_lock)
        {
            foreach (var secret in _secrets)
                text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        return text;
    }
}
