using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Published Hypa protocol schema.
/// </summary>
public static class ProtocolSchemaCatalog
{
    public const int SchemaVersion = 1;

    public static IReadOnlyList<string> ReservedMethods { get; } = [];

    public static IReadOnlyList<string> ReservedEvents { get; } = [];

    public static IReadOnlyList<string> PostM6Methods { get; } =
    [
        ProtocolMethods.ServerReloadConfig,
        .. ProtocolMethods.H53,
        .. ProtocolMethods.H54,
        ProtocolMethods.PaneRead,
        .. ProtocolMethods.H56,
        .. ProtocolMethods.H57,
        .. ProtocolMethods.AgentExplainView,
        .. ProtocolMethods.EventWait,
        .. ProtocolMethods.Plugins,
        ProtocolMethods.NotificationShow,
        ProtocolMethods.PaneScroll,
    ];

    public static IReadOnlyList<string> PostM6Events { get; } =
    [
        ProtocolEventTypes.PaneScrollChanged,
        ProtocolEventTypes.LayoutUpdated,
        ProtocolEventTypes.PaneAgentStatusChanged,
        ProtocolEventTypes.ConfigReloaded,
        ProtocolEventTypes.PopupLifecycle,
        ProtocolEventTypes.WorkspaceMetadataUpdated,
        ProtocolEventTypes.PaneMetadataUpdated,
        ProtocolEventTypes.ResourceChanged,
        ProtocolEventTypes.ConfigChanged,
    ];

    public static ProtocolSchemaDocument Create()
    {
        var methods = ProtocolMethods.All
            .Select(name => new ProtocolSchemaMethodEntry
            {
                Name = name,
                Status = ReservedMethods.Contains(name, StringComparer.Ordinal)
                    ? ProtocolSchemaStatus.Reserved
                    : ProtocolSchemaStatus.Implemented,
            })
            .ToList();

        var events = UniqueEvents()
            .Select(name => new ProtocolSchemaEventEntry
            {
                Name = name,
                Status = ReservedEvents.Contains(name, StringComparer.Ordinal)
                    ? ProtocolSchemaStatus.Reserved
                    : ProtocolSchemaStatus.Implemented,
            })
            .ToList();

        return new ProtocolSchemaDocument
        {
            ProtocolName = ProtocolVersion.Name,
            ProtocolMajor = ProtocolVersion.Major,
            ProtocolMinor = ProtocolVersion.Minor,
            EndpointGeneration = ProtocolAttachEndpoint.EndpointGeneration,
            SchemaVersion = SchemaVersion,
            Methods = methods,
            Events = events,
            PaneReadSources =
            [
                ProtocolPaneReadSources.Visible,
                ProtocolPaneReadSources.Recent,
                ProtocolPaneReadSources.RecentUnwrapped,
                ProtocolPaneReadSources.Detection,
            ],
            Attach = new ProtocolSchemaAttachSection
            {
                EndpointGeneration = ProtocolAttachEndpoint.EndpointGeneration,
                Methods = [.. ProtocolAttachEndpoint.Methods],
                Events = [.. ProtocolAttachEndpoint.Events],
                Codecs = [.. ProtocolAttachEndpoint.Codecs],
                RequiredCapabilities = [.. ProtocolAttachEndpoint.RequiredCapabilities],
                OptionalCapabilities = [.. ProtocolAttachEndpoint.OptionalCapabilities],
                Records = [.. ProtocolAttachEndpoint.Records],
            },
            Render = CreateRenderSection(),
        };
    }

    /// <summary>
    /// Wire shapes for <c>terminal.render</c>. Field names match the live encoder.
    /// <c>omit_when</c> is the value the encoder does not write.
    /// </summary>
    public static ProtocolSchemaRenderSection CreateRenderSection()
    {
        return new ProtocolSchemaRenderSection
        {
            Event = ProtocolEventTypes.TerminalRender,
            Payloads =
            [
                CellsPayload(),
                CellRow(),
                StyleRun(),
                CursorPayload(),
                SnapshotPayload(),
                SnapshotGrid(),
                SnapshotCursor(),
                ScrollRegion(),
                SnapshotModes(),
                SnapshotCell(),
                SnapshotStyle(),
                BlitPayload(),
                PaneByteFallback(),
                PopupByteFallback(),
            ],
        };
    }

