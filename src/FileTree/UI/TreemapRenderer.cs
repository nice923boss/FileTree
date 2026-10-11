using System.Drawing.Imaging;
using FileTree.Model;

namespace FileTree.UI;

/// <summary>A drawn treemap cell. <see cref="IsRest"/> marks the merged block of a directory's too-small children.</summary>
internal readonly record struct TreemapItem(RectangleF Bounds, int Node, bool IsRest);

internal sealed class TreemapImage(Bitmap bitmap, List<TreemapItem> items, Dictionary<int, RectangleF> bounds) : IDisposable
{
    public Bitmap Bitmap { get; } = bitmap;
    /// <summary>Cells in drawing order (parents before children), so a reverse scan finds the deepest hit.</summary>
    public List<TreemapItem> Items { get; } = items;
    /// <summary>Bounds of every drawn node (rest blocks excluded).</summary>
    public Dictionary<int, RectangleF> Bounds { get; } = bounds;

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Draws a subtree as a squarified treemap. Directories show as 1 px dark frames around their
/// children; files are filled with their extension color. Children whose area would be below
/// <see cref="MinArea"/> are merged into one grey block. Runs on a background thread.
/// </summary>
internal static class TreemapRenderer
{
    private const double MinArea = 6; // px²
    public static readonly Color Background = Color.FromArgb(48, 48, 48);
    private static readonly Color RestColor = Color.FromArgb(112, 112, 112);

    public static TreemapImage? Render(FileTreeModel model, int root, Size size, CancellationToken ct)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        var painter = new Painter(model, ct);
        try
        {
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Background);
                painter.Graphics = g;
                painter.Draw(root, new RectangleF(0, 0, size.Width, size.Height));
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            painter.DisposeBrushes();
        }
        if (ct.IsCancellationRequested)
        {
            bitmap.Dispose();
            return null;
        }
        return new TreemapImage(bitmap, painter.Items, painter.Bounds);
    }

    private sealed class Painter(FileTreeModel model, CancellationToken ct)
    {
        private readonly Dictionary<Color, SolidBrush> _brushes = [];
        private readonly Dictionary<Color, Pen> _pens = [];
        public Graphics Graphics = null!;
        public readonly List<TreemapItem> Items = [];
        public readonly Dictionary<int, RectangleF> Bounds = [];

        public void Draw(int node, RectangleF rect)
        {
            if (ct.IsCancellationRequested) return;
            var cell = Snap(rect);
            if (cell.Width <= 0 || cell.Height <= 0) return;
            Items.Add(new TreemapItem(cell, node, false));
            Bounds[node] = cell;

            if (!model.IsDirectory(node))
            {
                var color = ExtensionColors.For(model.Name(node));
                Graphics.FillRectangle(Brush(color), cell);
                if (cell.Width >= 4 && cell.Height >= 4)
                    Graphics.DrawRectangle(Pen(Color.FromArgb(color.R * 3 / 4, color.G * 3 / 4, color.B * 3 / 4)),
                        cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
                return;
            }

            var inner = RectangleF.Inflate(cell, -1, -1);
            long total = model.Alloc(node);
            if (inner.Width < 1 || inner.Height < 1 || total <= 0) return;

            double scale = inner.Width * inner.Height / total;
            var children = model.Children(node);
            int count = 0;
            long shown = 0;
            foreach (int child in children)
            {
                long a = model.Alloc(child);
                if (a * scale < MinArea) break; // children are sorted by size, the rest are smaller
                count++;
                shown += a;
            }
            long rest = total - shown;
            bool hasRest = rest * scale >= 1;
            int k = count + (hasRest ? 1 : 0);
            if (k == 0) return;

            var weights = new double[k];
            var rects = new RectangleF[k];
            for (int i = 0; i < count; i++) weights[i] = model.Alloc(children[i]);
            if (hasRest) weights[count] = rest;
            Squarify.Layout(weights, inner, rects);

            for (int i = 0; i < count; i++) Draw(children[i], rects[i]);
            if (hasRest)
            {
                var restCell = Snap(rects[count]);
                if (restCell.Width <= 0 || restCell.Height <= 0) return;
                Graphics.FillRectangle(Brush(RestColor), restCell);
                Items.Add(new TreemapItem(restCell, node, true));
            }
        }

        /// <summary>Rounds edges to whole pixels so neighbouring cells neither overlap nor leave gaps.</summary>
        private static RectangleF Snap(RectangleF r)
        {
            float left = MathF.Round(r.Left), top = MathF.Round(r.Top);
            return new RectangleF(left, top, MathF.Round(r.Right) - left, MathF.Round(r.Bottom) - top);
        }

        private SolidBrush Brush(Color c)
        {
            if (!_brushes.TryGetValue(c, out var b)) _brushes[c] = b = new SolidBrush(c);
            return b;
        }

        private Pen Pen(Color c)
        {
            if (!_pens.TryGetValue(c, out var p)) _pens[c] = p = new Pen(c);
            return p;
        }

        public void DisposeBrushes()
        {
            foreach (var b in _brushes.Values) b.Dispose();
            foreach (var p in _pens.Values) p.Dispose();
        }
    }
}
