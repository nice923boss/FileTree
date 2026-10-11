namespace FileTree.Scanning;

internal static class NodeFlags
{
    public const byte InUse = 0x01;
    public const byte Directory = 0x02;
}

/// <summary>
/// Raw per-record arrays produced by a scanner. Index = MFT record number (or sequential id
/// for the fallback scanner). <see cref="Model.FileTreeModel"/> takes ownership of the arrays.
/// </summary>
internal sealed class ScanData
{
    public required int Count { get; init; }
    public required int Root { get; init; }
    public required int[] Parent { get; init; }
    public required long[] Size { get; init; }
    public required long[] Alloc { get; init; }
    public required string?[] Name { get; init; }
    public required byte[] Flags { get; init; }
    /// <summary>Used bytes according to the NTFS cluster bitmap; 0 when unknown (fallback scanner).</summary>
    public long VolumeUsedBytes { get; init; }
}

/// <summary>Progress counters shared between the scan thread and the UI (read with Volatile/Interlocked).</summary>
internal sealed class ScanState
{
    public long Done;
    public long Total;
    public long Skipped;
}
