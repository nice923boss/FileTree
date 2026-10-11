using System.Drawing.Drawing2D;
using FileTree.Model;

namespace FileTree.UI;

/// <summary>
/// Expandable folder tree drawn inside a virtual, owner-drawn ListView: only visible rows are
/// materialized, so folders with tens of thousands of entries stay responsive.
/// </summary>
internal sealed class TreeListView : ListView
{
    private const int IndentWidth = 16;
    private const int GlyphWidth = 16;
    private const int IconSize = 10;
    private const int MinNameWidth = 160;
    private static readonly Color BarColor = Color.FromArgb(86, 140, 200);
    private static readonly Color BarTrack = Color.FromArgb(228, 228, 228);

    private readonly record struct Row(int Node, int Depth, bool Expanded);

    private readonly List<Row> _rows = [];
    private FileTreeModel? _model;
    private bool _suppressSelection;

    /// <summary>Raised when the user selects a row (not when the selection is set programmatically).</summary>
    public event Action<int>? NodeSelected;

    public TreeListView()
    {
        View = View.Details;
        VirtualMode = true;
        OwnerDraw = true;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        DoubleBuffered = true;
        Columns.Add("名稱", 330);
        Columns.Add("佔用空間", 85, HorizontalAlignment.Right);
        Columns.Add("大小", 85, HorizontalAlignment.Right);
        Columns.Add("佔上層比例", 150);
        Columns.Add("檔案數", 80, HorizontalAlignment.Right);
    }

    public int SelectedNode => SelectedRow >= 0 ? _rows[SelectedRow].Node : -1;

    private int SelectedRow => SelectedIndices.Count > 0 ? SelectedIndices[0] : -1;

    public void SetModel(FileTreeModel? model)
    {
        _model = model;
        _rows.Clear();
        if (model is not null)
        {
            _rows.Add(new Row(model.Root, 0, false));
            ExpandRows(0);
        }
        Rebind(model is null ? -1 : 0);
    }

    /// <summary>Expands the ancestors of <paramref name="node"/> and selects it without raising <see cref="NodeSelected"/>.</summary>
    public void Reveal(int node)
    {
        if (_model is null || _rows.Count == 0) return;
        var chain = _model.PathFromRoot(node);
        int index = 0;
        for (int k = 1; k < chain.Count; k++)
        {
            ExpandRows(index);
            int depth = _rows[index].Depth + 1;
            int i = index + 1;
            while (i < _rows.Count && _rows[i].Depth >= depth && (_rows[i].Depth != depth || _rows[i].Node != chain[k])) i++;
            if (i >= _rows.Count || _rows[i].Node != chain[k]) break;
            index = i;
        }
        Rebind(index);
        EnsureVisible(index);
    }

    private bool IsExpandable(int node) => _model!.IsDirectory(node) && _model.ChildCount(node) > 0;

    /// <summary>Inserts the children of an expandable row. Returns the number of rows added.</summary>
    private int ExpandRows(int index)
    {
        var row = _rows[index];
        if (row.Expanded || !IsExpandable(row.Node)) return 0;
        var children = _model!.Children(row.Node);
        var added = new Row[children.Length];
        for (int i = 0; i < added.Length; i++) added[i] = new Row(children[i], row.Depth + 1, false);
        _rows[index] = row with { Expanded = true };
        _rows.InsertRange(index + 1, added);
        return added.Length;
    }

    private void Expand(int index)
    {
        int sel = SelectedRow;
        int added = ExpandRows(index);
        if (added > 0) Rebind(sel > index ? sel + added : sel);
    }

    private void Collapse(int index)
    {
        var row = _rows[index];
        if (!row.Expanded) return;
        int end = index + 1;
        while (end < _rows.Count && _rows[end].Depth > row.Depth) end++;
        int removed = end - index - 1;
        int sel = SelectedRow;
        _rows[index] = row with { Expanded = false };
        _rows.RemoveRange(index + 1, removed);
        bool selectionHidden = sel > index && sel < end;
        Rebind(sel <= index ? sel : selectionHidden ? index : sel - removed);
        if (selectionHidden) NodeSelected?.Invoke(row.Node);
    }

