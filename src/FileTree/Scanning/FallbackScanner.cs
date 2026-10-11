using System.IO.Enumeration;

namespace FileTree.Scanning;

/// <summary>
/// Slow path for non-NTFS volumes or when administrator rights are unavailable:
/// parallel directory enumeration. Allocated size is approximated by rounding up to the cluster size,
/// hard links are counted once per link, and junctions / symlinks are not followed.
/// </summary>
internal static class FallbackScanner
{
    private readonly record struct Entry(string Name, bool IsDirectory, long Length, bool IsReparsePoint);

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static ScanData Scan(string rootPath, ScanState state, CancellationToken ct)
    {
        var builder = new Builder(GetClusterSize(rootPath));
        int root = builder.AddRoot(rootPath);
        ScanDirectory(builder, root, rootPath, state, ct);
        return builder.ToScanData(root);
    }

    private static void ScanDirectory(Builder builder, int index, string path, ScanState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entries = new List<Entry>();
        try
        {
            var enumerable = new FileSystemEnumerable<Entry>(path,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length,
                    (e.Attributes & FileAttributes.ReparsePoint) != 0),
                Options);
            foreach (var entry in enumerable) entries.Add(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref state.Skipped);
        }

        int first = builder.AddChildren(index, entries);
        Interlocked.Add(ref state.Done, entries.Count);

        var subdirs = new List<(int Index, string Path)>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsDirectory && !entries[i].IsReparsePoint)
                subdirs.Add((first + i, Path.Join(path, entries[i].Name)));
        }
        if (subdirs.Count == 1)
            ScanDirectory(builder, subdirs[0].Index, subdirs[0].Path, state, ct);
        else if (subdirs.Count > 1)
            Parallel.ForEach(subdirs, new ParallelOptions { CancellationToken = ct },
                d => ScanDirectory(builder, d.Index, d.Path, state, ct));
    }

    private static long GetClusterSize(string rootPath)
    {
        string root = Path.GetPathRoot(rootPath) ?? rootPath;
        return NativeMethods.GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)
            ? (long)sectorsPerCluster * bytesPerSector
            : 4096;
    }

    private sealed class Builder(long clusterSize)
    {
        private readonly object _lock = new();
        private readonly List<int> _parent = [];
        private readonly List<long> _size = [];
        private readonly List<long> _alloc = [];
        private readonly List<string?> _name = [];
        private readonly List<byte> _flags = [];

        public int AddRoot(string path)
        {
            lock (_lock) return Add(-1, path, isDirectory: true, 0);
        }

        public int AddChildren(int parent, List<Entry> entries)
        {
            lock (_lock)
            {
                int first = _parent.Count;
                foreach (var e in entries) Add(parent, e.Name, e.IsDirectory, e.Length);
                return first;
            }
        }

        private int Add(int parent, string name, bool isDirectory, long length)
        {
            _parent.Add(parent);
            _name.Add(name);
            _size.Add(isDirectory ? 0 : length);
            _alloc.Add(isDirectory ? 0 : (length + clusterSize - 1) / clusterSize * clusterSize);
            _flags.Add((byte)(NodeFlags.InUse | (isDirectory ? NodeFlags.Directory : 0)));
            return _parent.Count - 1;
        }

        public ScanData ToScanData(int root) => new()
        {
            Count = _parent.Count,
            Root = root,
            Parent = [.. _parent],
            Size = [.. _size],
            Alloc = [.. _alloc],
            Name = [.. _name],
            Flags = [.. _flags],
        };
    }
}
