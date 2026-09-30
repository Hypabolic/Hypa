namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Per-frame glyph table. Index 0 to 127 is the shared ASCII prefix from
/// <see cref="VtGlyphIntern"/>. A glyph above ASCII goes to an extras list
/// with a reused dictionary lookup. A linear scan costs O(n^2) on a full
/// CJK screen.
/// </summary>
public sealed class VtGlyphTable
{
    private readonly List<string> _extras = new();
    private readonly Dictionary<string, int> _extraIndex = new(StringComparer.Ordinal);

    public int ExtraCount => _extras.Count;

    public void Clear()
    {
        _extras.Clear();
        _extraIndex.Clear();
    }

    public int Intern(string glyph)
    {
        if (glyph.Length == 1 && glyph[0] <= 127)
            return glyph[0];
        var ascii = VtGlyphIntern.AsciiIndex(glyph);
        if (ascii >= 0)
            return ascii;
        if (_extraIndex.TryGetValue(glyph, out var found))
            return 128 + found;
        var index = _extras.Count;
        if (128 + index > VtFrameCell.MaxGlyphIndex)
            return 32;
        _extras.Add(glyph);
        _extraIndex[glyph] = index;
        return 128 + index;
    }

    public string Resolve(int index)
    {
        if ((uint)index < 128u)
            return VtGlyphIntern.FromIndex(index);
        var extra = index - 128;
        if ((uint)extra < (uint)_extras.Count)
            return _extras[extra];
        return " ";
    }

    internal IReadOnlyList<string> Extras => _extras;
}

/// <summary>
/// Per-frame hyperlink table. The cell holds an index, not a string.
/// <c>hyperlink_indices.entry(uri).or_insert_with(...)</c>. Hypa copies
/// that dedupe: a repeated URI returns the first index.
/// </summary>
public sealed class VtHyperlinkTable
{
    private readonly List<string> _uris = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public int Count => _uris.Count;

    public void Clear()
    {
        _uris.Clear();
        _index.Clear();
    }

    public int Intern(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return -1;
        if (_index.TryGetValue(uri, out var found))
            return found;
        if (_uris.Count > VtFrameCell.MaxHyperlinkIndex)
            return -1;
        var index = _uris.Count;
        _uris.Add(uri);
        _index[uri] = index;
        return index;
    }

    public string? Resolve(int index)
    {
        if ((uint)index < (uint)_uris.Count)
            return _uris[index];
        return null;
    }

    internal IReadOnlyList<string> Uris => _uris;
}

/// <summary>
/// Mutable builder form. Reused for each pane, like the live style table.
/// <see cref="BeginFrame"/> clears all three tables.
/// </summary>
public sealed class VtStampTables
{
    public VtStyleTable Styles { get; } = new();

    public VtGlyphTable Glyphs { get; } = new();

    public VtHyperlinkTable Links { get; } = new();

    public void BeginFrame()
    {
        Styles.BeginFrame();
        Glyphs.Clear();
        Links.Clear();
    }

    public VtFrameCell PackCell(
        string text,
        int width,
        bool isContinuation,
        in VtPackedStyle style,
        string? hyperlink)
    {
        var styleIndex = Styles.Intern(in style);
        var glyphIndex = Glyphs.Intern(string.IsNullOrEmpty(text) ? " " : text);
        var linkIndex = Links.Intern(hyperlink);
        return VtFrameCell.Pack(glyphIndex, styleIndex, linkIndex, width, isContinuation);
    }

    public VtFrameTables Freeze(VtFrameTables? previous)
    {
        var styles = Styles.CopyEntries();
        string[] glyphExtras = Glyphs.ExtraCount == 0 ? [] : Glyphs.Extras.ToArray();
        string[] links = Links.Count == 0 ? [] : Links.Uris.ToArray();
        if (previous is not null
            && previous.StylesEqual(styles)
            && previous.GlyphExtrasEqual(glyphExtras)
            && previous.HyperlinksEqual(links))
        {
            return previous;
        }

        return new VtFrameTables(styles, glyphExtras, links);
    }
}

/// <summary>
// / Frozen immutable form.
/// <c>cells</c>, <c>width</c>, <c>height</c>, and <c>hyperlinks</c> in one
/// value. Hypa adds a glyph table and a style table beside those, because
/// the Hypa cell holds indexes and not values. The shared ASCII prefix is
/// static and is not copied. The stamp buffer must never be retained,
/// because the next stamp overwrites it.
/// </summary>
public sealed class VtFrameTables
{
    public static VtFrameTables Empty { get; } = new(
        [new VtPackedStyle(0, 0, 0, 0, 0)], [], []);

    private readonly VtPackedStyle[] _styles;
    private readonly string[] _glyphExtras;
    private readonly string[] _hyperlinks;

    public VtFrameTables(VtPackedStyle[] styles, string[] glyphExtras, string[] hyperlinks)
    {
        _styles = styles.Length == 0 ? [new VtPackedStyle(0, 0, 0, 0, 0)] : styles;
        _glyphExtras = glyphExtras;
        _hyperlinks = hyperlinks;
    }

    public int StyleCount => _styles.Length;

    public int HyperlinkCount => _hyperlinks.Length;

    public int GlyphExtraCount => _glyphExtras.Length;

    public VtPackedStyle StyleAt(int index)
    {
        if ((uint)index < (uint)_styles.Length)
            return _styles[index];
        return default;
    }

    public string GlyphAt(int index)
    {
        if ((uint)index < 128u)
            return VtGlyphIntern.FromIndex(index);
        var extra = index - 128;
        if ((uint)extra < (uint)_glyphExtras.Length)
            return _glyphExtras[extra];
        return " ";
    }

    public string? HyperlinkAt(int index)
    {
        if ((uint)index < (uint)_hyperlinks.Length)
            return _hyperlinks[index];
        return null;
    }

    public IReadOnlyList<string> Hyperlinks => _hyperlinks;

    public VtCellView Resolve(in VtFrameCell cell)
    {
        var style = StyleAt(cell.StyleIndex);
        return new VtCellView(
            GlyphAt(cell.GlyphIndex),
            cell.Width,
            cell.IsContinuation,
            style.Fg,
            style.Bg,
            style.Modifier,
            style.UnderlineColor,
            style.UnderlineStyle,
            cell.HyperlinkIndex < 0 ? null : HyperlinkAt(cell.HyperlinkIndex),
            (ushort)Math.Min(cell.StyleIndex, ushort.MaxValue));
    }

    internal bool StylesEqual(VtPackedStyle[] styles)
    {
        if (_styles.Length != styles.Length)
            return false;
        for (var i = 0; i < _styles.Length; i++)
        {
            if (!_styles[i].Equals(styles[i]))
                return false;
        }

        return true;
    }

    internal bool GlyphExtrasEqual(string[] extras)
    {
        if (_glyphExtras.Length != extras.Length)
            return false;
        for (var i = 0; i < _glyphExtras.Length; i++)
        {
            if (!string.Equals(_glyphExtras[i], extras[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    internal bool HyperlinksEqual(string[] links)
    {
        if (_hyperlinks.Length != links.Length)
            return false;
        for (var i = 0; i < _hyperlinks.Length; i++)
        {
            if (!string.Equals(_hyperlinks[i], links[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
