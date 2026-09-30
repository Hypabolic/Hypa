using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Infrastructure;

/// <summary>Local WorkPack spool pull (C-13).</summary>
public sealed class LocalWorkPackSpool : IWorkPackSpool
{
    private readonly IWorkPackCodec _codec;

    public LocalWorkPackSpool(string? spoolDirectory = null, IWorkPackCodec? codec = null)
    {
        _codec = codec ?? new WorkPackCodec();
        SpoolDirectory = spoolDirectory ?? DefaultSpoolDirectory();
        Directory.CreateDirectory(SpoolDirectory);
    }

    public string SpoolDirectory { get; }

    public static string DefaultSpoolDirectory()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return Path.Combine(xdg, "hypa", "continuity", "spool");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "hypa", "continuity", "spool");
    }

    public ContinuityOutcome Publish(string packPath, string sidecarPath)
    {
        if (!File.Exists(packPath))
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "pack missing");
        if (!File.Exists(sidecarPath))
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "sidecar missing");

        Directory.CreateDirectory(SpoolDirectory);
        var name = Path.GetFileName(packPath);
        var dest = Path.Combine(SpoolDirectory, name);
        var destSide = dest + ".sha256";
        File.Copy(packPath, dest, overwrite: true);
        File.Copy(sidecarPath, destSide, overwrite: true);
        return _codec.VerifySidecar(dest);
    }

    public ContinuityOutcome Pull(string workId, string destDirectory, out string? packPath)
    {
        packPath = null;
        if (!WorkId.TryParse(workId, out var parsed))
            return ContinuityOutcome.Failure(ContinuityReasons.Internal, "work id is invalid");

        var src = Path.Combine(SpoolDirectory, parsed.Value + ".workpack");
        if (!File.Exists(src))
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "workpack not in spool");

        var verify = _codec.VerifySidecar(src);
        if (!verify.Ok)
            return verify;

        Directory.CreateDirectory(destDirectory);
        var dest = Path.Combine(destDirectory, parsed.Value + ".workpack");
        File.Copy(src, dest, overwrite: true);
        File.Copy(src + ".sha256", dest + ".sha256", overwrite: true);
        packPath = dest;
        return ContinuityOutcome.Success();
    }
}
