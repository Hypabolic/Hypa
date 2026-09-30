using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>Expected overlay failure. Does not throw.</summary>
public sealed record PaneOverlayError
{
    public const string NotFoundCode = "not_found";
    public const string MissingOwnerCode = "missing_owner";
    public const string UnknownOwnerCode = "unknown_owner";
    public const string BusyCode = "ui_busy";
    public const string TiledCode = "tiled";
    public const string StaleGenerationCode = "stale_generation";

    public required string Code { get; init; }

    public required string Message { get; init; }

    public static PaneOverlayError NotFound(string message) =>
        new() { Code = NotFoundCode, Message = message };

    public static PaneOverlayError MissingOwner(string message) =>
        new() { Code = MissingOwnerCode, Message = message };

    public static PaneOverlayError UnknownOwner(string message) =>
        new() { Code = UnknownOwnerCode, Message = message };

    public static PaneOverlayError Busy(string message) =>
        new() { Code = BusyCode, Message = message };

    public static PaneOverlayError Tiled(string message) =>
        new() { Code = TiledCode, Message = message };

    public static PaneOverlayError StaleGeneration(string message) =>
        new() { Code = StaleGenerationCode, Message = message };
}

public sealed record PaneShowOverlayRequest
{
    public required PaneId PaneId { get; init; }

    public required string AttachClientId { get; init; }

    public int? AreaCols { get; init; }

    public int? AreaRows { get; init; }
}

public sealed record PaneHideOverlayRequest
{
    public required PaneId PaneId { get; init; }

    public string? AttachClientId { get; init; }

    public long? Generation { get; init; }
}

public sealed record PaneOverlayOwner
{
    public required PaneId PaneId { get; init; }

    public required string AttachClientId { get; init; }

    public required long Generation { get; init; }

    public int InnerCols { get; init; }

    public int InnerRows { get; init; }
}

public sealed record PaneOverlayChange
{
    public required PaneState Pane { get; init; }

    public string? AttachClientId { get; init; }

    public required long Generation { get; init; }

    public required bool Changed { get; init; }

    public required string Mode { get; init; }
}

public sealed record PaneOverlayInputAdmit
{
    public required string AttachClientId { get; init; }

    public required PaneId PaneId { get; init; }

    public required long Generation { get; init; }
}
