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

        // Shell reserved words / control constructs require intact grammar
        // (loops, conditionals, brace groups). Stateful builtins do not —
        // they are left raw at segment level while rewritable siblings rewrite.
        if (segments.Any(SegmentRequiresWholeShell))
            return RewriteDecision.Passthrough();

        if (segments.Count == 1)
            return RewriteSegment(segments[0].Tokens, context);

        return RewriteCompound(segments, context);
    }

    private static bool SegmentRequiresWholeShell(Segment segment)
    {
        var verb = ShellVerb.Extract(segment.Tokens);
        return verb is not null && ShellReservedWords.IsReservedWord(verb);
    }

    private RewriteDecision RewriteSegment(
        IReadOnlyList<ShellToken> tokens, RewriteContext context)
    {
        // Any stdout/stdin redirect anywhere in the segment (including pipe consumers)
        // can make compressed producer output land in a file or change read intent.
        // Only stderr merge (2>&1) is treated as safe plumbing.
        if (HasUnsafeRedirect(tokens))
            return RewriteDecision.Passthrough();

        // Split at the first pipe: rewrite producer only; leave consumer raw.
        if (!TrySplitAtFirstPipe(tokens, out var producerTokens, out var pipeSuffix))
            return RewriteDecision.Passthrough();

        // Peel trailing safe redirects (e.g. 2>&1) off the producer; re-append later.
        if (!TrySplitTrailingRedirects(producerTokens, out var coreTokens, out var redirectSuffix, out var redirectsSafe))
            return RewriteDecision.Passthrough();

        if (!redirectsSafe)
            return RewriteDecision.Passthrough();

        // Pipes: only first-class strategies — generic compression can break consumers
        // that expect native producer format (grep, path listers, etc.).
        var allowGenericWrapper = pipeSuffix is null;
        var coreDecision = RewriteSimpleCommand(coreTokens, context, allowGenericWrapper);

        if (coreDecision.Outcome is RewriteOutcome.Deny or RewriteOutcome.Ask)
            return coreDecision;

        if (coreDecision.Outcome == RewriteOutcome.Passthrough || coreDecision.Command is null)
            return RewriteDecision.Passthrough();

        var reassembled = coreDecision.Command
            + (redirectSuffix ?? string.Empty)
            + (pipeSuffix ?? string.Empty);

        return coreDecision.Outcome == RewriteOutcome.GenericWrapper
            ? RewriteDecision.Generic(reassembled)
            : RewriteDecision.Rewritten(reassembled);
    }

    private RewriteDecision RewriteSimpleCommand(
        IReadOnlyList<ShellToken> tokens,
        RewriteContext context,
        bool allowGenericWrapper)
    {
        var verb = ShellVerb.Extract(tokens);
        if (verb is null)
            return RewriteDecision.Passthrough();

        // Stateful builtins (cd/export/…) must never be wrapped — they mutate shell state.
        if (ShellBuiltins.IsStateful(verb) || ShellReservedWords.IsReservedWord(verb))
            return RewriteDecision.Passthrough();

        if (context.ExcludeCommands.Contains(verb, StringComparer.OrdinalIgnoreCase))
            return RewriteDecision.Passthrough();

        // Strategy matching uses the extracted verb (skips VAR=value prefixes).
        foreach (var strategy in _strategies)
        {
            if (strategy.CanHandle(verb))
                return strategy.Rewrite(tokens, context);
        }

        if (allowGenericWrapper && context.GenericWrapperEnabled)
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
            var decision = RewriteSegment(segment.Tokens, context);

            if (decision.Outcome == RewriteOutcome.Deny)
                return RewriteDecision.Deny();

            // Preserve Ask so compound approval is not silently auto-allowed.
            if (decision.Outcome == RewriteOutcome.Ask)
                return decision;

            if (decision.Outcome != RewriteOutcome.Passthrough)
                anyRewritten = true;

            parts.Add(decision.Command ?? raw);
        }

        if (!anyRewritten)
            return RewriteDecision.Passthrough();

        var joined = string.Join(" ", parts);
        return RewriteDecision.Rewritten(joined);
    }

    /// <summary>
    /// True when the segment contains a redirect other than stderr merge (<c>2>&1</c>).
    /// Covers producer and pipe-consumer sides so write redirects force passthrough.
    /// </summary>
    private static bool HasUnsafeRedirect(IReadOnlyList<ShellToken> tokens) =>
        tokens.Any(t => t.Kind == TokenKind.Redirect && t.Value != "2>&1");

    /// <summary>
    /// Split tokens at the first pipe. <paramref name="pipeSuffix"/> includes leading
    /// whitespace before the pipe, the pipe token, and the consumer side so reassembly
    /// preserves spacing (<c>cmd | consumer</c>).
    /// </summary>
    private static bool TrySplitAtFirstPipe(
        IReadOnlyList<ShellToken> tokens,
        out IReadOnlyList<ShellToken> producerTokens,
        out string? pipeSuffix)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Pipe)
                continue;

            var producerEnd = i;
            while (producerEnd > 0 && tokens[producerEnd - 1].Kind == TokenKind.Whitespace)
                producerEnd--;

            producerTokens = tokens.Take(producerEnd).ToList();
            pipeSuffix = string.Join("", tokens.Skip(producerEnd).Select(t => t.Value));
            return true;
        }

        producerTokens = tokens;
        pipeSuffix = null;
        return true;
    }

    /// <summary>
    /// Split a simple command (no pipes) into core tokens and a trailing redirect
    /// suffix. Safe trailing plumbing today is only <c>2>&1</c> (stderr merge).
    /// stdout/stdin redirects and redirects with targets stay unsafe → caller passthroughs.
    /// The suffix includes leading whitespace so reassembly yields <c>cmd 2>&1</c>.
    /// </summary>
    private static bool TrySplitTrailingRedirects(
        IReadOnlyList<ShellToken> tokens,
        out IReadOnlyList<ShellToken> coreTokens,
        out string? redirectSuffix,
        out bool redirectsSafe)
    {
        coreTokens = tokens;
        redirectSuffix = null;
        redirectsSafe = true;

        var firstRedirect = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.Redirect)
            {
                firstRedirect = i;
                break;
            }
        }

        if (firstRedirect < 0)
            return true;

        // Everything from the first redirect to the end must be safe trailing plumbing.
        // Safe: whitespace and Redirect("2>&1") only. Any other redirect, or any arg
        // (redirect target), is unsafe / non-trailing.
        for (var i = firstRedirect; i < tokens.Count; i++)
        {
            var kind = tokens[i].Kind;
            if (kind == TokenKind.Whitespace)
                continue;

            if (kind == TokenKind.Redirect && tokens[i].Value == "2>&1")
                continue;

            redirectsSafe = false;
            return true;
        }

        var coreEnd = firstRedirect;
        while (coreEnd > 0 && tokens[coreEnd - 1].Kind == TokenKind.Whitespace)
            coreEnd--;

        coreTokens = tokens.Take(coreEnd).ToList();
        redirectSuffix = string.Join("", tokens.Skip(coreEnd).Select(t => t.Value));
        return true;
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
