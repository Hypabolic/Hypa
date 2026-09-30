using System.Reflection;
using System.Text;

namespace Hypa.AgentRuntime.Application;

/// <summary>Commented default attach TOML. Same text as <c>Resources/default-config.toml</c>.</summary>
public static class AttachConfigDefaults
{
    public const string ResourceName = "Hypa.AgentRuntime.Resources.default-config.toml";

    private static readonly Lazy<string> Cached = new(LoadEmbedded);

    public static string Toml => Cached.Value;

    private static string LoadEmbedded()
    {
        var assembly = typeof(AttachConfigDefaults).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
