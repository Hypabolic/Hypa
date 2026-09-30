using Hypa.AgentRuntime.Application;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Cli.Attach.Cubes;

public enum CubePairingDialogKind
{
    None,
    Share,
    Add,
}

public sealed class CubeShareState
{
    public const string AdvertiseHostVariable = "HYPA_ADVERTISE_HOST";
    public const string DefaultBindHost = "0.0.0.0";
    public const string DefaultPort = "7443";

    public string BindHost { get; set; } = DefaultBindHost;
    public string AdvertiseHost { get; set; } = "";

    /// <summary>First address row shown when the list does not fit.</summary>
    public int ReachScroll { get; set; }
    public IReadOnlyList<string> AdvertisedHosts { get; set; } = [];
    public string Port { get; set; } = DefaultPort;
    public string Invite { get; set; } = "";
    public string? Error { get; set; }
    public bool Running { get; set; }
    public bool Starting { get; set; }
    public bool ShareEnabled { get; set; } = true;
    public bool Copied { get; set; }
    public bool Advanced { get; set; }
    public bool Paired { get; set; }
    public bool PairedShown { get; set; }
    public DateTimeOffset? PairedAt { get; set; }
    public int Focus { get; set; }
}

public sealed class CubeAddState
{
    public string Invite { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Error { get; set; }
    public bool Submitting { get; set; }
    public int Focus { get; set; }
}

public sealed class CubePairingDialogModel
{
    public CubePairingDialogKind Kind { get; private set; }
    public CubeShareState? Share { get; private set; }
    public CubeAddState? Add { get; private set; }
    public CubePairingDialogLayout? Layout { get; set; }
    public CubePairingDialogSelection Selection { get; } = new();
    public bool IgnoreNextRelease { get; set; }
    public CellRect PanelBounds { get; set; }
    public char[]? PanelGlyphs { get; set; }

    public bool IsOpen => Kind is not CubePairingDialogKind.None;

    private IReadOnlyList<string>? _interfaceAddresses;

    /// <summary>Recompute the address preview from the bind and host fields.</summary>
    private void RefreshReach()
    {
        if (Share is null || _interfaceAddresses is null)
            return;
        var port = int.TryParse(Share.Port, out var parsed) ? parsed : 7443;
        var named = string.IsNullOrWhiteSpace(Share.AdvertiseHost) ? null : Share.AdvertiseHost;
        var resolved = HostInviteAdvertisement.Resolve(
            _interfaceAddresses,
            named,
            Share.BindHost,
            port);
        Share.AdvertisedHosts = resolved.Ok && resolved.Value is not null
            ? resolved.Value
            : [];
        Share.ReachScroll = 0;
    }

    public void OpenShare(
        bool enabled,
        CubeShareState? restore = null,
        string? advertiseEnvironment = null,
        IReadOnlyList<string>? interfaceAddresses = null)
    {
        Kind = CubePairingDialogKind.Share;
        Share = restore ?? new CubeShareState();
        Share.ShareEnabled = enabled;
        if (string.IsNullOrWhiteSpace(Share.AdvertiseHost)
            && !string.IsNullOrWhiteSpace(advertiseEnvironment))
        {
            // An invalid value stays in the field. The share then stops with
            // the reason, and does not fall back to every address.
            Share.AdvertiseHost = HostInviteReach.TryValidate(advertiseEnvironment, 7443, out var fromEnv, out _)
                ? fromEnv
                : advertiseEnvironment.Trim();
        }

        if (string.IsNullOrWhiteSpace(Share.BindHost))
            Share.BindHost = CubeShareState.DefaultBindHost;
        if (string.IsNullOrWhiteSpace(Share.Port))
            Share.Port = CubeShareState.DefaultPort;
        _interfaceAddresses = interfaceAddresses;
        RefreshReach();

        Add = null;
        Selection.Clear();
        IgnoreNextRelease = false;
        PanelGlyphs = null;
    }

    public void OpenAdd()
    {
        Kind = CubePairingDialogKind.Add;
        Add = new CubeAddState();
        Share = null;
        Selection.Clear();
        IgnoreNextRelease = false;
        PanelGlyphs = null;
    }

    public bool Cancel()
    {
        if (!IsOpen)
            return false;
        Kind = CubePairingDialogKind.None;
        Share = null;
        Add = null;
        Layout = null;
        Selection.Clear();
        IgnoreNextRelease = false;
        PanelGlyphs = null;
        return true;
    }

    public bool TakeIgnoreNextRelease()
    {
        if (!IgnoreNextRelease)
            return false;
        IgnoreNextRelease = false;
        return true;
    }

    public void CaptureGlyphs(CellRect panel, int col, int row, string text)
    {
        if (panel.Cols <= 0 || panel.Rows <= 0)
            return;
        if (PanelGlyphs is null
            || PanelBounds.Col != panel.Col
            || PanelBounds.Row != panel.Row
            || PanelBounds.Cols != panel.Cols
            || PanelBounds.Rows != panel.Rows)
        {
            PanelBounds = panel;
            PanelGlyphs = new char[panel.Cols * panel.Rows];
            Array.Fill(PanelGlyphs, ' ');
        }

        if (row < panel.Row || row >= panel.EndRow)
            return;
        var max = Math.Min(text.Length, panel.EndCol - col);
        for (var i = 0; i < max; i++)
        {
            var x = col + i;
            if (x < panel.Col || x >= panel.EndCol)
                continue;
            PanelGlyphs[(row - panel.Row) * panel.Cols + (x - panel.Col)] = text[i];
        }
    }

