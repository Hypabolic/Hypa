namespace Hypa.Annotate.Application;

/// <summary>
/// User-visible refusal messages for the <c>last</c> action.
/// </summary>
public static class AnnotateLastMessages
{
    public const string NoAgentSession = "No agent session on this pane.";

    public const string UnsupportedAgent =
        "Agent transcript review is not supported for this agent.";

    public const string UnreadableTranscript = "Cannot read the agent transcript.";
}
