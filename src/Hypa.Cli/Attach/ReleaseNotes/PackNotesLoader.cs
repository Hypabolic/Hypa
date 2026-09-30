using System.Reflection;

namespace Hypa.Cli.Attach.ReleaseNotes;

/// <summary>
/// Reads pack notes beside the apphost. Falls back to an embedded dev copy.
/// Content comes from the Hypa pack, not the network.
/// </summary>
public sealed class PackNotesLoader
{
    public const string PackFileName = "hypa.pack-notes.txt";
    public const string EmbeddedResourceName = "Hypa.Cli.Resources.hypa.pack-notes.txt";

    private readonly Func<string?> _readBesideBinary;
    private readonly Func<string?> _readEmbedded;

    public PackNotesLoader(
        Func<string?>? readBesideBinary = null,
        Func<string?>? readEmbedded = null)
    {
        _readBesideBinary = readBesideBinary ?? ReadBesideBinaryDefault;
        _readEmbedded = readEmbedded ?? ReadEmbeddedDefault;
    }

    public PackNotesDocument? TryLoad(string? version = null)
    {
        var body = NormalizeBody(_readBesideBinary() ?? _readEmbedded());
        if (string.IsNullOrEmpty(body))
            return null;

        var resolvedVersion = string.IsNullOrWhiteSpace(version)
            ? ResolveCurrentVersion()
            : version.Trim();
        return new PackNotesDocument(resolvedVersion, body);
    }

    public static string ResolveCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString(3)
               ?? "0.0.0";
    }

    internal static string NormalizeBody(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd();
        return string.Join('\n', lines).Trim();
    }

    private static string? ReadBesideBinaryDefault()
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir))
            return null;
        var path = Path.Combine(Path.GetFullPath(baseDir), PackFileName);
        if (!File.Exists(path))
            return null;
        return File.ReadAllText(path);
    }

    private static string? ReadEmbeddedDefault()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
