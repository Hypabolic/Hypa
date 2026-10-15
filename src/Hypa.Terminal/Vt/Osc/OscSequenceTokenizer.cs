using System.Buffers;

namespace Hypa.Terminal.Vt.Osc;

/// <summary>
/// The one escape-sequence reader for pane output.
/// It finds OSC strings, RIS (<c>ESC c</c>), and DECSTR (<c>CSI ! p</c>).
/// It skips DCS, APC, PM, and SOS strings.
/// It keeps its state between calls, so a sequence split over chunks works.
/// Passive capture only. It does not change the bytes that go to the VT.
/// </summary>
/// <remarks>
/// libghostty-vt has no OSC callback, so this reader is the only managed reader.
/// A handler is chosen by the OSC number when the first semicolon arrives.
/// The handler policy then sets the body cap and the rule for ESC and CAN/SUB.
/// An OSC number with no handler is skipped with no copy and no allocation.
/// </remarks>
public sealed class OscSequenceTokenizer
{
    private const byte Esc = 0x1b;
    private const byte Bel = 0x07;
    private const byte Can = 0x18;
    private const byte Sub = 0x1a;
    private const int MaxNumberDigits = 5;
    private const int InitialBodyCapacity = 256;

    // Bytes that can change the state inside a string.
    private static readonly SearchValues<byte> StringStops = SearchValues.Create([Esc, Bel, Can, Sub]);

    // Before the number is known, the string follows the standard rules: ESC ends it, and CAN/SUB cancel it.
    private static readonly OscStringPolicy NumberPhasePolicy =
        new(MaxNumberDigits + 1, OscEscapeInBody.AbortAndRestart, CancelAborts: true);

    private enum State
    {
        Ground,
        Escape,
        Csi,
        Osc,
        OscEscape,
        Skip,
        SkipEscape,
    }

    private readonly OscHandlerRegistry _registry;
    private State _state;
    private byte[] _body = new byte[InitialBodyCapacity];
    private int _bodyLength;
    private bool _numberKnown;
    private OscStringPolicy _policy = NumberPhasePolicy;
    private int _number;
    private IOscHandler[]? _handlers;
    private bool _skipLenient;
    private bool _skipBelEnds;
    private bool _skipCancels;
    private int _csiLength;
    private bool _csiBang;
    private OscOutput _output = null!;

