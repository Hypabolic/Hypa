using System.Diagnostics;
using System.Text;

namespace Hypa.Placement.Infrastructure;

/// <summary>Spawns the host <c>ssh</c> binary. No keys are stored.</summary>
public sealed class ProcessOpenSshProcess : IOpenSshProcess
{
    public async Task<OpenSshProcessResult> RunAsync(
        OpenSshProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in OpenSshArgumentBuilder.Build(request))
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        try
        {
            if (request.StdinText is { } stdin)
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new OpenSshProcessResult
            {
                ExitCode = process.ExitCode,
                Stdout = await stdoutTask.ConfigureAwait(false),
                Stderr = await stderrTask.ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }
        finally
        {
            if (!process.HasExited)
                TryKillProcessTree(process);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}

public static class OpenSshArgumentBuilder
{
    public static IReadOnlyList<string> Build(OpenSshProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var args = new List<string>();
        if (!string.IsNullOrEmpty(request.ConfigPath))
        {
            args.Add("-F");
            args.Add(request.ConfigPath);
        }

        if (!string.IsNullOrEmpty(request.ControlPath))
        {
            args.Add("-o");
            args.Add("ControlPath=" + request.ControlPath);
            args.Add("-o");
            args.Add("ControlMaster=auto");
            args.Add("-o");
            args.Add("ControlPersist=60");
        }

        if (request.BatchMode)
        {
            args.Add("-o");
            args.Add("BatchMode=yes");
            args.Add("-o");
            args.Add("NumberOfPasswordPrompts=0");
        }

        args.Add("-T");
        args.Add(request.Target);
        args.Add(request.RemoteCommand);
        return args;
    }

    public static string Quote(string value)
    {
        var builder = new StringBuilder("'");
        foreach (var c in value)
        {
            if (c == '\'')
                builder.Append("'\\''");
            else
                builder.Append(c);
        }

        builder.Append('\'');
        return builder.ToString();
    }
}
