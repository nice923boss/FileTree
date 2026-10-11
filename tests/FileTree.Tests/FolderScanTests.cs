using System.Diagnostics;
using FileTree.Scanning;

namespace FileTree.Tests;

/// <summary>Folder scans and cancellation against a real temporary directory.</summary>
public sealed class FolderScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FileTreeTests_" + Guid.NewGuid().ToString("N"));

    public FolderScanTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        File.WriteAllBytes(Path.Combine(_dir, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_dir, "sub", "b.bin"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(_dir, "sub", "deep", "c.bin"), new byte[20]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void FolderScan_RootsTreeAtFolder()
    {
        var outcome = ScanService.Scan(_dir + "\\", new ScanState(), forceFallback: true, CancellationToken.None);
        var m = outcome.Model;

        Assert.True(outcome.IsFolder);
        Assert.False(outcome.UsedMft);
        Assert.Equal(_dir, m.Name(m.Root));
        Assert.Equal(3, m.FileCount);
        Assert.Equal(3, m.DirectoryCount); // sub, sub\deep, empty
        Assert.Equal(6020, m.Size(m.Root));
        int largest = m.TopFiles(m.Root, 1)[0];
        Assert.Equal(Path.Combine(_dir, "sub", "b.bin"), m.FullPath(largest));
    }

    [Fact]
    public void Scan_WithCancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ScanService.Scan(_dir, new ScanState(), true, cts.Token));
    }

    [Fact]
    public void Scan_CancelledMidway_StopsQuickly()
    {
        // C:\Windows is large enough that the scan is still running when the token is cancelled.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var state = new ScanState();
        using var cts = new CancellationTokenSource();
        var scan = Task.Run(() => ScanService.Scan(windows, state, true, cts.Token));
        while (Interlocked.Read(ref state.Done) < 5000 && !scan.IsCompleted) Thread.Sleep(1);

        var sw = Stopwatch.StartNew();
        cts.Cancel();
        var ex = Record.Exception(() => scan.GetAwaiter().GetResult());
        sw.Stop();

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"cancellation took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Scan_MissingFolder_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => ScanService.Scan(Path.Combine(_dir, "missing"), new ScanState(), true, CancellationToken.None));
    }

    [Fact]
    public void LocateDirectory_VolumeRoot_IsMftRecord5()
    {
        string root = Path.GetPathRoot(_dir)!; // the test machine's temp drive is NTFS
        var (letter, frn, name) = MftScanner.LocateDirectory(root);

        Assert.Equal(char.ToUpperInvariant(root[0]), letter);
        Assert.Equal(MftScanner.RootFrn, frn);
        Assert.Equal("", name);
    }

    [Fact]
    public void LocateDirectory_Subfolder_ReturnsItsName()
    {
        var (letter, frn, name) = MftScanner.LocateDirectory(Path.Combine(_dir, "sub"));

        Assert.Equal(char.ToUpperInvariant(_dir[0]), letter);
        Assert.Equal("sub", name);
        Assert.True(frn > MftScanner.RootFrn);
    }

    [Fact]
    public void LocateDirectory_ResolvesJunctionToTarget()
    {
        string target = Path.Combine(_dir, "sub"), link = Path.Combine(_dir, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false };
        using (var p = Process.Start(psi)!) p.WaitForExit();
        try
        {
            Assert.True(Directory.Exists(link));
            Assert.Equal(MftScanner.LocateDirectory(target), MftScanner.LocateDirectory(link));
        }
        finally
        {
            // Removes only the junction; a recursive delete would try DeleteVolumeMountPoint and fail without admin.
            Directory.Delete(link);
        }
    }
}
