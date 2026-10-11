using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FileTree.Scanning;

/// <summary>A data run: <see cref="Lcn"/> is -1 for a sparse run.</summary>
internal readonly record struct Run(long Lcn, long Clusters);

/// <summary>Fields extracted from one MFT FILE record.</summary>
internal struct RecordInfo
{
    public bool IsDirectory;
    /// <summary>Base record number; 0 when this record is itself a base record.</summary>
    public long BaseFrn;
    /// <summary>True when this record holds the first extent (or resident value) of the unnamed $DATA stream.</summary>
    public bool HasSize;
    /// <summary>Logical size of the unnamed $DATA stream.</summary>
    public long Size;
    /// <summary>Bytes backed by the non-sparse runs of every non-resident attribute extent in this record.</summary>
    public long Alloc;
    public string? Name;
    public long ParentFrn;
}

/// <summary>
/// Pure parsing of NTFS on-disk structures (FILE records, attributes, mapping pairs).
/// Layout reference: NTFS FILE_RECORD_SEGMENT_HEADER / ATTRIBUTE_RECORD_HEADER.
/// </summary>
internal static class RecordParser
{
    public const uint FileSignature = 0x454C4946; // "FILE"
    private const uint AttrEnd = 0xFFFFFFFF;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const ushort RecordInUse = 0x01;
    private const ushort RecordIsDirectory = 0x02;
    private const ushort AttrCompressed = 0x0001;
    private const ushort AttrSparse = 0x8000;
    private const byte NamespaceDos = 2;
    private const long RefMask = 0x0000_FFFF_FFFF_FFFF;

    /// <summary>
    /// Parses an in-use FILE record in place (applies the update-sequence fixup).
    /// Returns false for free, torn or malformed records.
    /// </summary>
    public static bool TryParse(Span<byte> rec, long clusterSize, out RecordInfo info)
    {
        info = default;
        info.ParentFrn = -1;
        if (rec.Length < 0x30 || U32(rec, 0) != FileSignature) return false;
        ushort flags = U16(rec, 0x16);
        if ((flags & RecordInUse) == 0) return false;
        if (!ApplyFixup(rec)) return false;

        info.IsDirectory = (flags & RecordIsDirectory) != 0;
        info.BaseFrn = I64(rec, 0x20) & RefMask;

        int used = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
        int off = U16(rec, 0x14);
        while (off + 0x10 <= used)
        {
            uint type = U32(rec, off);
            if (type == AttrEnd) break;
            int len = (int)U32(rec, off + 4);
            if (len < 0x10 || len > used - off) break;
            var attr = rec.Slice(off, len);
            if (attr[8] == 0) ParseResident(attr, type, ref info);
            else ParseNonResident(attr, type, clusterSize, ref info);
            off += len;
        }
        return true;
    }

    private static void ParseResident(ReadOnlySpan<byte> attr, uint type, ref RecordInfo info)
    {
        if (attr.Length < 0x18) return;
        int valueLen = (int)U32(attr, 0x10);
        int valueOff = U16(attr, 0x14);
        if (valueLen < 0 || valueOff > attr.Length || valueLen > attr.Length - valueOff) return;
        var value = attr.Slice(valueOff, valueLen);

        if (type == AttrData)
        {
            if (attr[9] == 0) { info.Size = valueLen; info.HasSize = true; }
        }
        else if (type == AttrFileName && info.Name is null && value.Length >= 0x42)
        {
            int nameLen = value[0x40];
            // Skip DOS 8.3 aliases; the Win32/POSIX name is the one users see.
            if (value[0x41] == NamespaceDos || 0x42 + nameLen * 2 > value.Length) return;
            info.Name = new string(MemoryMarshal.Cast<byte, char>(value.Slice(0x42, nameLen * 2)));
            info.ParentFrn = I64(value, 0) & RefMask;
        }
    }

    private static void ParseNonResident(ReadOnlySpan<byte> attr, uint type, long clusterSize, ref RecordInfo info)
    {
        if (attr.Length < 0x40) return;
        // Every extent counts the clusters its own runs occupy. Header sizes are not trusted for this:
        // $BadClus:$Bad claims the whole volume as allocated while its runs are all sparse.
        int mpOff = U16(attr, 0x20);
        if (mpOff >= 0x40 && mpOff < attr.Length)
        {
            try
            {
                info.Alloc += AllocatedClusters(attr[mpOff..]) * clusterSize;
            }
            catch (InvalidDataException)
            {
                info.Alloc += HeaderAlloc(attr);
            }
        }
        else info.Alloc += HeaderAlloc(attr);

        // Only the extent starting at VCN 0 carries valid size fields.
        if (type == AttrData && attr[9] == 0 && I64(attr, 0x10) == 0)
        {
            info.Size = I64(attr, 0x30);
            info.HasSize = true;
        }
    }

