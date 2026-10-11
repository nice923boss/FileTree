using System.Diagnostics;
using System.Security.Principal;
using FileTree.Model;

namespace FileTree.Scanning;

internal sealed record ScanOutcome(FileTreeModel Model, bool UsedMft, string? Note, TimeSpan ScanTime, TimeSpan BuildTime, long Skipped,
    long VolumeUsedBytes, bool IsFolder);

/// <summary>Chooses the MFT fast path when possible and falls back to directory enumeration otherwise.</summary>
internal static class ScanService
{
    public static readonly bool IsAdministrator =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Scans a drive root (e.g. <c>C:\</c>) or a single folder.</summary>
    public static ScanOutcome Scan(string path, ScanState state, bool forceFallback, CancellationToken ct)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"找不到資料夾：{path}");
        string root = Path.GetPathRoot(path)!;
        var drive = root.Length == 3 && root[1] == ':' ? new DriveInfo(root) : null;
        bool isFolder = drive is null || path.Length > root.Length;
        var sw = Stopwatch.StartNew();
        ScanData? raw = null;
        string? note = null;

        if (forceFallback) note = "手動指定一般遍歷";
        else if (drive is null) note = "不是本機磁碟";
        else if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) note = $"{drive.DriveFormat} 不是 NTFS";
        else if (!IsAdministrator) note = "未取得系統管理員權限";
        else
        {
            try
            {
                raw = isFolder ? ReadFolderFromMft(path, state, ct) : MftScanner.Scan(root[0], state, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                note = $"MFT 讀取失敗：{ex.Message}";
            }
        }

        bool usedMft = raw is not null;
        if (raw is null)
        {
            Interlocked.Exchange(ref state.Done, 0);
            Interlocked.Exchange(ref state.Total, 0);
            raw = FallbackScanner.Scan(path, state, ct);
        }
        ct.ThrowIfCancellationRequested();
        var scanTime = sw.Elapsed;
        sw.Restart();
        var model = FileTreeModel.Build(raw, path);
        return new ScanOutcome(model, usedMft, note, scanTime, sw.Elapsed, Interlocked.Read(ref state.Skipped), raw.VolumeUsedBytes,
            isFolder);
    }

    /// <summary>
    /// Reads the whole MFT (about as fast as for the drive) and re-roots it at the folder's record;
    /// records outside the folder no longer reach the root and are left out of the tree.
    /// </summary>
    private static ScanData ReadFolderFromMft(string path, ScanState state, CancellationToken ct)
    {
        var (letter, frn, name) = MftScanner.LocateDirectory(path);
        var raw = MftScanner.Scan(letter, state, ct);
        if (frn >= raw.Count || (raw.Flags[frn] & NodeFlags.Directory) == 0
            || !string.Equals(raw.Name[frn], name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"MFT 第 {frn} 筆紀錄與資料夾「{name}」不符");
        return new ScanData
        {
            Count = raw.Count,
            Root = (int)frn,
            Parent = raw.Parent,
            Size = raw.Size,
            Alloc = raw.Alloc,
            Name = raw.Name,
            Flags = raw.Flags,
        };
    }
}
