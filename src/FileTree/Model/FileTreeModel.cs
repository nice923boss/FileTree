using FileTree.Scanning;

namespace FileTree.Model;

/// <summary>
/// Immutable directory tree built from <see cref="ScanData"/>: validated parent links,
/// cumulative directory sizes, and children stored in CSR form sorted by allocated size (desc).
/// Safe for concurrent reads after construction.
/// </summary>
internal sealed class FileTreeModel
{
    private readonly string?[] _name;
    private readonly int[] _parent;
    private readonly long[] _size;
    private readonly long[] _alloc;
    private readonly int[] _files;
    private readonly byte[] _flags;
    private readonly int[] _childStart;
    private readonly int[] _children;

    public int Root { get; }
    public int FileCount { get; }
    public int DirectoryCount { get; }
    /// <summary>In-use named records whose parent chain does not reach the root (excluded from the tree).</summary>
    public int OrphanCount { get; }
    /// <summary>Allocated bytes of in-use records left out of the tree (orphans and nameless records).</summary>
    public long UnattachedAlloc { get; }

    private FileTreeModel(ScanData raw, int[] childStart, int[] children, int[] files, int dirCount, int orphans,
        long unattachedAlloc)
    {
        _name = raw.Name;
        _parent = raw.Parent;
        _size = raw.Size;
        _alloc = raw.Alloc;
        _flags = raw.Flags;
        _childStart = childStart;
        _children = children;
        _files = files;
        Root = raw.Root;
        FileCount = files[raw.Root];
        DirectoryCount = dirCount;
        OrphanCount = orphans;
        UnattachedAlloc = unattachedAlloc;
    }

    public string Name(int node) => _name[node] ?? "";
    public int Parent(int node) => node == Root ? -1 : _parent[node];
    public long Size(int node) => _size[node];
    public long Alloc(int node) => _alloc[node];
    public bool IsDirectory(int node) => (_flags[node] & NodeFlags.Directory) != 0;
    /// <summary>Number of files in the subtree (1 for a file).</summary>
    public int Files(int node) => _files[node];
    public ReadOnlySpan<int> Children(int node) => _children.AsSpan(_childStart[node], _childStart[node + 1] - _childStart[node]);
    public int ChildCount(int node) => _childStart[node + 1] - _childStart[node];

    public string FullPath(int node)
    {
        var parts = new List<string>();
        for (int n = node; n != Root && n >= 0; n = _parent[n]) parts.Add(Name(n));
        parts.Reverse();
        return Path.Join(Name(Root), string.Join('\\', parts));
    }

    public bool IsAncestorOrSelf(int ancestor, int node)
    {
        for (int n = node; n >= 0; n = Parent(n))
            if (n == ancestor) return true;
        return false;
    }

    /// <summary>Chain of nodes from the root down to <paramref name="node"/> (inclusive).</summary>
    public List<int> PathFromRoot(int node)
    {
        var chain = new List<int>();
        for (int n = node; n >= 0; n = Parent(n)) chain.Add(n);
        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Largest files (by allocated size) under <paramref name="dir"/>, largest first.
    /// Children are pre-sorted, so once the heap is full any sibling not larger than its minimum
    /// (and that sibling's whole subtree) can be skipped.
    /// </summary>
    public int[] TopFiles(int dir, int limit)
    {
        var heap = new PriorityQueue<int, long>(limit + 1);
        var stack = new Stack<int>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            foreach (int child in Children(stack.Pop()))
            {
                long a = _alloc[child];
                if (heap.Count == limit && heap.TryPeek(out _, out long min) && a <= min) break;
                if (IsDirectory(child)) stack.Push(child);
                else if (heap.Count < limit) heap.Enqueue(child, a);
                else heap.EnqueueDequeue(child, a);
            }
        }
        var result = new int[heap.Count];
        for (int i = result.Length - 1; i >= 0; i--) result[i] = heap.Dequeue();
        return result;
    }

