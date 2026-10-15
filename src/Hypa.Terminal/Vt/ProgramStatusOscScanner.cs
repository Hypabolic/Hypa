using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.Terminal.Vt.Osc;

namespace Hypa.Terminal.Vt;

/// <summary>One program status signal in the output of a chunk.</summary>
public sealed record ProgramStatusOscEffect(ProgramStatusSignal Signal) : OscEffect;

/// <summary>
/// Reads OSC 7501 reports and queries, and RIS, from the strings the tokenizer finds.
/// It is an OSC handler and a control sequence observer.
/// Passive capture only. It does not change the bytes that go to the VT.
/// </summary>
public sealed class ProgramStatusOscScanner : IOscHandler, IControlSequenceObserver
{
    public const int Number = 7501;

    private static ReadOnlySpan<byte> Prefix => "7501;"u8;

    // A bad escape or a cancel byte drops the report. The cap is the whole sequence.
    public static OscStringPolicy Policy { get; } =
        new(ProgramStatusParser.MaxSequenceBytes, OscEscapeInBody.AbortAndRestart, CancelAborts: true);

    /// <summary>Register this handler for OSC 7501 and for RIS.</summary>
    public void RegisterWith(OscHandlerRegistry.Builder registry)
    {
        registry.Add(Number, Policy, this).AddObserver(this);
    }

    public void Handle(in OscSequence sequence, OscOutput output)
    {
        var body = sequence.Body;
        if (body.Length < Prefix.Length)
            return;

        var payload = body[Prefix.Length..];
        if (payload.Length == 1 && payload[0] == (byte)'?')
        {
            output.Add(new ProgramStatusOscEffect(ProgramStatusSignal.Query));
            return;
        }

        var parsed = ProgramStatusParser.Parse(payload, sequence.TerminatorBytes);
        if (parsed.IsOk)
            output.Add(new ProgramStatusOscEffect(ProgramStatusSignal.FromReport(parsed.Value)));
    }

    public void OnFullReset(int endOffset, OscOutput output) =>
        output.Add(new ProgramStatusOscEffect(ProgramStatusSignal.Reset));

    public void OnSoftReset(int endOffset, OscOutput output)
    {
        // DECSTR is not a program status event.
    }
}
