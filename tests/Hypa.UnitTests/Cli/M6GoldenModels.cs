using System.Text.Json.Serialization;

namespace Hypa.UnitTests.Cli;

internal sealed class M6GoldenMeta
{
    [JsonPropertyName("suite")]
    public string Suite { get; init; } = "";

    [JsonPropertyName("floor")]
    public string Floor { get; init; } = "M6";

    [JsonPropertyName("cols")]
    public int Cols { get; init; }

    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    [JsonPropertyName("term")]
    public string Term { get; init; } = "xterm-256color";

    [JsonPropertyName("lang")]
    public string Lang { get; init; } = "C.UTF-8";

    [JsonPropertyName("tz")]
    public string Tz { get; init; } = "UTC";

    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = "hypa";

    [JsonPropertyName("input_kind")]
    public string InputKind { get; init; } = "";

    [JsonPropertyName("capture_kind")]
    public string CaptureKind { get; init; } = "reconstructed_tty_script";

    [JsonPropertyName("note")]
    public string Note { get; init; } = "";
}

internal sealed class M6GoldenExpected
{
    [JsonPropertyName("rpc_methods")]
    public string[] RpcMethods { get; init; } = [];

    [JsonPropertyName("forbidden_rpc_methods")]
    public string[] ForbiddenRpcMethods { get; init; } = [];

    [JsonPropertyName("checkpoints")]
    public string[] Checkpoints { get; init; } = [];

    [JsonPropertyName("detach_requested")]
    public bool DetachRequested { get; init; }

    [JsonPropertyName("detach_source")]
    public string? DetachSource { get; init; }

    [JsonPropertyName("copy_kind")]
    public string? CopyKind { get; init; }

    [JsonPropertyName("capture_kind")]
    public string CaptureKind { get; init; } = "reconstructed_tty_script";

    [JsonPropertyName("input_kind")]
    public string InputKind { get; init; } = "";

    [JsonPropertyName("sidebar_used")]
    public bool SidebarUsed { get; init; }

    [JsonPropertyName("popup_is_pane")]
    public bool PopupIsPane { get; init; }
}

internal sealed class M6ScriptLine
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("cols")]
    public int? Cols { get; init; }

    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("agent_status")]
    public string? AgentStatus { get; init; }

    [JsonPropertyName("occupant_generation")]
    public int? OccupantGeneration { get; init; }

    [JsonPropertyName("seen")]
    public bool? Seen { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("seq")]
    public long? Seq { get; init; }
}
