using System.Drawing;
using FileTree.UI;

namespace FileTree.Tests;

public class SquarifyTests
{
    private static double Area(RectangleF r) => (double)r.Width * r.Height;

    [Fact]
    public void Layout_TilesBoundsWithProportionalNonOverlappingRects()
    {
        double[] weights = [6, 6, 4, 3, 2, 2, 1]; // example from the squarified treemap paper
        var bounds = new RectangleF(10, 20, 600, 400);
        var rects = new RectangleF[weights.Length];

        Squarify.Layout(weights, bounds, rects);

        double scale = Area(bounds) / weights.Sum();
        for (int i = 0; i < rects.Length; i++)
        {
            Assert.Equal(weights[i] * scale, Area(rects[i]), 0.5);
            Assert.True(RectangleF.Inflate(bounds, 0.01f, 0.01f).Contains(rects[i]), $"rect {i} {rects[i]} is outside the bounds");
            for (int j = i + 1; j < rects.Length; j++)
            {
                var overlap = RectangleF.Intersect(rects[i], rects[j]);
                Assert.True(Area(overlap) < 0.5, $"rects {i} and {j} overlap");
            }
        }
        Assert.Equal(Area(bounds), rects.Sum(Area), 1.0);
    }

    [Fact]
    public void Layout_GivesEmptyRectsToNonPositiveWeights()
    {
        double[] weights = [5, 0, 3, -1];
        var rects = new RectangleF[weights.Length];

        Squarify.Layout(weights, new RectangleF(0, 0, 100, 80), rects);

        Assert.Equal(5000, Area(rects[0]), 0.5);
        Assert.Equal(3000, Area(rects[2]), 0.5);
        Assert.True(rects[1].IsEmpty);
        Assert.True(rects[3].IsEmpty);
    }

    [Fact]
    public void Layout_ClearsResultWhenNothingToDraw()
    {
        var rects = new[] { new RectangleF(1, 1, 1, 1), new RectangleF(2, 2, 2, 2) };

        Squarify.Layout([0, 0], new RectangleF(0, 0, 100, 100), rects);
        Assert.All(rects, r => Assert.True(r.IsEmpty));

        rects[0] = new RectangleF(1, 1, 1, 1);
        Squarify.Layout([1, 2], new RectangleF(0, 0, 0, 100), rects);
        Assert.All(rects, r => Assert.True(r.IsEmpty));
    }
}

public class ExtensionColorsTests
{
    [Fact]
    public void For_IgnoresCaseAndFileStem()
    {
        Assert.Equal(ExtensionColors.For("movie.MP4"), ExtensionColors.For("clip.mp4"));
        Assert.Equal(ExtensionColors.For("a.tar.GZ"), ExtensionColors.For("b.gz"));
    }

    [Theory]
    [InlineData("README")]
    [InlineData(".gitignore")]
    [InlineData("trailing.")]
    public void For_ReturnsNeutralColorWithoutExtension(string name)
    {
        Assert.Equal(ExtensionColors.NoExtension, ExtensionColors.For(name));
    }
}
