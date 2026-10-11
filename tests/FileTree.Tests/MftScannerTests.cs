using FileTree.Scanning;

namespace FileTree.Tests;

public class MftScannerTests
{
    private const int Cluster = 4096, RecSize = 1024;

    [Fact]
    public void PlanChunks_SplitsRunsAndClipsToValidLength()
    {
        // 3 + 2 clusters = 20 records on disk, but only 13 are valid.
        var runs = new[] { new Run(100, 3), new Run(500, 2) };

        var chunks = MftScanner.PlanChunks(runs, 13 * RecSize, RecSize, Cluster, sector: 512, chunkBytes: 8192);

        Assert.Equal(new[]
        {
            new MftScanner.Chunk(100 * Cluster, 0, 8, 8192),
            new MftScanner.Chunk(100 * Cluster + 8192, 8, 4, 4096),
            new MftScanner.Chunk(500 * Cluster, 12, 1, 1024),
        }, chunks);
    }

    [Fact]
    public void PlanChunks_AlignsReadSizeToSector()
    {
        var runs = new[] { new Run(100, 3), new Run(500, 2) };

        var last = MftScanner.PlanChunks(runs, 13 * RecSize, RecSize, Cluster, sector: 4096, chunkBytes: 8192)[^1];

        Assert.Equal(1, last.RecordCount);
        Assert.Equal(4096, last.ReadBytes);
    }

    [Fact]
    public void PlanChunks_SkipsSparseRunsButKeepsRecordNumbering()
    {
        var runs = new[] { new Run(100, 1), new Run(-1, 1), new Run(200, 1) };

        var chunks = MftScanner.PlanChunks(runs, 3 * Cluster, RecSize, Cluster, sector: 512);

        Assert.Equal(new[]
        {
            new MftScanner.Chunk(100 * Cluster, 0, 4, 4096),
            new MftScanner.Chunk(200 * Cluster, 8, 4, 4096),
        }, chunks);
    }
}
