namespace Hypa.Runtime.Domain.Filters;

public enum FilterStageKind
{
    StripAnsi,
    Replace,
    MatchOutput,
    KeepLines,
    StripLines,
    TruncateLinesAt,
    HeadLines,
    TailLines,
    MaxLines,
    OnEmpty,
    NativeTransform,
}
