using System.Runtime.InteropServices;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// AOT-safe P/Invoke surface for libghostty-vt.
/// <c>c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3</c>.
/// Uses <see cref="LibraryImportAttribute"/> with a DllImportResolver
/// from <see cref="GhosttyLibraryLoader"/>.
/// </summary>
internal static partial class GhosttyNative
{
    internal const int Success = 0;
    internal const int OutOfMemory = -1;
    internal const int InvalidValue = -2;
    internal const int OutOfSpace = -3;
    internal const int NoValue = -4;

    internal const int BuildInfoVersionString = 5;
    internal const int BuildInfoVersionBuild = 10;
    internal const int BuildInfoOptimize = 4;

    // GhosttyTerminalData
    internal const int TerminalDataCols = 1;
    internal const int TerminalDataRows = 2;
    internal const int TerminalDataCursorX = 3;
    internal const int TerminalDataCursorY = 4;
    internal const int TerminalDataActiveScreen = 6;
    internal const int TerminalDataCursorVisible = 7;
    internal const int TerminalDataMouseTracking = 11;
    internal const int TerminalDataTotalRows = 14;
    internal const int TerminalDataScrollbackRows = 15;
    internal const uint TerminalDataScrollbar = 9;
    internal const uint TerminalOptColorPalette = 14;

    internal const uint ScrollViewportTop = 0;
    internal const uint ScrollViewportBottom = 1;
    internal const uint ScrollViewportDelta = 2;
    internal const uint ScrollViewportRow = 3;

    // GhosttyTerminalScreen
    internal const uint TerminalScreenPrimary = 0;
    internal const uint TerminalScreenAlternate = 1;

    // GhosttyPointTag
    internal const uint PointTagActive = 0;
    internal const uint PointTagViewport = 1;
    internal const uint PointTagScreen = 2;
    internal const uint PointTagHistory = 3;

    // GhosttyCellContentTag
    internal const uint CellContentCodepoint = 0;
    internal const uint CellContentGrapheme = 1;
    internal const uint CellContentBgPalette = 2;
    internal const uint CellContentBgRgb = 3;

    // GhosttyCellData
    internal const uint CellDataCodepoint = 1;
    internal const uint CellDataContentTag = 2;
    internal const uint CellDataWide = 3;
    internal const uint CellDataHasText = 4;
    internal const uint CellDataHasStyling = 5;
    internal const uint CellDataStyleId = 6;
    internal const uint CellDataColorPalette = 10;
    internal const uint CellDataColorRgb = 11;

    // GhosttyCellWide
    internal const uint CellWideNarrow = 0;
    internal const uint CellWideWide = 1;
    internal const uint CellWideSpacerTail = 2;
    internal const uint CellWideSpacerHead = 3;

    // GhosttyStyleColorTag
    internal const uint StyleColorNone = 0;
    internal const uint StyleColorPalette = 1;
    internal const uint StyleColorRgb = 2;

    // GhosttyFormatterFormat
    internal const uint FormatterFormatPlain = 0;
    internal const uint FormatterFormatVt = 1;

    internal const uint DefaultCellWidthPx = 8;
    internal const uint DefaultCellHeightPx = 16;

    /// <summary>Pack a DEC private mode (bit 15 clear).</summary>
    internal static ushort ModeDec(ushort value) => (ushort)(value & 0x7FFF);

    /// <summary>Pack an ANSI mode (bit 15 set).</summary>
    internal static ushort ModeAnsi(ushort value) => (ushort)((value & 0x7FFF) | 0x8000);