    /// <summary>Allocation from the attribute header; used only when the mapping pairs are unreadable.</summary>
    private static long HeaderAlloc(ReadOnlySpan<byte> attr)
    {
        if (I64(attr, 0x10) != 0) return 0;
        // Compressed/sparse attributes store the clusters really in use at 0x40.
        if ((U16(attr, 0x0C) & (AttrCompressed | AttrSparse)) != 0 && U16(attr, 0x20) >= 0x48 && attr.Length >= 0x48)
            return I64(attr, 0x40);
        return I64(attr, 0x28);
    }

    /// <summary>Number of clusters backed by real (non-sparse) runs.</summary>
    public static long AllocatedClusters(ReadOnlySpan<byte> mp)
    {
        long clusters = 0, lcn = 0;
        int p = 0;
        while (NextRun(mp, ref p, ref lcn, out var run))
            if (run.Lcn >= 0) clusters += run.Clusters;
        return clusters;
    }

    /// <summary>Restores the last two bytes of every 512-byte stride from the update sequence array.</summary>
    public static bool ApplyFixup(Span<byte> rec)
    {
        int usaOff = U16(rec, 4);
        int usaCount = U16(rec, 6);
        if (usaCount < 2 || usaOff + usaCount * 2 > rec.Length) return false;
        int stride = rec.Length / (usaCount - 1);
        if (stride < 2 || stride * (usaCount - 1) != rec.Length) return false;
        ushort usn = U16(rec, usaOff);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * stride - 2;
            if (U16(rec, pos) != usn) return false;
            rec[pos] = rec[usaOff + 2 * i];
            rec[pos + 1] = rec[usaOff + 2 * i + 1];
        }
        return true;
    }

    /// <summary>Decodes an NTFS mapping-pairs array into absolute runs.</summary>
    public static List<Run> DecodeRuns(ReadOnlySpan<byte> mp)
    {
        var runs = new List<Run>();
        long lcn = 0;
        int p = 0;
        while (NextRun(mp, ref p, ref lcn, out var run)) runs.Add(run);
        return runs;
    }

    /// <summary>Reads the mapping pair at <paramref name="p"/>. Returns false at the terminator.</summary>
    private static bool NextRun(ReadOnlySpan<byte> mp, ref int p, ref long lcn, out Run run)
    {
        run = default;
        if (p >= mp.Length || mp[p] == 0) return false;
        int lenBytes = mp[p] & 0x0F;
        int offBytes = mp[p] >> 4;
        p++;
        if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || p + lenBytes + offBytes > mp.Length)
            throw new InvalidDataException("資料區段（mapping pairs）格式錯誤");

        long length = 0;
        for (int i = 0; i < lenBytes; i++) length |= (long)mp[p + i] << (8 * i);
        p += lenBytes;

        if (offBytes == 0)
        {
            run = new Run(-1, length);
            return true;
        }
        long delta = 0;
        for (int i = 0; i < offBytes; i++) delta |= (long)mp[p + i] << (8 * i);
        if (offBytes < 8 && (mp[p + offBytes - 1] & 0x80) != 0) delta |= -1L << (8 * offBytes);
        p += offBytes;
        lcn += delta;
        run = new Run(lcn, length);
        return true;
    }

    /// <summary>
    /// Extracts the data runs of $MFT from record 0. Returns null when record 0 does not describe
    /// the whole valid MFT (the $DATA attribute continues in extension records).
    /// </summary>
    public static List<Run>? ParseMftDataRuns(Span<byte> rec0, long clusterSize, long validDataLength)
    {
        if (rec0.Length < 0x30 || U32(rec0, 0) != FileSignature || !ApplyFixup(rec0))
            throw new InvalidDataException("$MFT 第 0 筆紀錄損毀");

        int used = (int)Math.Min(U32(rec0, 0x18), (uint)rec0.Length);
        int off = U16(rec0, 0x14);
        while (off + 0x10 <= used)
        {
            uint type = U32(rec0, off);
            if (type == AttrEnd) break;
            int len = (int)U32(rec0, off + 4);
            if (len < 0x10 || len > used - off) break;
            var attr = rec0.Slice(off, len);
            if (type == AttrData && attr[8] != 0 && attr[9] == 0 && attr.Length >= 0x40 && I64(attr, 0x10) == 0)
            {
                int mpOff = U16(attr, 0x20);
                if (mpOff >= attr.Length) throw new InvalidDataException("$MFT 的 $DATA 屬性格式錯誤");
                var runs = DecodeRuns(attr[mpOff..]);
                long clusters = 0;
                foreach (var r in runs) clusters += r.Clusters;
                return clusters * clusterSize >= validDataLength ? runs : null;
            }
            off += len;
        }
        throw new InvalidDataException("$MFT 找不到 $DATA 屬性");
    }

    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    private static long I64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt64LittleEndian(s[o..]);
}