    private static ProtocolSchemaRenderPayloadEntry CellsPayload() =>
        new()
        {
            Name = "cells",
            Kind = TerminalRenderCellsPayload.KindCells,
            Fields =
            [
                Field("pane_id", "string", "null"),
                Field("kind", "string"),
                Field("full", "bool"),
                Field("grid_cols", "int"),
                Field("grid_rows", "int"),
                Field("generation", "int"),
                Field("base_generation", "int", "0"),
                Field("occupant_generation", "int", "0"),
                Field("reanchor", "bool", "false"),
                Field("changed_cells", "int", "0"),
                Field("rows", "row[]", "null"),
                Field("cursor", "cursor", "null"),
                Field("active_screen", "string", "null"),
                Field("provider", "string", "null"),
                Field("viewport_origin", "int", "0"),
                Field("sync", "bool", "false"),
                Field("mouse", "string", "null"),
                Field("mouse_encoding", "string", "null"),
                Field("bracketed_paste", "bool", "false"),
                Field("application_cursor", "bool", "false"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry CellRow() =>
        new()
        {
            Name = "row",
            Parent = "cells",
            Fields =
            [
                Field("i", "int"),
                Field("t", "string"),
                Field("w", "int[]", "null"),
                Field("g", "int[]", "null"),
                Field("s", "style[]", "null"),
                Field("c", "int", "null"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry StyleRun() =>
        new()
        {
            Name = "style",
            Parent = "row",
            Fields =
            [
                Field("c", "int"),
                Field("n", "int"),
                Field("fg", "uint", "0"),
                Field("bg", "uint", "0"),
                Field("bold", "bool", "null"),
                Field("dim", "bool", "null"),
                Field("italic", "bool", "null"),
                Field("underline", "bool", "null"),
                Field("inverse", "bool", "null"),
                Field("invisible", "bool", "null"),
                Field("strikethrough", "bool", "null"),
                Field("blink", "bool", "null"),
                Field("overline", "bool", "null"),
                Field("underline_color", "uint", "0"),
                Field("underline_style", "int", "null"),
                Field("hyperlink", "string", "null"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry CursorPayload() =>
        new()
        {
            Name = "cursor",
            Parent = "cells",
            Fields =
            [
                Field("col", "int"),
                Field("row", "int"),
                Field("visible", "bool"),
                Field("shape", "int", "0"),
                Field("has_cursor", "bool", "null"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry SnapshotPayload() =>
        new()
        {
            Name = "snapshot",
            Kind = TerminalRenderSnapshotPayload.KindSnapshot,
            Fields =
            [
                Field("pane_id", "string", "null"),
                Field("target", "string", "null"),
                Field("kind", "string"),
                Field("row_start", "int"),
                Field("row_end", "int"),
                Field("complete", "bool"),
                Field("grid_cols", "int"),
                Field("grid_rows", "int"),
                Field("generation", "int"),
                Field("patch", "bool", "false"),
                Field("occupant_generation", "int", "0"),
                Field("snapshot", "snapshot_grid"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry SnapshotGrid() =>
        new()
        {
            Name = "snapshot_grid",
            Parent = "snapshot",
            Fields =
            [
                Field("schema_version", "int", "0"),
                Field("provider", "string", "null"),
                Field("cols", "int"),
                Field("rows", "int"),
                Field("cursor", "snapshot_cursor", "absent"),
                Field("active_screen", "string"),
                Field("scroll_region", "scroll_region"),
                Field("modes", "modes"),
                Field("cells", "snapshot_cell[][]"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry SnapshotCursor() =>
        new()
        {
            Name = "snapshot_cursor",
            Parent = "snapshot_grid",
            Fields =
            [
                Field("col", "int", "0"),
                Field("row", "int", "0"),
                Field("visible", "bool"),
                Field("shape", "int", "0"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry ScrollRegion() =>
        new()
        {
            Name = "scroll_region",
            Parent = "snapshot_grid",
            Fields =
            [
                Field("top", "int", "0"),
                Field("bottom", "int", "0"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry SnapshotModes() =>
        new()
        {
            Name = "modes",
            Parent = "snapshot_grid",
            Fields =
            [
                Field("origin", "bool", "false"),
                Field("auto_wrap", "bool", "false"),
                Field("insert", "bool", "false"),
                Field("bracketed_paste", "bool", "false"),
                Field("mouse", "string", "null"),
                Field("focus_reporting", "bool", "false"),
                Field("sync", "bool", "false"),
                Field("application_cursor", "bool", "false"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry SnapshotCell() =>
        new()
        {
            Name = "snapshot_cell",
            Parent = "snapshot_grid",
            Fields =
            [
                Field("text", "string"),
                Field("width", "int", "0"),
                Field("is_continuation", "bool", "false"),
                Field("style", "snapshot_style"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry SnapshotStyle() =>
        new()
        {
            Name = "snapshot_style",
            Parent = "snapshot_cell",
            Fields =
            [
                Field("fg", "string", "null"),
                Field("bg", "string", "null"),
                Field("bold", "bool", "false"),
                Field("dim", "bool", "false"),
                Field("italic", "bool", "false"),
                Field("underline", "bool", "false"),
                Field("inverse", "bool", "false"),
                Field("invisible", "bool", "false"),
                Field("strikethrough", "bool", "false"),
                Field("blink", "bool", "false"),
                Field("overline", "bool", "false"),
                Field("underline_color", "string", "null"),
                Field("underline_style", "int", "0"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry BlitPayload() =>
        new()
        {
            Name = "blit",
            Kind = TerminalRenderBlitPayload.KindBlit,
            Legacy = true,
            Fields =
            [
                Field("pane_id", "string", "null"),
                Field("kind", "string"),
                Field("ansi", "string", "null"),
                Field("full", "bool"),
                Field("grid_cols", "int"),
                Field("grid_rows", "int"),
                Field("generation", "int"),
                Field("base_generation", "int", "0"),
                Field("occupant_generation", "int", "0"),
                Field("reanchor", "bool", "false"),
                Field("changed_cells", "int", "0"),
                Field("wire_bytes", "int", "0"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry PaneByteFallback() =>
        new()
        {
            Name = "byte_fallback",
            Legacy = true,
            Fields =
            [
                Field("pane_id", "string"),
                Field("encoding", "string"),
                Field("data", "string"),
                Field("byte_count", "int"),
                Field("route", "string", "null"),
            ],
        };

    private static ProtocolSchemaRenderPayloadEntry PopupByteFallback() =>
        new()
        {
            Name = "popup_byte_fallback",
            Legacy = true,
            Fields =
            [
                Field("target", "string"),
                Field("encoding", "string"),
                Field("data", "string"),
                Field("byte_count", "int"),
            ],
        };

    private static ProtocolSchemaRenderFieldEntry Field(string name, string type, string? omitWhen = null) =>
        new()
        {
            Name = name,
            Type = type,
            OmitWhen = omitWhen,
        };

    public static bool IsPublished(string method) =>
        ProtocolMethods.All.Contains(method, StringComparer.Ordinal);

    private static IReadOnlyList<string> UniqueEvents()
    {
        var names = new List<string>();
        AddRange(names, ProtocolEventTypes.P0);
        AddRange(names, ProtocolEventTypes.H07);
        AddRange(names, ProtocolEventTypes.H10);
        AddRange(names, ProtocolEventTypes.H12);
        AddRange(names, ProtocolEventTypes.H25);
        AddRange(names, ProtocolEventTypes.H41);
        AddRange(names, ProtocolEventTypes.H44);
        AddRange(names, ProtocolEventTypes.H52);
        AddRange(names, ProtocolEventTypes.H54);
        AddRange(names, ProtocolEventTypes.H56);
        AddRange(names, ProtocolEventTypes.H57);
        AddRange(names, ProtocolEventTypes.PaneVisibility);
        AddRange(names, ProtocolEventTypes.PluginResources);
        AddRange(names, ProtocolEventTypes.PluginConfig);
        AddRange(names, ProtocolEventTypes.Worktrees);
        AddRange(names, ProtocolEventTypes.H91);
        AddRange(names, ProtocolEventTypes.SettingsOverlay);
        return names;
    }

    private static void AddRange(List<string> names, IReadOnlyList<string> extra)
    {
        foreach (var name in extra)
        {
            if (!names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }
    }
}

public static class ProtocolSchemaText
{
    public static string Render(ProtocolSchemaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return
            "Hypa API schema\n" +
            $"protocol_name: {document.ProtocolName}\n" +
            $"protocol_major: {document.ProtocolMajor}\n" +
            $"protocol_minor: {document.ProtocolMinor}\n" +
            $"endpoint_generation: {document.EndpointGeneration}\n" +
            $"schema_version: {document.SchemaVersion}\n" +
            $"methods: {document.Methods.Count}\n" +
            $"events: {document.Events.Count}\n" +
            "\n" +
            "Use `hypa api schema --json` to print the full schema.\n";
    }
}