    // Common modes (packed)
    internal static readonly ushort ModeInsert = ModeAnsi(4);
    internal static readonly ushort ModeCursorKeys = ModeDec(1);
    internal static readonly ushort ModeOrigin = ModeDec(6);
    internal static readonly ushort ModeWraparound = ModeDec(7);
    internal static readonly ushort ModeCursorVisible = ModeDec(25);
    internal static readonly ushort ModeNormalMouse = ModeDec(1000);
    internal static readonly ushort ModeButtonMouse = ModeDec(1002);
    internal static readonly ushort ModeAnyMouse = ModeDec(1003);
    internal static readonly ushort ModeMouseUtf8 = ModeDec(1005);
    internal static readonly ushort ModeMouseSgr = ModeDec(1006);
    internal static readonly ushort ModeMouseUrxvt = ModeDec(1015);
    internal static readonly ushort ModeMouseSgrPixels = ModeDec(1016);
    internal static readonly ushort ModeFocusEvent = ModeDec(1004);
    internal static readonly ushort ModeBracketedPaste = ModeDec(2004);
    internal static readonly ushort ModeSynchronizedOutput = ModeDec(2026);
    internal static readonly ushort ModeGraphemeCluster = ModeDec(2027);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyString
    {
        public IntPtr Ptr;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyTerminalOptions
    {
        public ushort Cols;
        public ushort Rows;
        public nuint MaxScrollback;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyColorRgb
    {
        public byte R;
        public byte G;
        public byte B;
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    internal struct GhosttyStyleColorValue
    {
        [FieldOffset(0)] public byte Palette;
        [FieldOffset(0)] public GhosttyColorRgb Rgb;
        [FieldOffset(0)] public ulong Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyStyleColor
    {
        public uint Tag;
        public GhosttyStyleColorValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyStyle
    {
        public nuint Size;
        public GhosttyStyleColor FgColor;
        public GhosttyStyleColor BgColor;
        public GhosttyStyleColor UnderlineColor;
        public byte Bold;
        public byte Italic;
        public byte Faint;
        public byte Blink;
        public byte Inverse;
        public byte Invisible;
        public byte Strikethrough;
        public byte Overline;
        public int Underline;
    }

    /// <summary>Sized grid ref (24 bytes on 64-bit).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyGridRef
    {
        public nuint Size;
        public IntPtr Node;
        public ushort X;
        public ushort Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyPointCoordinate
    {
        public ushort X;
        public uint Y;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct GhosttyPointValue
    {
        [FieldOffset(0)] public GhosttyPointCoordinate Coordinate;
        [FieldOffset(0)] public ulong Padding0;
        [FieldOffset(8)] public ulong Padding1;
    }

    /// <summary>Tagged point (24 bytes on 64-bit).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyPoint
    {
        public uint Tag;
        public GhosttyPointValue Value;
    }

    /// <summary>
    // / Scroll viewport value union.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct GhosttyTerminalScrollViewportValue
    {
        [FieldOffset(0)] public nint Delta;
        [FieldOffset(0)] public nuint Row;
    }

    /// <summary>
    // / Tagged scroll viewport.
    /// value at 8, size 24.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct GhosttyTerminalScrollViewport
    {
        [FieldOffset(0)] public uint Tag;
        [FieldOffset(8)] public GhosttyTerminalScrollViewportValue Value;
    }

    /// <summary>
    // / Scrollbar metrics.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyTerminalScrollbar
    {
        public ulong Total;
        public ulong Offset;
        public ulong Len;
    }

    /// <summary>
    /// Sized extra (32 bytes on 64-bit). Flattened screen pointer; Extra zeros stay zeros.
    /// Do not invent nested ScreenExtra.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    internal struct GhosttyFormatterTerminalExtra
    {
        public nuint Size;
        public byte Palette;
        public byte Modes;
        public byte ScrollingRegion;
        public byte Tabstops;
        public byte Pwd;
        public byte Keyboard;
        // pad to pointer alignment for screen pointer
        public ushort Pad;
        public IntPtr Screen;
    }

    [StructLayout(LayoutKind.Sequential, Size = 56)]
    internal struct GhosttyFormatterTerminalOptions
    {
        public nuint Size;
        public uint Emit;
        public byte Unwrap;
        public byte Trim;
        public GhosttyFormatterTerminalExtra Extra;
        public IntPtr Selection;
    }

    /// <summary>
    /// Sized selection (64 bytes on 64-bit: size, start GridRef, end GridRef, rectangle).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct GhosttySelection
    {
        public nuint Size;
        public GhosttyGridRef Start;
        public GhosttyGridRef End;
        public byte Rectangle;
    }

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_build_info")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_build_info(int data, IntPtr outPtr);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_new")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_terminal_new(
        IntPtr allocator,
        out IntPtr terminal,
        GhosttyTerminalOptions options);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_free")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_terminal_free(IntPtr terminal);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_vt_write")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_terminal_vt_write(IntPtr terminal, IntPtr data, nuint len);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_terminal_reset(IntPtr terminal);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_resize")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_terminal_resize(
        IntPtr terminal,
        ushort cols,
        ushort rows,
        uint cellWidthPx,
        uint cellHeightPx);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_get")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_terminal_get(IntPtr terminal, uint data, IntPtr outPtr);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_set")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_terminal_set(IntPtr terminal, uint option, IntPtr value);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_mode_get")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_terminal_mode_get(IntPtr terminal, ushort mode, out byte outValue);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_scroll_viewport")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_terminal_scroll_viewport(
        IntPtr terminal,
        GhosttyTerminalScrollViewport behavior);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_grid_ref")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_terminal_grid_ref(
        IntPtr terminal,
        GhosttyPoint point,
        ref GhosttyGridRef outRef);

    // Opaque tracked grid ref.
    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_terminal_grid_ref_track")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_terminal_grid_ref_track(
        IntPtr terminal,
        GhosttyPoint point,
        out IntPtr outRef);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_tracked_grid_ref_free")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_tracked_grid_ref_free(IntPtr trackedRef);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_tracked_grid_ref_has_value")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial byte ghostty_tracked_grid_ref_has_value(IntPtr trackedRef);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_tracked_grid_ref_point")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_tracked_grid_ref_point(
        IntPtr trackedRef,
        uint tag,
        ref GhosttyPointCoordinate outPoint);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_grid_ref_cell")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_grid_ref_cell(in GhosttyGridRef gridRef, out ulong outCell);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_grid_ref_graphemes")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_grid_ref_graphemes(
        in GhosttyGridRef gridRef,
        IntPtr buf,
        nuint bufLen,
        out nuint outLen);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_grid_ref_style")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_grid_ref_style(in GhosttyGridRef gridRef, ref GhosttyStyle style);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_cell_get")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_cell_get(ulong cell, uint data, IntPtr outPtr);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_style_default")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_style_default(ref GhosttyStyle style);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_formatter_terminal_new")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_formatter_terminal_new(
        IntPtr allocator,
        out IntPtr formatter,
        IntPtr terminal,
        GhosttyFormatterTerminalOptions options);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_formatter_format_alloc")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_formatter_format_alloc(
        IntPtr formatter,
        IntPtr allocator,
        out IntPtr outPtr,
        out nuint outLen);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_formatter_free")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_formatter_free(IntPtr formatter);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_free")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_free(IntPtr allocator, IntPtr ptr, nuint len);

    internal const int RenderStateColorsSize = 792;
    internal const uint RenderStateDirtyClean = 0;
    internal const uint RenderStateDirtyPartial = 1;
    internal const uint RenderStateDirtyFull = 2;
    internal const uint RenderStateDataDirty = 3;
    internal const uint RenderStateDataRowIterator = 4;
    internal const uint RenderStateDataCursorVisualStyle = 10;
    internal const uint RenderStateDataCursorVisible = 11;
    internal const uint RenderStateDataCursorBlinking = 12;
    internal const uint RenderStateDataCursorViewportHasValue = 14;
    internal const uint RenderStateDataCursorViewportX = 15;
    internal const uint RenderStateDataCursorViewportY = 16;
    internal const uint RenderStateCursorStyleBar = 0;
    internal const uint RenderStateCursorStyleBlock = 1;
    internal const uint RenderStateCursorStyleUnderline = 2;
    internal const uint RenderStateCursorStyleBlockHollow = 3;
    internal const uint RenderStateOptionDirty = 0;
    internal const uint RenderStateRowDataDirty = 1;
    internal const uint RenderStateRowDataCells = 3;
    internal const uint RenderStateRowOptionDirty = 0;

    // GhosttyRenderStateRowCellsData
    internal const uint RowCellsDataRaw = 1;
    internal const uint RowCellsDataStyle = 2;
    internal const uint RowCellsDataBgColor = 5;
    internal const uint RowCellsDataFgColor = 6;

    /// <summary>
    /// Packed 792-byte render-state colour struct. RGB fields are unaligned.
    /// Sequential layout would pad and corrupt the palette.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = RenderStateColorsSize, Pack = 1)]
    internal unsafe struct GhosttyRenderStateColors
    {
        [FieldOffset(0)] public nuint Size;
        [FieldOffset(8)] public GhosttyColorRgb Background;
        [FieldOffset(11)] public GhosttyColorRgb Foreground;
        [FieldOffset(14)] public GhosttyColorRgb Cursor;
        [FieldOffset(17)] public byte CursorHasValue;
        [FieldOffset(18)] public fixed byte PaletteRaw[768];

        public GhosttyColorRgb PaletteAt(int index)
        {
            if ((uint)index >= 256)
                return default;
            fixed (byte* p = PaletteRaw)
            {
                var o = index * 3;
                return new GhosttyColorRgb { R = p[o], G = p[o + 1], B = p[o + 2] };
            }
        }

        public void CopyPalette(GhosttyCellStyle.Rgb[] dest)
        {
            ArgumentNullException.ThrowIfNull(dest);
            var n = Math.Min(dest.Length, 256);
            fixed (byte* p = PaletteRaw)
            {
                for (var i = 0; i < n; i++)
                {
                    var o = i * 3;
                    dest[i] = new GhosttyCellStyle.Rgb(p[o], p[o + 1], p[o + 2]);
                }
            }
        }
    }

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_color_palette_default")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_color_palette_default(IntPtr outPalette);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_new")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_new(IntPtr allocator, out IntPtr state);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_free")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_render_state_free(IntPtr state);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_update")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_update(IntPtr state, IntPtr terminal);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_get")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_get(IntPtr state, uint data, IntPtr outPtr);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_set")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_set(IntPtr state, uint option, IntPtr value);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_colors_get")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe partial int ghostty_render_state_colors_get(
        IntPtr state,
        GhosttyRenderStateColors* outColors);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_iterator_new")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_row_iterator_new(
        IntPtr allocator,
        out IntPtr iterator);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_iterator_free")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_render_state_row_iterator_free(IntPtr iterator);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_iterator_next")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool ghostty_render_state_row_iterator_next(IntPtr iterator);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_get")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_row_get(IntPtr iterator, uint data, IntPtr outPtr);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_set")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_row_set(IntPtr iterator, uint option, IntPtr value);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_cells_new")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_row_cells_new(IntPtr allocator, out IntPtr cells);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_cells_next")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool ghostty_render_state_row_cells_next(IntPtr cells);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_cells_select")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_row_cells_select(IntPtr cells, ushort x);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_cells_get")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_row_cells_get(IntPtr cells, uint data, IntPtr outPtr);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_cells_get_multi")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ghostty_render_state_row_cells_get_multi(
        IntPtr cells,
        nuint count,
        IntPtr keys,
        IntPtr values,
        out nuint outWritten);

    [LibraryImport(GhosttyLibraryLoader.LibraryBaseName, EntryPoint = "ghostty_render_state_row_cells_free")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ghostty_render_state_row_cells_free(IntPtr cells);

    internal static GhosttyPoint ViewportPoint(ushort x, uint y) => new()
    {
        Tag = PointTagViewport,
        Value = new GhosttyPointValue
        {
            Coordinate = new GhosttyPointCoordinate { X = x, Y = y },
        },
    };

    internal static GhosttyPoint ScreenPoint(ushort x, uint y) => new()
    {
        Tag = PointTagScreen,
        Value = new GhosttyPointValue
        {
            Coordinate = new GhosttyPointCoordinate { X = x, Y = y },
        },
    };

    internal static GhosttyPoint HistoryPoint(ushort x, uint y) => new()
    {
        Tag = PointTagHistory,
        Value = new GhosttyPointValue
        {
            Coordinate = new GhosttyPointCoordinate { X = x, Y = y },
        },
    };

    internal static GhosttyGridRef EmptyGridRef() => new()
    {
        Size = (nuint)Marshal.SizeOf<GhosttyGridRef>(),
        Node = IntPtr.Zero,
        X = 0,
        Y = 0,
    };

    internal static GhosttyStyle EmptyStyle()
    {
        var style = new GhosttyStyle
        {
            Size = (nuint)Marshal.SizeOf<GhosttyStyle>(),
        };
        return style;
    }
}
