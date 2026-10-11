using FileTree.Model;

namespace FileTree.UI;

/// <summary>
/// Treemap view. Rendering happens on a background thread; a newer request (root change or
/// debounced resize) cancels the older one so only the latest image is shown.
/// </summary>
internal sealed class TreemapControl : Control
{
    private readonly System.Windows.Forms.Timer _resizeTimer = new() { Interval = 120 };
    private readonly ToolTip _tip = new();
    private FileTreeModel? _model;
    private TreemapImage? _image;
    private CancellationTokenSource? _cts;
    private int _root = -1;
    private int _highlight = -1;
    private (int Node, bool IsRest)? _tipTarget;
    private string? _message = "選擇磁碟後按「掃描」";

    /// <summary>Raised with the clicked node (the parent directory for a merged block of small items).</summary>
    public event Action<int>? NodeClicked;

    public int Root => _root;

    public TreemapControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw, true);
        BackColor = TreemapRenderer.Background;
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            Rebuild();
        };
    }

    public void SetModel(FileTreeModel? model, string? message = null)
    {
        _cts?.Cancel();
        _image?.Dispose();
        _image = null;
        _model = model;
        _root = -1;
        _highlight = -1;
        _message = message;
        Invalidate();
    }

    public void ShowRoot(int node)
    {
        if (node == _root) return;
        _root = node;
        Rebuild();
    }

    public void Highlight(int node)
    {
        _highlight = node;
        Invalidate();
    }

    public int NodeAt(Point clientPoint) => HitTest(clientPoint)?.Node ?? -1;

    private async void Rebuild()
    {
        _cts?.Cancel();
        if (_model is null || _root < 0 || ClientSize.Width < 2 || ClientSize.Height < 2) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var model = _model;
        int root = _root;
        var size = ClientSize;
        if (_image is null) _message = "繪製中…";
        Invalidate();
        try
        {
            var image = await Task.Run(() => TreemapRenderer.Render(model, root, size, cts.Token));
            if (cts.IsCancellationRequested || image is null)
            {
                image?.Dispose();
                return;
            }
            _image?.Dispose();
            _image = image;
            _message = null;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _message = $"樹狀圖繪製失敗：{ex.Message}";
        }
        finally
        {
            if (_cts == cts) _cts = null;
            cts.Dispose();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_image is not null) g.DrawImageUnscaled(_image.Bitmap, 0, 0);
        else g.Clear(BackColor);

        if (_message is not null)
        {
            TextRenderer.DrawText(g, _message, Font, ClientRectangle, Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
        if (_image is not null && HighlightBounds() is { } r)
        {
            g.DrawRectangle(Pens.White, r.X, r.Y, r.Width - 1, r.Height - 1);
            if (r.Width > 4 && r.Height > 4) g.DrawRectangle(Pens.Black, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
        }
    }

    /// <summary>Bounds of the highlighted node, or of its nearest ancestor when the node itself was too small to draw.</summary>
    private RectangleF? HighlightBounds()
    {
        if (_model is null || _image is null || _highlight < 0) return null;
        for (int n = _highlight; n >= 0; n = _model.Parent(n))
            if (_image.Bounds.TryGetValue(n, out var r)) return r;
        return null;
    }

    private TreemapItem? HitTest(Point p)
    {
        if (_image is null) return null;
        var items = _image.Items;
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i].Bounds.Contains(p)) return items[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var item = HitTest(e.Location);
        var target = item is { } it ? (it.Node, it.IsRest) : ((int, bool)?)null;
        if (target == _tipTarget) return;
        _tipTarget = target;
        if (item is not { } hit || _model is null)
        {
            _tip.Hide(this);
            return;
        }
        string text = hit.IsRest
            ? $"{_model.FullPath(hit.Node)}\n（較小的項目合併顯示）"
            : $"{_model.FullPath(hit.Node)}\n{SizeFormat.Bytes(_model.Alloc(hit.Node))}";
        _tip.Show(text, this, e.X + 16, e.Y + 16);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tipTarget = null;
        _tip.Hide(this);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && HitTest(e.Location) is { } item) NodeClicked?.Invoke(item.Node);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_model is null) return;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _image?.Dispose();
            _resizeTimer.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
