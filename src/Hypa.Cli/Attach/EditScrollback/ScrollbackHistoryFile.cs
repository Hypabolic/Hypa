using System.Text;
using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.EditScrollback;

/// <summary>
/// Unique 0600 transcript file. Same-host mux only — the server pane reads
/// this client path. Remote attach cannot see a client /tmp file.
/// </summary>
internal sealed class ScrollbackHistoryFile : IScrollbackHistoryFiles
{
    private readonly IAttachConfigEnvironment _env;

    public ScrollbackHistoryFile(IAttachConfigEnvironment? env = null) =>
        _env = env ?? new AgentRuntime.Infrastructure.Config.SystemAttachConfigEnvironment();

    public string WriteUnique(string text)
    {
        var dir = _env.GetVariable("TMPDIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.GetTempPath();
        Directory.CreateDirectory(dir);
        var payload = text ?? "";
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var name = string.Concat(
                "hypa-scrollback-",
                Environment.ProcessId.ToString(),
                "-",
                Environment.TickCount64.ToString(),
                "-",
                attempt.ToString(),
                ".txt");
            var path = Path.Combine(dir, name);
            var created = false;
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                };
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                using (var stream = new FileStream(path, options))
                {
                    created = true;
                    using var writer = new StreamWriter(stream, encoding);
                    writer.Write(payload);
                    writer.Flush();
                }

                return path;
            }
            catch (IOException) when (!created && File.Exists(path))
            {
            }
            catch
            {
                if (created)
                    TryDelete(path);
                throw;
            }
        }

        throw new InvalidOperationException("scrollback temp file create failed");
    }

    public void TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
