using Hypa.AgentRuntime.Domain.ProgramStatus;

namespace Hypa.Terminal.Vt.Osc;

/// <summary>
/// Reads the shell integration mark OSC 133 ; A (prompt start).
/// Parameters after the A are allowed. The marks B, C, and D give no result.
/// The result is a prompt start signal for the program status of the pane.
/// Passive capture only. It does not change the bytes that go to the VT.
/// </summary>
public sealed class ShellPromptOscHandler : IOscHandler
{
    public const int Number = 133;

    // A mark with its parameters is short. A small cap keeps a bad string cheap.
    // A bad escape or a cancel byte drops the mark, as the VT drops the string.
    public static OscStringPolicy Policy { get; } =
        new(MaxBodyBytes: 256, OscEscapeInBody.AbortAndRestart, CancelAborts: true);

    private static ReadOnlySpan<byte> Prefix => "133;A"u8;

    /// <summary>Register this handler for OSC 133.</summary>
    public void RegisterWith(OscHandlerRegistry.Builder registry) => registry.Add(Number, Policy, this);

    public void Handle(in OscSequence sequence, OscOutput output)
    {
        var body = sequence.Body;
        if (!body.StartsWith(Prefix))
            return;

        // The mark is the A alone, or the A and a semicolon with parameters.
        if (body.Length > Prefix.Length && body[Prefix.Length] != (byte)';')
            return;

        output.Add(new ProgramStatusOscEffect(ProgramStatusSignal.PromptStart));
    }
}