    public OscSequenceTokenizer(OscHandlerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>Read <paramref name="bytes"/> and add the handler results to <paramref name="output"/> in order.</summary>
    public void Observe(ReadOnlySpan<byte> bytes, OscOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        try
        {
            var i = 0;
            while (i < bytes.Length)
            {
                if (_state == State.Ground)
                {
                    // The common chunk has no escape byte. One vector scan covers it.
                    var escape = bytes[i..].IndexOf(Esc);
                    if (escape < 0)
                        return;
                    i += escape + 1;
                    _state = State.Escape;
                    continue;
                }

                if (_state == State.Skip || (_state == State.Osc && _numberKnown))
                {
                    var stop = bytes[i..].IndexOfAny(StringStops);
                    var run = stop < 0 ? bytes[i..] : bytes[i..(i + stop)];
                    if (_state == State.Osc)
                        AppendRun(run);
                    i += run.Length;
                    if (stop < 0)
                        return;
                }

                Step(bytes[i], i);
                i++;
            }
        }
        finally
        {
            _output = null!;
        }
    }

    private void Step(byte b, int index)
    {
        switch (_state)
        {
            case State.Ground:
                if (b == Esc)
                    _state = State.Escape;
                break;
            case State.Escape:
                StepEscape(b, index);
                break;
            case State.Csi:
                StepCsi(b, index);
                break;
            case State.Osc:
                StepOsc(b, index);
                break;
            case State.OscEscape:
                StepOscEscape(b, index);
                break;
            case State.Skip:
                if (b == Bel && _skipBelEnds)
                    _state = State.Ground;
                else if (b == Esc)
                    _state = State.SkipEscape;
                else if (b is Can or Sub && _skipCancels)
                    _state = State.Ground;
                break;
            case State.SkipEscape:
                if (b == (byte)'\\')
                    _state = State.Ground;
                else if (!_skipLenient)
                    Restart(b, index);
                else if (b != Esc)
                    _state = State.Skip;
                break;
        }
    }

    private void StepEscape(byte b, int index)
    {
        switch (b)
        {
            case (byte)']':
                _bodyLength = 0;
                _numberKnown = false;
                _policy = NumberPhasePolicy;
                _handlers = null;
                _state = State.Osc;
                break;
            case (byte)'[':
                _csiLength = 0;
                _csiBang = false;
                _state = State.Csi;
                break;
            case (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X':
                BeginSkip(lenient: false, belEnds: false, cancels: true);
                break;
            case (byte)'c':
                _state = State.Ground;
                foreach (var observer in _registry.Observers)
                    observer.OnFullReset(index + 1, _output);
                break;
            case Esc:
                break;
            default:
                _state = State.Ground;
                break;
        }
    }

    private void StepCsi(byte b, int index)
    {
        if (b == Esc)
        {
            _state = State.Escape;
        }
        else if (b is >= 0x20 and <= 0x3F)
        {
            if (_csiLength < int.MaxValue)
                _csiLength++;
            _csiBang = _csiLength == 1 && b == (byte)'!';
        }
        else
        {
            // Any other byte ends the control sequence, a final byte or a cancel.
            _state = State.Ground;
            if (b == (byte)'p' && _csiLength == 1 && _csiBang)
            {
                foreach (var observer in _registry.Observers)
                    observer.OnSoftReset(index + 1, _output);
            }
        }
    }

    private void StepOsc(byte b, int index)
    {
        if (!_numberKnown)
        {
            StepOscNumber(b, index);
            return;
        }

        if (b == Bel)
            Complete(terminatorBytes: 1, index + 1);
        else if (b == Esc)
            _state = State.OscEscape;
        else if (b is Can or Sub && _policy.CancelAborts)
            Cancel();
        else
            Push(b);
    }

    // The bytes before the first semicolon are the OSC number.
    private void StepOscNumber(byte b, int index)
    {
        if (b is >= (byte)'0' and <= (byte)'9')
        {
            _body[_bodyLength++] = b;
            if (_bodyLength > MaxNumberDigits)
                SkipUnhandled();
        }
        else if (b == (byte)';')
        {
            _body[_bodyLength++] = b;
            if (Resolve())
                _numberKnown = true;
            else
                SkipUnhandled();
        }
        else if (b == Bel)
        {
            Complete(terminatorBytes: 1, index + 1);
        }
        else if (b == Esc)
        {
            _state = State.OscEscape;
        }
        else if (b is Can or Sub)
        {
            Cancel();
        }
        else
        {
            SkipUnhandled();
        }
    }

    private void StepOscEscape(byte b, int index)
    {
        if (b == (byte)'\\')
        {
            Complete(terminatorBytes: 2, index + 1);
            return;
        }

        switch (_policy.EscapeInBody)
        {
            case OscEscapeInBody.AbortAndRestart:
                Restart(b, index);
                break;
            case OscEscapeInBody.KeepAndEndOnBel:
                if (b == Bel)
                {
                    if (Push(Esc))
                        Complete(terminatorBytes: 1, index + 1);
                    else
                        _state = State.Ground;
                }
                else if (b == Esc)
                {
                    _state = Push(Esc) ? State.OscEscape : State.SkipEscape;
                }
                else if (Push(Esc) && Push(b))
                {
                    _state = State.Osc;
                }

                break;
            default:
                if (Push(Esc) && Push(b))
                    _state = State.Osc;
                break;
        }
    }

    // The ESC ends the string. The byte after it starts a new escape sequence.
    private void Restart(byte b, int index)
    {
        _bodyLength = 0;
        _handlers = null;
        _state = State.Escape;
        Step(b, index);
    }

    private void Cancel()
    {
        _bodyLength = 0;
        _handlers = null;
        _state = State.Ground;
    }

    private void BeginSkip(bool lenient, bool belEnds, bool cancels)
    {
        _bodyLength = 0;
        _handlers = null;
        _skipLenient = lenient;
        _skipBelEnds = belEnds;
        _skipCancels = cancels;
        _state = State.Skip;
    }

    private void SkipUnhandled() => BeginSkip(lenient: false, belEnds: true, cancels: true);

    // Add one body byte. Returns false when the body is over the cap and the string is skipped.
    private bool Push(byte b)
    {
        if (_bodyLength >= _policy.MaxBodyBytes)
        {
            Overflow();
            return false;
        }

        EnsureCapacity(1);
        _body[_bodyLength++] = b;
        return true;
    }

    private void AppendRun(ReadOnlySpan<byte> run)
    {
        if (run.Length == 0)
            return;
        if (_bodyLength + run.Length > _policy.MaxBodyBytes)
        {
            Overflow();
            return;
        }

        EnsureCapacity(run.Length);
        run.CopyTo(_body.AsSpan(_bodyLength));
        _bodyLength += run.Length;
    }

    private void Overflow() =>
        BeginSkip(
            lenient: _policy.EscapeInBody != OscEscapeInBody.AbortAndRestart,
            belEnds: true,
            cancels: _policy.CancelAborts);

    private void EnsureCapacity(int extra)
    {
        var needed = _bodyLength + extra;
        if (needed <= _body.Length)
            return;
        Array.Resize(ref _body, Math.Max(needed, _body.Length * 2));
    }

    // Find the handlers for the number in the body so far. Returns false when no handler wants it.
    private bool Resolve()
    {
        var body = _body.AsSpan(0, _bodyLength);
        var semicolon = body.IndexOf((byte)';');
        var token = semicolon < 0 ? body : body[..semicolon];
        if (!TryParseNumber(token, out var number)
            || !_registry.TryFind(number, out var entry))
        {
            _handlers = null;
            return false;
        }

        _number = number;
        _policy = entry.Policy;
        _handlers = entry.Handlers;
        return true;
    }

    // Accept only the canonical text of a number, so "02" is not OSC 2.
    private static bool TryParseNumber(ReadOnlySpan<byte> token, out int number)
    {
        number = 0;
        if (token.Length is 0 or > MaxNumberDigits)
            return false;
        if (token.Length > 1 && token[0] == (byte)'0')
            return false;
        foreach (var digit in token)
        {
            if (digit is < (byte)'0' or > (byte)'9')
                return false;
            number = (number * 10) + (digit - (byte)'0');
        }

        return true;
    }

    private void Complete(int terminatorBytes, int endOffset)
    {
        _state = State.Ground;
        if (!_numberKnown && Resolve())
            _numberKnown = true;
        var handlers = _handlers;
        if (_numberKnown && handlers is not null)
        {
            var sequence = new OscSequence(_number, _body.AsSpan(0, _bodyLength), terminatorBytes, endOffset);
            foreach (var handler in handlers)
                handler.Handle(in sequence, _output);
        }

        _bodyLength = 0;
        _handlers = null;
    }
}
