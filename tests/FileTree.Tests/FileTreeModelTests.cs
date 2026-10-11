using FileTree.Model;
using FileTree.Scanning;

namespace FileTree.Tests;

public class FileTreeModelTests
{
    private const byte FileNode = NodeFlags.InUse, DirNode = NodeFlags.InUse | NodeFlags.Directory;

    private sealed class Raw
    {
        public readonly List<(int Parent, long Alloc, string? Name, byte Flags)> Nodes = [];

        public int Add(int parent, long alloc, string? name, byte flags)
        {
            Nodes.Add((parent, alloc, name, flags));
            return Nodes.Count - 1;
        }

        public ScanData ToScanData() => new()
        {
            Count = Nodes.Count,
            Root = 0,
            Parent = Nodes.Select(n => n.Parent).ToArray(),
            Size = Nodes.Select(n => n.Alloc / 2).ToArray(),
            Alloc = Nodes.Select(n => n.Alloc).ToArray(),
            Name = Nodes.Select(n => n.Name).ToArray(),
            Flags = Nodes.Select(n => n.Flags).ToArray(),
        };
    }

    // root ─ a ─ x (10), y (400)
    //      └ z (50)
    private static Raw SmallTree()
    {
        var raw = new Raw();
        int root = raw.Add(-1, 0, null, DirNode);
        int a = raw.Add(root, 0, "a", DirNode);
        raw.Add(a, 10, "x", FileNode);
        raw.Add(a, 400, "y", FileNode);
        raw.Add(root, 50, "z", FileNode);
        return raw;
    }

    [Fact]
    public void Build_AggregatesSizesAndSortsChildrenBySize()
    {
        var model = FileTreeModel.Build(SmallTree().ToScanData(), @"C:\");

        Assert.Equal(460, model.Alloc(0));
        Assert.Equal(410, model.Alloc(1));
        Assert.Equal(230, model.Size(0));
        Assert.Equal(new[] { 1, 4 }, model.Children(0).ToArray());
        Assert.Equal(new[] { 3, 2 }, model.Children(1).ToArray());
        Assert.Equal(3, model.FileCount);
        Assert.Equal(1, model.DirectoryCount);
        Assert.Equal(2, model.Files(1));
        Assert.Equal(1, model.Files(3));
    }

    [Fact]
    public void Build_ProvidesPathsAndParents()
    {
        var model = FileTreeModel.Build(SmallTree().ToScanData(), @"C:\");

        Assert.Equal(@"C:\a\x", model.FullPath(2));
        Assert.Equal(@"C:\", model.FullPath(0));
        Assert.Equal(new[] { 0, 1, 2 }, model.PathFromRoot(2));
        Assert.Equal(-1, model.Parent(0));
        Assert.Equal(1, model.Parent(2));
        Assert.True(model.IsAncestorOrSelf(1, 2));
        Assert.False(model.IsAncestorOrSelf(4, 2));
    }

    [Fact]
    public void Build_ExcludesOrphansCyclesAndNamelessRecords()
    {
        var raw = SmallTree();
        int lost = raw.Add(99, 1, "lost", DirNode);          // parent outside the MFT
        raw.Add(lost, 2, "inside-lost", FileNode);           // reaches an orphan
        int c1 = raw.Add(-1, 4, "cycle1", DirNode);
        int c2 = raw.Add(c1, 8, "cycle2", DirNode);
        raw.Nodes[c1] = (c2, 4, "cycle1", DirNode);          // c1 <-> c2
        raw.Add(4, 0, "under-a-file", FileNode);             // parent is a file, not a directory
        raw.Add(0, 1000, "deleted", 0);                  // free record: ignored entirely
        raw.Add(0, 1019, null, FileNode);                    // in use but nameless: unattached

        var model = FileTreeModel.Build(raw.ToScanData(), @"C:\");

        Assert.Equal(5, model.OrphanCount);
        Assert.Equal(1 + 2 + 4 + 8 + 0 + 1019, model.UnattachedAlloc);
        Assert.Equal(460, model.Alloc(0));
        Assert.Equal(3, model.FileCount);
        Assert.Equal(1, model.DirectoryCount);
        Assert.Equal(new[] { 1, 4 }, model.Children(0).ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(250)]
    [InlineData(100_000)] // more than the number of files
    public void TopFiles_MatchesBruteForce(int limit)
    {
        var rng = new Random(20261010);
        var raw = new Raw();
        raw.Add(-1, 0, null, DirNode);
        var dirs = new List<int> { 0 };
        for (int i = 1; i < 3000; i++)
        {
            int parent = dirs[rng.Next(dirs.Count)];
            if (rng.NextDouble() < 0.15) dirs.Add(raw.Add(parent, 0, $"d{i}", DirNode));
            else raw.Add(parent, rng.Next(0, 400) * 8L, $"f{i}", FileNode); // many ties on purpose
        }
        var model = FileTreeModel.Build(raw.ToScanData(), @"C:\");

        int busiest = dirs.Skip(1).MaxBy(d => model.Files(d));
        foreach (int dir in new[] { 0, busiest })
        {
            var top = model.TopFiles(dir, limit);
            var expected = Enumerable.Range(0, raw.Nodes.Count)
                .Where(n => !model.IsDirectory(n) && model.IsAncestorOrSelf(dir, n))
                .Select(model.Alloc).OrderDescending().Take(limit).ToArray();

            Assert.Equal(expected, top.Select(model.Alloc).ToArray());
            Assert.All(top, n => Assert.True(!model.IsDirectory(n) && model.IsAncestorOrSelf(dir, n)));
            Assert.Equal(top.Length, top.Distinct().Count());
        }
    }
}
