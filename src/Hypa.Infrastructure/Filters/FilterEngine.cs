using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Filters;

namespace Hypa.Infrastructure.Filters;

public sealed partial class FilterEngine : IFilterEngine
{
    public FilterResult Apply(CompiledFilterDefinition filter, string text)
    {
        var current = text;
        var applied = 0;

        foreach (var stage in filter.Stages)
        {
            if (stage.Stage.Kind == FilterStageKind.MatchOutput)
            {
                if (stage.CompiledRegex is not null && stage.CompiledRegex.IsMatch(current))
                {
                    var guardBlocks = stage.CompiledGuard is not null && stage.CompiledGuard.IsMatch(current);
                    if (!guardBlocks)
                        return new FilterResult(stage.Stage.Replacement ?? string.Empty, filter.Id, applied + 1);
                }

                continue;
            }

            var next = ApplyStage(stage, current);
            if (next != current)
                applied++;
            current = next;
        }

        return new FilterResult(current, filter.Id, applied);
    }

    private static string ApplyStage(CompiledFilterStage stage, string text) =>
        stage.Stage.Kind switch
        {
            FilterStageKind.StripAnsi => StripAnsiPattern().Replace(text, string.Empty),
            FilterStageKind.KeepLines => ApplyKeepLines(text, stage.CompiledRegex),
            FilterStageKind.StripLines => ApplyStripLines(text, stage.CompiledRegex),
            FilterStageKind.Replace => ApplyReplace(text, stage.CompiledRegex, stage.Stage.Replacement ?? string.Empty),
            FilterStageKind.HeadLines => ApplyHeadLines(text, stage.Stage.Count ?? 50),
            FilterStageKind.TailLines => ApplyTailLines(text, stage.Stage.Count ?? 50),
            FilterStageKind.MaxLines => ApplyMaxLines(text, stage.Stage.Count ?? 200),
            FilterStageKind.TruncateLinesAt => ApplyTruncateLinesAt(text, stage.Stage.Count ?? 200),
            FilterStageKind.OnEmpty => string.IsNullOrWhiteSpace(text) ? (stage.Stage.Replacement ?? text) : text,
            FilterStageKind.NativeTransform => NativeFilterTransforms.Apply(stage.Stage.TransformId, text),
            _ => text,
        };

    private static string ApplyKeepLines(string text, Regex? rx)
    {
        if (rx is null) return text;
        var lines = text.Split('\n');
        return string.Join('\n', lines.Where(l => rx.IsMatch(l)));
    }

    private static string ApplyStripLines(string text, Regex? rx)
    {
        if (rx is null) return text;
        var lines = text.Split('\n');
        return string.Join('\n', lines.Where(l => !rx.IsMatch(l)));
    }

    private static string ApplyReplace(string text, Regex? rx, string replacement)
    {
        if (rx is null) return text;
        return rx.Replace(text, replacement);
    }

    private static string ApplyHeadLines(string text, int count)
    {
        var lines = text.Split('\n');
        return lines.Length <= count ? text : string.Join('\n', lines.Take(count));
    }

    private static string ApplyTailLines(string text, int count)
    {
        var lines = text.Split('\n');
        return lines.Length <= count ? text : string.Join('\n', lines.TakeLast(count));
    }

    private static string ApplyMaxLines(string text, int max)
    {
        var lines = text.Split('\n');
        if (lines.Length <= max) return text;
        var headCount = (max + 1) / 2;
        var tailCount = max / 2;
        var head = lines.Take(headCount);
        var tail = lines.TakeLast(tailCount);
        return string.Join('\n', head) + $"\n... ({lines.Length - max} lines omitted) ...\n" + string.Join('\n', tail);
    }

    private static string ApplyTruncateLinesAt(string text, int max)
    {
        var lines = text.Split('\n');
        return lines.Length <= max ? text : string.Join('\n', lines.Take(max)) + $"\n... truncated at {max} lines";
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[mGKHF]")]
    private static partial Regex StripAnsiPattern();
}