    public string ExtractSelection()
    {
        if (!Selection.Active || PanelGlyphs is null || PanelBounds.Cols <= 0)
            return "";
        CubePairingDialogSelection.Normalize(
            Selection.AnchorCol,
            Selection.AnchorRow,
            Selection.EndCol,
            Selection.EndRow,
            out var c1,
            out var r1,
            out var c2,
            out var r2);
        var lines = new List<string>();
        for (var row = r1; row <= r2; row++)
        {
            if (row < PanelBounds.Row || row >= PanelBounds.EndRow)
                continue;
            var startCol = row == r1 ? c1 : PanelBounds.Col;
            var endCol = row == r2 ? c2 : PanelBounds.EndCol - 1;
            startCol = Math.Clamp(startCol, PanelBounds.Col, PanelBounds.EndCol - 1);
            endCol = Math.Clamp(endCol, PanelBounds.Col, PanelBounds.EndCol - 1);
            if (endCol < startCol)
                continue;
            var offset = (row - PanelBounds.Row) * PanelBounds.Cols + (startCol - PanelBounds.Col);
            var length = endCol - startCol + 1;
            lines.Add(new string(PanelGlyphs, offset, length).TrimEnd());
        }

        return string.Join('\n', lines);
    }

    public bool ToggleShareAdvanced()
    {
        if (Share is null)
            return false;
        Share.Advanced = !Share.Advanced;
        if (!Share.Advanced)
            Share.Focus = 0;
        Selection.Clear();
        Layout = null;
        return true;
    }

    public string FocusedField()
    {
        if (Kind is CubePairingDialogKind.Share && Share is { Advanced: true } share)
        {
            return share.Focus switch
            {
                1 => nameof(CubeShareState.AdvertiseHost),
                2 => nameof(CubeShareState.Port),
                _ => nameof(CubeShareState.BindHost),
            };
        }

        if (Kind is CubePairingDialogKind.Add && Add is { } add)
            return add.Focus == 1 ? nameof(CubeAddState.Label) : nameof(CubeAddState.Invite);
        return "";
    }

    public void MoveFocus(int delta)
    {
        if (Kind is CubePairingDialogKind.Share && Share is { Advanced: true } share)
            share.Focus = (share.Focus + delta + 3) % 3;
        else if (Kind is CubePairingDialogKind.Add && Add is { } add)
            add.Focus = (add.Focus + delta + 2) % 2;
    }

    public void TypeIntoFocus(string text)
    {
        if (Kind is CubePairingDialogKind.Share && Share is { Advanced: true } share)
        {
            switch (share.Focus)
            {
                case 1:
                    share.AdvertiseHost += text;
                    break;
                case 2:
                    share.Port += text;
                    break;
                default:
                    share.BindHost += text;
                    break;
            }

            RefreshReach();
            return;
        }

        if (Kind is CubePairingDialogKind.Add && Add is { } add)
        {
            if (add.Focus == 1)
                add.Label += text;
            else
                add.Invite += text;
        }
    }

    public void BackspaceFocus()
    {
        if (Kind is CubePairingDialogKind.Share && Share is { Advanced: true } share)
        {
            switch (share.Focus)
            {
                case 1:
                    share.AdvertiseHost = TrimLast(share.AdvertiseHost);
                    break;
                case 2:
                    share.Port = TrimLast(share.Port);
                    break;
                default:
                    share.BindHost = TrimLast(share.BindHost);
                    break;
            }

            RefreshReach();
            return;
        }

        if (Kind is CubePairingDialogKind.Add && Add is { } add)
        {
            if (add.Focus == 1)
                add.Label = TrimLast(add.Label);
            else
                add.Invite = TrimLast(add.Invite);
        }
    }

    private static string TrimLast(string value) =>
        value.Length == 0 ? value : value[..^1];
}

public sealed class CubePairingDialogSelection
{
    public bool Active { get; private set; }
    public int AnchorCol { get; private set; }
    public int AnchorRow { get; private set; }
    public int EndCol { get; private set; }
    public int EndRow { get; private set; }

    public bool PointerDown { get; private set; }

    public bool Dragged =>
        Active && (AnchorCol != EndCol || AnchorRow != EndRow);

    public void Begin(int col, int row)
    {
        AnchorCol = EndCol = col;
        AnchorRow = EndRow = row;
        Active = true;
        PointerDown = true;
    }

    public void Extend(int col, int row)
    {
        if (!Active)
            Begin(col, row);
        EndCol = col;
        EndRow = row;
    }

    public void EndPointer()
    {
        PointerDown = false;
    }

    public void Clear()
    {
        Active = false;
        PointerDown = false;
    }

    public bool Contains(int col, int row)
    {
        if (!Active)
            return false;
        Normalize(AnchorCol, AnchorRow, EndCol, EndRow, out var c1, out var r1, out var c2, out var r2);
        if (row < r1 || row > r2)
            return false;
        if (row == r1 && col < c1)
            return false;
        if (row == r2 && col > c2)
            return false;
        return true;
    }

    public static void Normalize(
        int aCol,
        int aRow,
        int bCol,
        int bRow,
        out int c1,
        out int r1,
        out int c2,
        out int r2)
    {
        if (aRow < bRow || (aRow == bRow && aCol <= bCol))
        {
            c1 = aCol;
            r1 = aRow;
            c2 = bCol;
            r2 = bRow;
            return;
        }

        c1 = bCol;
        r1 = bRow;
        c2 = aCol;
        r2 = aRow;
    }
}

public sealed record CubePairingDialogLayout(
    CellRect Panel,
    CellRect Primary,
    CellRect Copy,
    CellRect Cancel,
    CellRect Advanced,
    CellRect Invite);
