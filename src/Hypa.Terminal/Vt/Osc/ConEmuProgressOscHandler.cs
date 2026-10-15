using Hypa.AgentRuntime.Domain.ProgramStatus;

namespace Hypa.Terminal.Vt.Osc;

/// <summary>
/// Reads ConEmu progress, <c>OSC 9 ; 4 ; st [; pr] ST</c>, and maps it to the root program status record.
/// The result is a report signal with the source ConEmu progress. The record store decides if it counts.
/// The iTerm2 notification <c>OSC 9 ; text</c> gives no result.
/// <list type="table">
/// <item><term>st 0</term><description>Clear the root record.</description></item>
/// <item><term>st 1</term><description>Working, progress is <c>pr</c>. A missing <c>pr</c> is 0.</description></item>
/// <item><term>st 2</term><description>Error. <c>pr</c> is ignored.</description></item>
/// <item><term>st 3</term><description>Working, no progress. <c>pr</c> is ignored.</description></item>
/// <item><term>st 4</term><description>Working, progress is <c>pr</c>. A missing <c>pr</c> gives no progress.</description></item>
/// </list>
/// <c>st</c> must be 0 to 4. A given <c>pr</c> must be 0 to 100 and must end the string.
/// A bad value gives no result.
/// Passive capture only. It does not change the bytes that go to the VT.
/// </summary>
public sealed class ConEmuProgressOscHandler : IOscHandler
{
    public const int Number = 9;

    // OSC 9 has one string policy for all handlers. The agent tracker owns it.
    public static OscStringPolicy Policy => AgentOscStateTracker.Policy;

    private const int MaxDigits = 3;

    private static ReadOnlySpan<byte> Prefix => "9;4"u8;

    /// <summary>Register this handler for OSC 9, next to the agent tracker.</summary>
    public void RegisterWith(OscHandlerRegistry.Builder registry) => registry.Add(Number, Policy, this);

    public void Handle(in OscSequence sequence, OscOutput output)
    {
        var body = sequence.Body;
        if (!body.StartsWith(Prefix) || body.Length == Prefix.Length || body[Prefix.Length] != (byte)';')
            return;

        var rest = body[(Prefix.Length + 1)..];
        var stEnd = rest.IndexOf((byte)';');
        var stText = stEnd < 0 ? rest : rest[..stEnd];
        if (!TryReadNumber(stText, out var st) || st > 4)
            return;

        var hasPr = stEnd >= 0;
        var prText = hasPr ? rest[(stEnd + 1)..] : default;

        ProgramStatusReport? report = null;
        switch (st)
        {
            case 0:
                report = new ProgramStatusReport { State = ProgramStatusState.Clear };
                break;
            case 2:
                report = new ProgramStatusReport { State = ProgramStatusState.Error };
                break;
            case 3:
                report = new ProgramStatusReport { State = ProgramStatusState.Working };
                break;
            default:
                int? progress = null;
                if (hasPr)
                {
                    if (!TryReadNumber(prText, out var pr) || pr > 100)
                        return;
                    progress = pr;
                }
                else if (st == 1)
                {
                    progress = 0;
                }

                report = new ProgramStatusReport { State = ProgramStatusState.Working, Progress = progress };
                break;
        }

        output.Add(new ProgramStatusOscEffect(ProgramStatusSignal.FromReport(report, ProgramStatusReportSource.ConEmuProgress)));
    }

    // Plain decimal digits only. No sign, no space, no empty text.
    private static bool TryReadNumber(ReadOnlySpan<byte> text, out int value)
    {
        value = 0;
        if (text.Length is 0 or > MaxDigits)
            return false;
        foreach (var b in text)
        {
            if (b is < (byte)'0' or > (byte)'9')
                return false;
            value = (value * 10) + (b - (byte)'0');
        }

        return true;
    }
}