    private void Toggle(int index)
    {
        if (_rows[index].Expanded) Collapse(index);
        else Expand(index);
    }

    /// <summary>Re-syncs the virtual list with <see cref="_rows"/> and selects a row without raising <see cref="NodeSelected"/>.</summary>
    private void Rebind(int selectIndex)
    {
        _suppressSelection = true;
        try
        {
            SelectedIndices.Clear();
            VirtualListSize = _rows.Count;
            FitNameColumn(); // the vertical scroll bar may have appeared or gone
            if (selectIndex >= 0 && selectIndex < _rows.Count)
            {
                var item = Items[selectIndex];
                item.Selected = true;
                item.Focused = true;
            }
        }
        finally
        {
            _suppressSelection = false;
        }
        Invalidate();
    }

    private void SelectRow(int index)
    {
        SelectedIndices.Clear();
        Items[index].Selected = true;
        Items[index].Focused = true;
        EnsureVisible(index);
    }

    private int ParentRow(int index)
    {
        int depth = _rows[index].Depth;
        for (int i = index - 1; i >= 0; i--)
            if (_rows[i].Depth < depth) return i;
        return index;
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate(); // owner-drawn full-row selection: repaint every column, not just the first
        if (!_suppressSelection && SelectedNode >= 0) NodeSelected?.Invoke(SelectedNode);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int sel = SelectedRow;
        if (_model is not null && sel >= 0)
        {
            var row = _rows[sel];
            switch (e.KeyCode)
            {
                case Keys.Right:
                    if (!row.Expanded) Expand(sel);
                    else if (sel + 1 < _rows.Count && _rows[sel + 1].Depth > row.Depth) SelectRow(sel + 1);
                    e.Handled = true;
                    break;
                case Keys.Left:
                    if (row.Expanded) Collapse(sel);
                    else if (row.Depth > 0) SelectRow(ParentRow(sel));
                    e.Handled = true;
                    break;
                case Keys.Enter:
                    Toggle(sel);
                    e.Handled = true;
                    break;
            }
        }
        base.OnKeyDown(e);
    }

