using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Infrastructure.Rewrite;

public sealed class CommandRewriteRegistry(
    IShellLexer lexer,
    IEnumerable<ICommandRewriteStrategy> strategies,
    GenericWrapperStrategy genericWrapper) : ICommandRewriteRegistry
{
    private readonly IReadOnlyList<ICommandRewriteStrategy> _strategies = strategies
        .Where(s => s is not GenericWrapperStrategy)
        .ToList();

    public RewriteDecision Rewrite(string command, RewriteContext context)
    {
        var tokens = lexer.Lex(command);

        // Any shellism token (e.g. $(...), `, trailing &) — pass through unconditionally
        if (tokens.Any(t => t.Kind == TokenKind.Shellism))
            return RewriteDecision.Passthrough();

        // Split on compound operators and rewrite each segment
        var segments = SplitOnOperators(tokens);

        // A builtin that mutates shell state can't survive being split into separate
        // `hypa -c` processes. If any segment is such a builtin, pass the whole
        // command through unchanged to preserve exact shell semantics.
        if (segments.Any(SegmentIsStatefulBuiltin))
            return RewriteDecision.Passthrough();

        if (segments.Count == 1)
            return RewriteSegment(segments[0].Tokens, command, context);

        return RewriteCompound(segments, context);
    }

    private static bool SegmentIsStatefulBuiltin(Segment segment)
    {
        var verb = ShellVerb.Extract(segment.Tokens);
        return verb is not null && ShellBuiltins.IsStateful(verb);
    }

    private RewriteDecision RewriteSegment(
        IReadOnlyList<ShellToken> tokens, string originalCommand, RewriteContext context)
    {
        // Pipe or redirect: passthrough to preserve shell plumbing intact
        if (tokens.Any(t => t.Kind is TokenKind.Pipe or TokenKind.Redirect))
            return RewriteDecision.Passthrough();

        var verb = tokens.FirstOrDefault(t => t.Kind == TokenKind.Arg)?.Value;
        if (verb is null)
            return RewriteDecision.Passthrough();

        if (context.ExcludeCommands.Contains(verb, StringComparer.OrdinalIgnoreCase))
            return RewriteDecision.Passthrough();

        foreach (var strategy in _strategies)
        {
            if (strategy.CanHandle(verb))
                return strategy.Rewrite(tokens, context);
        }

        if (context.GenericWrapperEnabled)
            return genericWrapper.Rewrite(tokens, context);

        return RewriteDecision.Passthrough();
    }

    private RewriteDecision RewriteCompound(IReadOnlyList<Segment> segments, RewriteContext context)
    {
        var parts = new List<string>();
        var anyRewritten = false;

        foreach (var segment in segments)
        {
            if (segment.LeadingOperator is not null)
                parts.Add(segment.LeadingOperator);

            if (segment.Tokens.Count == 0)
                continue;

            var raw = string.Join("", segment.Tokens.Select(t => t.Value)).Trim();
            var decision = RewriteSegment(segment.Tokens, raw, context);

            if (decision.Outcome == RewriteOutcome.Deny)
                return RewriteDecision.Deny();

            if (decision.Outcome != RewriteOutcome.Passthrough)
                anyRewritten = true;

            parts.Add(decision.Command ?? raw);
        }

        if (!anyRewritten)
            return RewriteDecision.Passthrough();

        var joined = string.Join(" ", parts);
        return RewriteDecision.Rewritten(joined);
    }

    private static IReadOnlyList<Segment> SplitOnOperators(IReadOnlyList<ShellToken> tokens)
    {
        var segments = new List<Segment>();
        var current = new List<ShellToken>();
        string? leadingOp = null;

        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.Operator)
            {
                segments.Add(new Segment(leadingOp, current));
                current = [];
                leadingOp = token.Value;
            }
            else
            {
                current.Add(token);
            }
        }

        segments.Add(new Segment(leadingOp, current));
        return segments;
    }

    private sealed record Segment(string? LeadingOperator, IReadOnlyList<ShellToken> Tokens);
}
