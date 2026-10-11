using System.Buffers.Binary;
using System.Text;

namespace FileTree.Tests;

/// <summary>Builds synthetic NTFS FILE records (1024 bytes, 512-byte sectors) for parser tests.</summary>
internal sealed class RecordBuilder
{
    public const int RecordSize = 1024;
    private const int UsaOffset = 0x30;
    private const int FirstAttribute = 0x38;
    private const ushort Usn = 0x0007;

    private readonly List<byte[]> _attributes = [];
    private ushort _flags = 0x01;
    private long _baseRef;

    public RecordBuilder Directory()
    {
        _flags |= 0x02;
        return this;
    }

    public RecordBuilder NotInUse()
    {
        _flags = 0;
        return this;
    }

    public RecordBuilder Extension(long baseFrn, ushort sequence = 1)
    {
        _baseRef = baseFrn | (long)sequence << 48;
        return this;
    }

    public RecordBuilder FileName(string name, long parentFrn, byte nameSpace = 1)
    {
        var value = new byte[0x42 + name.Length * 2];
        BinaryPrimitives.WriteInt64LittleEndian(value, parentFrn | 1L << 48);
        value[0x40] = (byte)name.Length;
        value[0x41] = nameSpace;
        Encoding.Unicode.GetBytes(name, value.AsSpan(0x42));
        _attributes.Add(Resident(0x30, value));
        return this;
    }

    public RecordBuilder ResidentData(int size)
    {
        _attributes.Add(Resident(0x80, new byte[size]));
        return this;
    }

    /// <summary>Non-resident $DATA. <paramref name="headerAlloc"/> is what the header claims at 0x28.</summary>
    public RecordBuilder NonResidentData(byte[] mappingPairs, long realSize, long headerAlloc, long lowestVcn = 0, ushort attrFlags = 0)
    {
        const int mpOff = 0x48;
        int len = Align8(mpOff + mappingPairs.Length);
        var a = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(a, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)len);
        a[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x0C), attrFlags);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x10), lowestVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x20), mpOff);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x28), headerAlloc);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x30), realSize);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x38), realSize);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x40), headerAlloc);
        mappingPairs.CopyTo(a, mpOff);
        _attributes.Add(a);
        return this;
    }

    public byte[] Build()
    {
        var rec = new byte[RecordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(rec, 0x454C4946);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(4), UsaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0x14), FirstAttribute);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0x16), _flags);
        BinaryPrimitives.WriteInt64LittleEndian(rec.AsSpan(0x20), _baseRef);
        int off = FirstAttribute;
        foreach (var a in _attributes)
        {
            a.CopyTo(rec, off);
            off += a.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(off), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(0x18), (uint)(off + 8));
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(0x1C), RecordSize);

        // Update sequence: move the last two bytes of each sector into the array, stamp the USN there.
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(UsaOffset), Usn);
        for (int i = 1; i <= 2; i++)
        {
            int pos = i * 512 - 2;
            rec[UsaOffset + 2 * i] = rec[pos];
            rec[UsaOffset + 2 * i + 1] = rec[pos + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(pos), Usn);
        }
        return rec;
    }

    private static byte[] Resident(uint type, byte[] value)
    {
        int len = Align8(0x18 + value.Length);
        var a = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)len);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(0x10), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x14), 0x18);
        value.CopyTo(a, 0x18);
        return a;
    }

    private static int Align8(int n) => (n + 7) & ~7;
}
