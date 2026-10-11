namespace FileTree.UI;

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk, 2000).</summary>
internal static class Squarify
{
    /// <summary>
    /// Tiles <paramref name="bounds"/> with one rectangle per weight, each with area proportional to its weight.
    /// Weights should be sorted descending for the best aspect ratios; non-positive weights get an empty rectangle.
    /// </summary>
    public static void Layout(ReadOnlySpan<double> weights, RectangleF bounds, Span<RectangleF> result)
    {
        int n = weights.Length;
        double total = 0;
        foreach (double w in weights) if (w > 0) total += w;
        result[..n].Clear();
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

        double scale = (double)bounds.Width * bounds.Height / total;
        double x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height;
        int i = 0;
        while (i < n && width > 0 && height > 0)
        {
            if (weights[i] <= 0) { i++; continue; }
            double side = Math.Min(width, height);
            int start = i;
            double rowSum = 0, rowMin = double.MaxValue, rowMax = 0, best = double.MaxValue;
            // Grow the row while adding the next item does not make its worst aspect ratio worse.
            while (i < n && weights[i] > 0)
            {
                double a = weights[i] * scale;
                double sum = rowSum + a, min = Math.Min(rowMin, a), max = Math.Max(rowMax, a);
                double worst = Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
                if (i > start && worst > best) break;
                best = worst;
                rowSum = sum;
                rowMin = min;
                rowMax = max;
                i++;
            }

            double thickness = rowSum / side;
            double pos = width >= height ? y : x;
            for (int k = start; k < i; k++)
            {
                double len = weights[k] * scale / thickness;
                result[k] = width >= height
                    ? new RectangleF((float)x, (float)pos, (float)thickness, (float)len)
                    : new RectangleF((float)pos, (float)y, (float)len, (float)thickness);
                pos += len;
            }
            if (width >= height) { x += thickness; width -= thickness; }
            else { y += thickness; height -= thickness; }
        }
    }
}
