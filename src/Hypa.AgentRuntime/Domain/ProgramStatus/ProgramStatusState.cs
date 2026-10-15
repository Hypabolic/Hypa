namespace Hypa.AgentRuntime.Domain.ProgramStatus;

/// <summary>State of one program status record, or the clear command.</summary>
public enum ProgramStatusState
{
    Idle,
    Working,
    Done,
    Blocked,
    Error,

    /// <summary>Command, not a stored state. It removes records.</summary>
    Clear,
}

/// <summary>Reason a record is blocked.</summary>
public enum ProgramStatusKind
{
    Permission,
    Question,
    Auth,
}

/// <summary>Wire names for program status values.</summary>
public static class ProgramStatusWire
{
    public static string ToWire(this ProgramStatusState state) => state switch
    {
        ProgramStatusState.Idle => "idle",
        ProgramStatusState.Working => "working",
        ProgramStatusState.Done => "done",
        ProgramStatusState.Blocked => "blocked",
        ProgramStatusState.Error => "error",
        _ => "clear",
    };

    public static string ToWire(this ProgramStatusKind kind) => kind switch
    {
        ProgramStatusKind.Permission => "permission",
        ProgramStatusKind.Question => "question",
        _ => "auth",
    };

    public static bool TryParseState(ReadOnlySpan<char> text, out ProgramStatusState state)
    {
        switch (text)
        {
            case "idle": state = ProgramStatusState.Idle; return true;
            case "working": state = ProgramStatusState.Working; return true;
            case "done": state = ProgramStatusState.Done; return true;
            case "blocked": state = ProgramStatusState.Blocked; return true;
            case "error": state = ProgramStatusState.Error; return true;
            case "clear": state = ProgramStatusState.Clear; return true;
            default: state = default; return false;
        }
    }

    public static bool TryParseKind(ReadOnlySpan<char> text, out ProgramStatusKind kind)
    {
        switch (text)
        {
            case "permission": kind = ProgramStatusKind.Permission; return true;
            case "question": kind = ProgramStatusKind.Question; return true;
            case "auth": kind = ProgramStatusKind.Auth; return true;
            default: kind = default; return false;
        }
    }
}
