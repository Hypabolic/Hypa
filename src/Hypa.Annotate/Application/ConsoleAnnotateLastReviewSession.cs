using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Minimal popup review that reads comment lines from stdin.
/// </summary>
public sealed class ConsoleAnnotateLastReviewSession(TextReader input, TextWriter output)
    : IAnnotateLastReviewSession
{
    public AnnotateLastReviewSessionResult Run(PendingLastReview pending)
    {
        output.WriteLine("Agent message");
        output.WriteLine(pending.AgentMessage);
        output.WriteLine("Review (/send to send, /cancel to cancel)");
        var lines = new List<string>();
        while (true)
        {
            var line = input.ReadLine();
            if (line is null)
                return new AnnotateLastReviewSessionResult { Outcome = AnnotateLastReviewOutcome.Cancelled };

            if (string.Equals(line, "/cancel", StringComparison.Ordinal))
                return new AnnotateLastReviewSessionResult { Outcome = AnnotateLastReviewOutcome.Cancelled };

            if (string.Equals(line, "/send", StringComparison.Ordinal))
            {
                return new AnnotateLastReviewSessionResult
                {
                    Outcome = AnnotateLastReviewOutcome.Sent,
                    Comment = string.Join('\n', lines),
                };
            }

            lines.Add(line);
        }
    }
}
