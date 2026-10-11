using FileTree.Scanning;

namespace FileTree.Tests;

public class RecordParserTests
{
    private const long Cluster = 4096;

    // 16 clusters at LCN 0x1000, 8 sparse clusters, then 4 clusters at LCN 0x1000 - 16 (negative delta).
    private static readonly byte[] MixedRuns = [0x21, 0x10, 0x00, 0x10, 0x01, 0x08, 0x11, 0x04, 0xF0, 0x00];

    [Fact]
    public void DecodeRuns_HandlesSparseRunsAndNegativeOffsets()
    {
        var runs = RecordParser.DecodeRuns(MixedRuns);

        Assert.Equal(new[] { new Run(0x1000, 16), new Run(-1, 8), new Run(0xFF0, 4) }, runs);
    }

    [Fact]
    public void DecodeRuns_ReadsEightByteOffsetWithoutSignExtension()
    {
        byte[] mp = [0x81, 0x02, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00];

        Assert.Equal(new[] { new Run(0x1_0000_0000, 2) }, RecordParser.DecodeRuns(mp));
    }

    [Theory]
    [InlineData(new byte[] { 0x09, 0x01, 0x02 })] // length field wider than 8 bytes
    [InlineData(new byte[] { 0x21, 0x10, 0x00 })] // truncated offset
    [InlineData(new byte[] { 0x20, 0x00, 0x10 })] // zero-width length
    public void DecodeRuns_RejectsMalformedPairs(byte[] mp)
    {
        Assert.Throws<InvalidDataException>(() => RecordParser.DecodeRuns(mp));
    }

    [Fact]
    public void AllocatedClusters_CountsOnlyNonSparseRuns()
    {
        Assert.Equal(20, RecordParser.AllocatedClusters(MixedRuns));
    }

    [Fact]
    public void ApplyFixup_RestoresSectorTails()
    {
        var rec = new RecordBuilder().FileName(new string('n', 250), 5).Build(); // name crosses the first sector tail

        Assert.True(RecordParser.ApplyFixup(rec));
        Assert.Equal((byte)'n', rec[510]);
        Assert.Equal(0, rec[511]);
    }

    [Fact]
    public void ApplyFixup_RejectsTornRecord()
    {
        var rec = new RecordBuilder().Build();
        rec[1022] ^= 0xFF;

        Assert.False(RecordParser.ApplyFixup(rec));
    }

    [Fact]
    public void TryParse_SkipsDosNameAndReadsResidentData()
    {
        var rec = new RecordBuilder()
            .FileName("REPORT~1.TXT", 5, nameSpace: 2)
            .FileName("Report for 2026.txt", 5, nameSpace: 1)
            .ResidentData(123)
            .Build();

        Assert.True(RecordParser.TryParse(rec, Cluster, out var info));
        Assert.Equal("Report for 2026.txt", info.Name);
        Assert.Equal(5, info.ParentFrn);
        Assert.False(info.IsDirectory);
        Assert.Equal(0, info.BaseFrn);
        Assert.True(info.HasSize);
        Assert.Equal(123, info.Size);
        Assert.Equal(0, info.Alloc);
    }

    [Fact]
    public void TryParse_CountsOnlyBackedClustersOfSparseStream()
    {
        // Like $BadClus:$Bad: the header claims a huge allocation but every run is sparse.
        byte[] allSparse = [0x04, 0x00, 0x00, 0x00, 0x01, 0x00];
        var rec = new RecordBuilder().FileName("sparse.bin", 5).NonResidentData(allSparse, 1L << 36, 1L << 36).Build();

        Assert.True(RecordParser.TryParse(rec, Cluster, out var info));
        Assert.Equal(0, info.Alloc);
        Assert.Equal(1L << 36, info.Size);
    }

    [Fact]
    public void TryParse_UsesRunsForPartlySparseStream()
    {
        var rec = new RecordBuilder().FileName("vm.vhdx", 5).NonResidentData(MixedRuns, 28 * Cluster, 28 * Cluster).Build();

        Assert.True(RecordParser.TryParse(rec, Cluster, out var info));
        Assert.Equal(20 * Cluster, info.Alloc);
    }

    [Fact]
    public void TryParse_FallsBackToHeaderWhenRunsAreMalformed()
    {
        byte[] broken = [0x09, 0x01, 0x02, 0x00];
        var rec = new RecordBuilder().FileName("odd.dat", 5).NonResidentData(broken, 5000, 8192).Build();

        Assert.True(RecordParser.TryParse(rec, Cluster, out var info));
        Assert.Equal(8192, info.Alloc);
    }

    [Fact]
    public void TryParse_ExtensionRecordReportsBaseAndAllocationButNoSize()
    {
        byte[] runs = [0x11, 0x03, 0x20, 0x00];
        var rec = new RecordBuilder().Extension(42).NonResidentData(runs, 0, 0, lowestVcn: 100).Build();

        Assert.True(RecordParser.TryParse(rec, Cluster, out var info));
        Assert.Equal(42, info.BaseFrn);
        Assert.False(info.HasSize);
        Assert.Equal(3 * Cluster, info.Alloc);
        Assert.Null(info.Name);
    }

    [Fact]
    public void TryParse_ReadsDirectoryFlag()
    {
        var rec = new RecordBuilder().Directory().FileName("Users", 5).Build();

        Assert.True(RecordParser.TryParse(rec, Cluster, out var info));
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public void TryParse_RejectsFreeAndTornRecords()
    {
        var free = new RecordBuilder().NotInUse().FileName("deleted.txt", 5).Build();
        var torn = new RecordBuilder().FileName("torn.txt", 5).Build();
        torn[510] ^= 0xFF;

        Assert.False(RecordParser.TryParse(free, Cluster, out _));
        Assert.False(RecordParser.TryParse(torn, Cluster, out _));
    }
}