    public static FileTreeModel Build(ScanData raw, string rootName)
    {
        int n = raw.Count;
        int root = raw.Root;
        var parent = raw.Parent;
        var flags = raw.Flags;
        raw.Name[root] = rootName;
        flags[root] |= NodeFlags.InUse | NodeFlags.Directory;

        var state = ResolveReachability(raw);

        // Children in CSR form.
        var childStart = new int[n + 1];
        int validCount = 0, dirCount = 0, orphans = 0;
        long unattached = 0;
        for (int i = 0; i < n; i++)
        {
            if (state[i] != Reach.Ok)
            {
                if (state[i] == Reach.Orphan) orphans++;
                if ((flags[i] & NodeFlags.InUse) != 0) unattached += raw.Alloc[i];
                continue;
            }
            validCount++;
            if (i == root) continue;
            if ((flags[i] & NodeFlags.Directory) != 0) dirCount++;
            childStart[parent[i] + 1]++;
        }
        for (int i = 0; i < n; i++) childStart[i + 1] += childStart[i];
        var children = new int[childStart[n]];
        var fill = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (state[i] != Reach.Ok || i == root) continue;
            int p = parent[i];
            children[childStart[p] + fill[p]++] = i;
        }

        // Breadth-first order guarantees parents precede children; accumulate in reverse.
        var order = new int[validCount];
        int head = 0, tail = 0;
        order[tail++] = root;
        while (head < tail)
        {
            int node = order[head++];
            for (int k = childStart[node]; k < childStart[node + 1]; k++) order[tail++] = children[k];
        }
        var files = new int[n];
        for (int k = tail - 1; k > 0; k--)
        {
            int node = order[k];
            int p = parent[node];
            if ((flags[node] & NodeFlags.Directory) == 0) files[node] = 1;
            raw.Size[p] += raw.Size[node];
            raw.Alloc[p] += raw.Alloc[node];
            files[p] += files[node];
        }

        SortChildren(childStart, children, raw.Alloc);
        return new FileTreeModel(raw, childStart, children, files, dirCount, orphans, unattached);
    }

    private static class Reach
    {
        public const byte Unknown = 0, Ok = 1, Orphan = 2, Excluded = 3, Visiting = 4;
    }

    /// <summary>Marks each record Ok when its parent chain reaches the root through in-use directories.</summary>
    private static byte[] ResolveReachability(ScanData raw)
    {
        int n = raw.Count;
        var state = new byte[n];
        state[raw.Root] = Reach.Ok;
        var path = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (state[i] != Reach.Unknown) continue;
            if ((raw.Flags[i] & NodeFlags.InUse) == 0 || raw.Name[i] is null)
            {
                state[i] = Reach.Excluded;
                continue;
            }
            byte result;
            int cur = i;
            while (true)
            {
                byte s = state[cur];
                if (s == Reach.Ok) { result = Reach.Ok; break; }
                if (s != Reach.Unknown) { result = Reach.Orphan; break; } // excluded, orphan, or cycle
                if ((raw.Flags[cur] & NodeFlags.InUse) == 0 || raw.Name[cur] is null) { result = Reach.Orphan; break; }
                state[cur] = Reach.Visiting;
                path.Add(cur);
                int p = raw.Parent[cur];
                if (p < 0 || p >= n || (raw.Flags[p] & NodeFlags.Directory) == 0) { result = Reach.Orphan; break; }
                cur = p;
            }
            foreach (int node in path) state[node] = result;
            path.Clear();
        }
        return state;
    }

    private static void SortChildren(int[] childStart, int[] children, long[] alloc)
    {
        var keys = new long[children.Length];
        for (int k = 0; k < children.Length; k++) keys[k] = -alloc[children[k]];
        for (int node = 0; node + 1 < childStart.Length; node++)
        {
            int len = childStart[node + 1] - childStart[node];
            if (len > 1) Array.Sort(keys, children, childStart[node], len);
        }
    }
}
