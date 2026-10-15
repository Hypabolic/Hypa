using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.Terminal.Vt.Osc;

namespace Hypa.Terminal.Vt;

/// <summary>
/// Retains OSC 0/2 title, OSC 7 working directory, and OSC 9 progress for agent detection.
/// It is an OSC handler. The tokenizer reads the bytes and gives it each complete string.
/// Passive capture only. Does not affect render.
/// </summary>
public sealed class AgentOscStateTracker : IOscHandler
{
    public const int MaxBodyBytes = 4096;
    public const int MaxChars = 256;

    // Control characters and ESC pairs stay in the body. ESC BEL ends the string.
    public static OscStringPolicy Policy { get; } =
        new(MaxBodyBytes, OscEscapeInBody.KeepAndEndOnBel, CancelAborts: false);

    private string? _latestTitle;
    private string? _terminalTitle;
    private string? _latestProgress;

    public string LatestTitle => _latestTitle ?? "";

    public string LatestProgress => _latestProgress ?? "";

    public string? TerminalTitle => _terminalTitle;

    public string? WorkingDirectory { get; private set; }

    /// <summary>Register this handler for OSC 0, 2, 7, and 9.</summary>
    public void RegisterWith(OscHandlerRegistry.Builder registry)
    {
        registry.Add(0, Policy, this).Add(2, Policy, this).Add(7, Policy, this).Add(9, Policy, this);
    }

    public void Handle(in OscSequence sequence, OscOutput output)
    {
        ApplyBody(sequence.Body);
    }

    public void ClearRetained()
    {
        _latestTitle = null;
        _latestProgress = null;
    }

    private void ApplyBody(ReadOnlySpan<byte> body)
    {
        var sep = body.IndexOf((byte)';');
        if (sep < 0)
            return;
        var command = body[..sep];
        var payload = body[(sep + 1)..];
        if (command.Length != 1)
            return;

        switch ((char)command[0])
        {
            case '0':
            case '2':
                var title = Sanitize(payload);
                var stored = title.Length == 0 ? null : title;
                _terminalTitle = stored;
                _latestTitle = stored;
                break;
            case '7':
                var cwd = PaneWorkingDirectory.FromOsc7(Encoding.UTF8.GetString(payload), Directory.Exists);
                if (cwd is not null)
                    WorkingDirectory = cwd;
                break;
            case '9':
                _latestProgress = Sanitize(payload);
                break;
        }
    }

    private static string Sanitize(ReadOnlySpan<byte> payload)
    {
        var text = Encoding.UTF8.GetString(payload);
        var sb = new StringBuilder();
        var n = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
                continue;
            sb.Append(rune);
            n++;
            if (n >= MaxChars)
                break;
        }

        return sb.ToString();
    }
}