    private bool IsOnGlyph(Point p, out int index)
    {
        index = HitTest(p).Item?.Index ?? -1;
        if (_model is null || index < 0 || index >= _rows.Count) return false;
        var row = _rows[index];
        int glyphX = GetItemRect(index).X + 2 + row.Depth * IndentWidth;
        return IsExpandable(row.Node) && p.X >= glyphX && p.X < glyphX + GlyphWidth;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && IsOnGlyph(e.Location, out int index)) Toggle(index);
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || IsOnGlyph(e.Location, out _)) return;
        int index = HitTest(e.Location).Item?.Index ?? -1;
        if (index >= 0 && index < _rows.Count) Toggle(index);
    }

    /// <summary>
    /// Keeps the row no wider than the view: with full-row select, a row wider than the client area makes
    /// the native list scroll horizontally on selection, hiding the expand glyphs.
    /// </summary>
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        FitNameColumn();
    }

    private void FitNameColumn()
    {
        int others = 0;
        for (int i = 1; i < Columns.Count; i++) others += Columns[i].Width;
        if (Columns.Count > 0) Columns[0].Width = Math.Max(MinNameWidth, ClientSize.Width - others);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
    {
        if (_model is null || e.ItemIndex >= _rows.Count)
        {
            e.Item = new ListViewItem(["", "", "", "", ""]);
            return;
        }
        int node = _rows[e.ItemIndex].Node;
        e.Item = new ListViewItem([_model.Name(node), SizeFormat.Bytes(_model.Alloc(node)), SizeFormat.Bytes(_model.Size(node)),
            ShareText(node), _model.IsDirectory(node) ? _model.Files(node).ToString("N0") : ""]);
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) => e.DrawDefault = true;

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        // Cells are painted in OnDrawSubItem.
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (_model is null || e.ItemIndex < 0 || e.ItemIndex >= _rows.Count || e.Item is null) return;
        var row = _rows[e.ItemIndex];
        int node = row.Node;
        var g = e.Graphics;
        var bounds = e.Bounds;
        // In details view the first sub-item reports the whole row as its bounds.
        if (e.ColumnIndex == 0) bounds = new Rectangle(bounds.X, bounds.Y, Columns[0].Width, bounds.Height);

        bool selected = e.Item.Selected;
        Color back = !selected ? BackColor : Focused ? SystemColors.Highlight : Color.FromArgb(217, 217, 217);
        Color fore = selected && Focused ? SystemColors.HighlightText : ForeColor;
        using (var brush = new SolidBrush(back)) g.FillRectangle(brush, bounds);

        switch (e.ColumnIndex)
        {
            case 0:
                DrawName(g, bounds, row, fore);
                break;
            case 3:
                DrawShare(g, bounds, node, fore);
                break;
            default:
                string text = e.ColumnIndex switch
                {
                    1 => SizeFormat.Bytes(_model.Alloc(node)),
                    2 => SizeFormat.Bytes(_model.Size(node)),
                    _ => _model.IsDirectory(node) ? _model.Files(node).ToString("N0") : "",
                };
                var cell = Rectangle.Inflate(bounds, -4, 0);
                TextRenderer.DrawText(g, text, Font, cell, fore,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                break;
        }
    }

    private void DrawName(Graphics g, Rectangle bounds, Row row, Color fore)
    {
        var model = _model!;
        int mid = bounds.Y + bounds.Height / 2;
        int x = bounds.X + 2 + row.Depth * IndentWidth;
        if (IsExpandable(row.Node))
        {
            Point[] glyph = row.Expanded
                ? [new(x + 3, mid - 2), new(x + 11, mid - 2), new(x + 7, mid + 3)]
                : [new(x + 5, mid - 4), new(x + 10, mid), new(x + 5, mid + 4)];
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(fore == ForeColor ? Color.DimGray : fore)) g.FillPolygon(brush, glyph);
            g.SmoothingMode = oldMode;
        }
        x += GlyphWidth;

        var color = model.IsDirectory(row.Node) ? ExtensionColors.Folder : ExtensionColors.For(model.Name(row.Node));
        var icon = new Rectangle(x, mid - IconSize / 2, IconSize, IconSize);
        using (var brush = new SolidBrush(color)) g.FillRectangle(brush, icon);
        using (var pen = new Pen(Color.FromArgb(color.R * 3 / 4, color.G * 3 / 4, color.B * 3 / 4)))
            g.DrawRectangle(pen, icon.X, icon.Y, icon.Width - 1, icon.Height - 1);
        x += IconSize + 5;

        var textRect = new Rectangle(x, bounds.Y, Math.Max(0, bounds.Right - x), bounds.Height);
        TextRenderer.DrawText(g, model.Name(row.Node), Font, textRect, fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix);
    }

    private double Share(int node)
    {
        int parent = _model!.Parent(node);
        if (parent < 0) return 1;
        long total = _model.Alloc(parent);
        return total > 0 ? (double)_model.Alloc(node) / total : 0;
    }

    private string ShareText(int node) => $"{Share(node) * 100:F1} %";

    private void DrawShare(Graphics g, Rectangle bounds, int node, Color fore)
    {
        const int TextWidth = 52;
        var bar = new Rectangle(bounds.X + 4, bounds.Y + 4, bounds.Width - TextWidth - 8, bounds.Height - 8);
        if (bar.Width > 4 && bar.Height > 2)
        {
            using (var track = new SolidBrush(BarTrack)) g.FillRectangle(track, bar);
            int filled = (int)Math.Round(bar.Width * Share(node));
            if (filled > 0)
                using (var fill = new SolidBrush(BarColor)) g.FillRectangle(fill, bar.X, bar.Y, filled, bar.Height);
        }
        var textRect = new Rectangle(bounds.Right - TextWidth - 4, bounds.Y, TextWidth, bounds.Height);
        TextRenderer.DrawText(g, ShareText(node), Font, textRect, fore,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
