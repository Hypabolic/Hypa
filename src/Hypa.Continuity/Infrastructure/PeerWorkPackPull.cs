using System.Diagnostics;
using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Infrastructure;

/// <summary>
/// Same-host peer spool pull (C-21 fixture): copy pack + sidecar between spool dirs.
/// Models “another machine’s spool” without SSH.
/// </summary>
public sealed class LocalPathPeerWorkPackPull : IWorkPackPeerPull
{
    private readonly IWorkPackCodec _codec;

    public LocalPathPeerWorkPackPull(IWorkPackCodec? codec = null)
    {
        _codec = codec ?? new WorkPackCodec();
    }

    public ContinuityOutcome Pull(
        string workId,
        PeerPackSource source,
        IWorkPackSpool destSpool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destSpool);

        if (!string.Equals(source.Kind, "local-path", StringComparison.OrdinalIgnoreCase))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.Internal,
                "LocalPathPeerWorkPackPull requires kind=local-path");
        }

        var remotePack = Path.Combine(source.RemoteSpoolDirectory, workId + ".workpack");
        var remoteSide = remotePack + ".sha256";
        if (!File.Exists(remotePack) || !File.Exists(remoteSide))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "peer workpack or sidecar missing");
        }

        Directory.CreateDirectory(destSpool.SpoolDirectory);
        var destPack = Path.Combine(destSpool.SpoolDirectory, workId + ".workpack");
        File.Copy(remotePack, destPack, overwrite: true);
        File.Copy(remoteSide, destPack + ".sha256", overwrite: true);
        return _codec.VerifySidecar(destPack);
    }
}

/// <summary>
/// SSH peer pull via <c>scp</c> of the workpack + sha256 sidecar only (C-21).
/// </summary>
public sealed class SshPeerWorkPackPull : IWorkPackPeerPull
{
    private readonly IWorkPackCodec _codec;
    private readonly string _scpPath;

    public SshPeerWorkPackPull(IWorkPackCodec? codec = null, string? scpPath = null)
    {
        _codec = codec ?? new WorkPackCodec();
        _scpPath = scpPath ?? "scp";
    }

    public ContinuityOutcome Pull(
        string workId,
        PeerPackSource source,
        IWorkPackSpool destSpool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destSpool);

        if (!string.Equals(source.Kind, "ssh", StringComparison.OrdinalIgnoreCase))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.Internal,
                "SshPeerWorkPackPull requires kind=ssh");
        }

        if (string.IsNullOrWhiteSpace(source.Host))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.Internal,
                "ssh peer host is required");
        }

        Directory.CreateDirectory(destSpool.SpoolDirectory);
        var remotePack = $"{source.RemoteSpoolDirectory.TrimEnd('/')}/{workId}.workpack";
        var remoteSide = remotePack + ".sha256";
        var destPack = Path.Combine(destSpool.SpoolDirectory, workId + ".workpack");
        var destSide = destPack + ".sha256";

        var copyPack = RunScp($"{source.Host}:{remotePack}", destPack);
        if (!copyPack.Ok)
            return copyPack;

        var copySide = RunScp($"{source.Host}:{remoteSide}", destSide);
        if (!copySide.Ok)
            return copySide;

        return _codec.VerifySidecar(destPack);
    }

    private ContinuityOutcome RunScp(string remote, string local)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _scpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ConnectTimeout=10");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(remote);
            psi.ArgumentList.Add(local);

            using var p = Process.Start(psi);
            if (p is null)
                return ContinuityOutcome.Failure(ContinuityReasons.Internal, "failed to start scp");

            // Drain streams concurrently so WaitForExit's budget is real (Bugbot: ReadToEnd first hangs forever).
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { _ = p.WaitForExit(5_000); } catch { /* ignore */ }
                return ContinuityOutcome.Failure(ContinuityReasons.Timeout, "scp timed out");
            }

            var stderr = stderrTask.GetAwaiter().GetResult();
            _ = stdoutTask.GetAwaiter().GetResult();

            if (p.ExitCode != 0)
            {
                // Prefer internal for auth/missing/refused; timeout only for true wait expiry.
                return ContinuityOutcome.Failure(
                    ContinuityReasons.Internal,
                    string.IsNullOrWhiteSpace(stderr) ? $"scp exit {p.ExitCode}" : stderr.Trim());
            }

            return ContinuityOutcome.Success();
        }
        catch (Exception ex)
        {
            return ContinuityOutcome.Failure(ContinuityReasons.Internal, ex.Message);
        }
    }
}
