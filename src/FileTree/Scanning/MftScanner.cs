using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static FileTree.Scanning.NativeMethods;

namespace FileTree.Scanning;

/// <summary>
/// Reads the NTFS Master File Table straight from the raw volume (requires administrator).
/// The MFT is split into 4 MB chunks that are read and parsed in parallel; each record writes
/// only its own array slot, so no locking is needed except for rare extension records.
/// </summary>
internal static unsafe class MftScanner
{
    public const int RootFrn = 5;
    private const int ChunkBytes = 4 * 1024 * 1024;
    private const int BufferAlignment = 4096;

    internal readonly record struct Chunk(long DiskOffset, long FirstRecord, int RecordCount, int ReadBytes);

    public static ScanData Scan(char driveLetter, ScanState state, CancellationToken ct)
    {
        using var volume = OpenVolume(driveLetter);
        var vd = QueryVolumeData(volume);
        int recSize = (int)vd.BytesPerFileRecordSegment;
        int cluster = (int)vd.BytesPerCluster;
        int sector = (int)vd.BytesPerSector;
        if (cluster < recSize)
            throw new NotSupportedException($"叢集大小 {cluster} 位元組小於 MFT 紀錄大小 {recSize}，不支援直接讀取");

        var runs = ReadMftRuns(volume, vd);
        long totalRecords = vd.MftValidDataLength / recSize;
        if (totalRecords > int.MaxValue) throw new NotSupportedException("MFT 紀錄數超過上限");
        int count = (int)totalRecords;
        var chunks = PlanChunks(runs, vd.MftValidDataLength, recSize, cluster, sector);
        Interlocked.Exchange(ref state.Total, count);

        var data = new ScanData
        {
            Count = count,
            Root = RootFrn,
            Parent = new int[count],
            Size = new long[count],
            Alloc = new long[count],
            Name = new string?[count],
            Flags = new byte[count],
            VolumeUsedBytes = (vd.TotalClusters - vd.FreeClusters) * vd.BytesPerCluster,
        };
        var extensions = new ConcurrentQueue<(int BaseFrn, RecordInfo Info)>();

        Parallel.ForEach(
            chunks,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) },
            () => (nint)NativeMemory.AlignedAlloc(ChunkBytes, BufferAlignment),
            (chunk, _, buffer) =>
            {
                ProcessChunk(volume, chunk, buffer, recSize, cluster, data, extensions);
                Interlocked.Add(ref state.Done, chunk.RecordCount);
                return buffer;
            },
            buffer => NativeMemory.AlignedFree((void*)buffer));

        foreach (var (baseFrn, info) in extensions) MergeExtension(data, baseFrn, info);
        return data;
    }

    private static void ProcessChunk(SafeFileHandle volume, Chunk chunk, nint buffer, int recSize, int cluster,
        ScanData data, ConcurrentQueue<(int, RecordInfo)> extensions)
    {
        var span = new Span<byte>((void*)buffer, chunk.ReadBytes);
        ReadExactly(volume, span, chunk.DiskOffset);
        for (int r = 0; r < chunk.RecordCount; r++)
        {
            long frnLong = chunk.FirstRecord + r;
            if (frnLong >= data.Count) break;
            if (!RecordParser.TryParse(span.Slice(r * recSize, recSize), cluster, out var info)) continue;

            if (info.BaseFrn != 0)
            {
                // Extension record: its attributes belong to the base record; merged after the parallel pass.
                if (info.BaseFrn < data.Count) extensions.Enqueue(((int)info.BaseFrn, info));
                continue;
            }
            int frn = (int)frnLong;
            data.Flags[frn] = (byte)(NodeFlags.InUse | (info.IsDirectory ? NodeFlags.Directory : 0));
            data.Name[frn] = info.Name;
            data.Parent[frn] = info.ParentFrn >= 0 && info.ParentFrn < data.Count ? (int)info.ParentFrn : -1;
            data.Size[frn] = info.Size;
            data.Alloc[frn] = info.Alloc;
        }
    }

    private static void MergeExtension(ScanData data, int baseFrn, in RecordInfo ext)
    {
        if ((data.Flags[baseFrn] & NodeFlags.InUse) == 0) return;
        data.Alloc[baseFrn] += ext.Alloc;
        if (ext.HasSize) data.Size[baseFrn] = ext.Size;
        if (data.Name[baseFrn] is null && ext.Name is not null)
        {
            data.Name[baseFrn] = ext.Name;
            data.Parent[baseFrn] = ext.ParentFrn >= 0 && ext.ParentFrn < data.Count ? (int)ext.ParentFrn : -1;
        }
    }

    /// <summary>Splits the MFT runs into sector-aligned read chunks, clipped to the valid data length.</summary>
    internal static List<Chunk> PlanChunks(IReadOnlyList<Run> runs, long validBytes, int recSize, int cluster, int sector,
        int chunkBytes = ChunkBytes)
    {
        var chunks = new List<Chunk>();
        long logical = 0; // byte offset inside the $MFT stream
        foreach (var run in runs)
        {
            if (logical >= validBytes) break;
            long runBytes = run.Clusters * cluster;
            long usable = Math.Min(runBytes, validBytes - logical);
            if (run.Lcn >= 0)
            {
                for (long pos = 0; pos < usable; pos += chunkBytes)
                {
                    int bytes = (int)Math.Min(chunkBytes, usable - pos);
                    chunks.Add(new Chunk(run.Lcn * cluster + pos, (logical + pos) / recSize, bytes / recSize, AlignUp(bytes, sector)));
                }
            }
            logical += runBytes;
        }
        return chunks;
    }

    private static List<Run> ReadMftRuns(SafeFileHandle volume, in NtfsVolumeData vd)
    {
        int recSize = (int)vd.BytesPerFileRecordSegment;
        int readSize = AlignUp(recSize, (int)vd.BytesPerSector);
        void* p = NativeMemory.AlignedAlloc((nuint)readSize, BufferAlignment);
        try
        {
            var buf = new Span<byte>(p, readSize);
            ReadExactly(volume, buf, vd.MftStartLcn * vd.BytesPerCluster);
            return RecordParser.ParseMftDataRuns(buf[..recSize], vd.BytesPerCluster, vd.MftValidDataLength)
                ?? throw new NotSupportedException("$MFT 高度碎片化（$DATA 跨越多筆紀錄），暫不支援直接讀取");
        }
        finally
        {
            NativeMemory.AlignedFree(p);
        }
    }

    /// <summary>
    /// Finds the volume and MFT record of a directory without administrator rights. On NTFS the file ID is the
    /// MFT file reference (record number in the low 48 bits). Junctions and mount points along the path are
    /// resolved, so the returned drive letter is the volume that actually holds the directory.
    /// </summary>
    public static (char DriveLetter, long Frn, string Name) LocateDirectory(string path)
    {
        using var handle = CreateFile(path, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid) throw new IOException($"無法開啟資料夾 {path}（{LastErrorMessage()}）");
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException($"無法讀取資料夾資訊 {path}（{LastErrorMessage()}）");

        var buffer = new char[32768];
        uint length;
        fixed (char* p = buffer) length = GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            throw new IOException($"無法解析資料夾實際路徑 {path}（{LastErrorMessage()}）");
        string final = new(buffer, 0, (int)length); // \\?\C:\dir\sub
        if (final.Length < 7 || !final.StartsWith(@"\\?\", StringComparison.Ordinal) || final[5] != ':')
            throw new NotSupportedException($"資料夾不在有磁碟代號的磁碟區上：{final}");

        long frn = ((long)info.FileIndexHigh << 32 | info.FileIndexLow) & 0xFFFF_FFFF_FFFF;
        return (char.ToUpperInvariant(final[4]), frn, Path.GetFileName(final));
    }

    private static SafeFileHandle OpenVolume(char letter)
    {
        var handle = CreateFile($@"\\.\{letter}:", GenericRead, FileShareAll, 0, OpenExisting, FileFlagNoBuffering, 0);
        if (handle.IsInvalid)
            throw new IOException($"無法開啟磁碟區 {letter}:（{LastErrorMessage()}）");
        return handle;
    }

    private static NtfsVolumeData QueryVolumeData(SafeFileHandle volume)
    {
        NtfsVolumeData vd;
        if (!DeviceIoControl(volume, FsctlGetNtfsVolumeData, null, 0, &vd, sizeof(NtfsVolumeData), out _, 0))
            throw new IOException($"讀取 NTFS 磁碟區資訊失敗（{LastErrorMessage()}）");
        return vd;
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (!buffer.IsEmpty)
        {
            int n = RandomAccess.Read(handle, buffer, offset);
            if (n <= 0) throw new EndOfStreamException($"讀取磁區位移 {offset} 時資料提前結束");
            buffer = buffer[n..];
            offset += n;
        }
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
