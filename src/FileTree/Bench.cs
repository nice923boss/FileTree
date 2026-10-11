using System.Diagnostics;
using System.Text;
using FileTree.Model;
using FileTree.Scanning;

namespace FileTree;

/// <summary>
/// Headless benchmark / verification mode:
/// FileTree.exe --bench C|folder [--fallback] [--out report.txt] [--tsv dirs.tsv] [--depth 3]
/// Writes a UTF-8 report (timings, totals vs. volume used space, top files) and optionally
/// a TSV of directory totals for cross-checking the MFT and fallback scanners.
/// </summary>
internal static class Bench
{
    public static int Run(string[] args)
    {
        string target = args.Length > 1 ? args[1] : "C";
        if (target.TrimEnd(':', '\\').Length <= 1) target = $"{char.ToUpperInvariant(target[0])}:\\";
        bool fallback = args.Contains("--fallback");
        string outPath = Arg(args, "--out") ?? Path.Combine(AppContext.BaseDirectory, $"bench_{(fallback ? "fallback" : "auto")}.txt");
        string? tsvPath = Arg(args, "--tsv");
        int depth = int.TryParse(Arg(args, "--depth"), out int d) ? d : 3;

        var report = new StringBuilder();
        try
        {
            var outcome = ScanService.Scan(target, new ScanState(), fallback, CancellationToken.None);
            var m = outcome.Model;

            var sw = Stopwatch.StartNew();
            var top = m.TopFiles(m.Root, 1000);
            var topTime = sw.Elapsed;

            long scanned = m.Alloc(m.Root);
            // In folder mode the records outside the folder count as orphans, so the volume checks don't apply.
            var drive = outcome.IsFolder ? null : new DriveInfo(target);
            report.AppendLine(drive is null
                ? $"資料夾：{m.Name(m.Root)}　系統管理員：{ScanService.IsAdministrator}"
                : $"磁碟：{drive.Name} {drive.DriveFormat}　系統管理員：{ScanService.IsAdministrator}");
            report.AppendLine($"模式：{(outcome.UsedMft ? "MFT 直讀" : "一般遍歷")}　備註：{outcome.Note ?? "無"}");
            report.AppendLine($"掃描耗時：{outcome.ScanTime.TotalMilliseconds:F0} ms　建樹耗時：{outcome.BuildTime.TotalMilliseconds:F0} ms　Top1000 查詢：{topTime.TotalMilliseconds:F1} ms");
            report.AppendLine($"檔案：{m.FileCount:N0}　資料夾：{m.DirectoryCount:N0}　{(drive is null ? "" : $"孤立紀錄：{m.OrphanCount:N0}　")}無權限資料夾：{outcome.Skipped:N0}");
            report.AppendLine($"根目錄佔用：{scanned:N0} B（{SizeFormat.Bytes(scanned)}）　邏輯大小：{SizeFormat.Bytes(m.Size(m.Root))}");
            if (drive is not null)
            {
                long used = drive.TotalSize - drive.TotalFreeSpace;
                report.AppendLine($"磁碟已用：{used:N0} B（{SizeFormat.Bytes(used)}）　差異：{(scanned - used) * 100.0 / Math.Max(used, 1):F3} %");
                if (outcome.VolumeUsedBytes > 0)
                {
                    long bitmap = outcome.VolumeUsedBytes;
                    report.AppendLine($"NTFS 位元圖已用：{bitmap:N0} B（{SizeFormat.Bytes(bitmap)}）　差異：{(scanned - bitmap) * 100.0 / bitmap:F3} %");
                }
                report.AppendLine($"未掛入樹的空間：{m.UnattachedAlloc:N0} B（{SizeFormat.Bytes(m.UnattachedAlloc)}）");
            }
            report.AppendLine($"行程記憶體：{Environment.WorkingSet / 1048576} MB");
            report.AppendLine();
            report.AppendLine("== 根目錄子項目（依佔用空間）==");
            foreach (int c in m.Children(m.Root)[..Math.Min(25, m.ChildCount(m.Root))])
                report.AppendLine($"{SizeFormat.Bytes(m.Alloc(c)),10}  {SizeFormat.Bytes(m.Size(c)),10}  {m.Files(c),9:N0}  {m.Name(c)}");
            report.AppendLine();
            report.AppendLine("== 最大檔案 Top 20 ==");
            foreach (int f in top.Take(20))
                report.AppendLine($"{SizeFormat.Bytes(m.Alloc(f)),10}  {SizeFormat.Bytes(m.Size(f)),10}  {m.FullPath(f)}");

            if (tsvPath is not null) WriteTsv(m, tsvPath, depth);
            File.WriteAllText(outPath, report.ToString(), Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            report.AppendLine($"錯誤：{ex}");
            File.WriteAllText(outPath, report.ToString(), Encoding.UTF8);
            return 1;
        }
    }

    private static void WriteTsv(FileTreeModel m, string path, int maxDepth)
    {
        using var w = new StreamWriter(path, false, Encoding.UTF8);
        w.WriteLine("path\tsize\talloc\tfiles");
        var stack = new Stack<(int Node, int Depth)>();
        stack.Push((m.Root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            w.WriteLine($"{m.FullPath(node)}\t{m.Size(node)}\t{m.Alloc(node)}\t{m.Files(node)}");
            if (depth >= maxDepth) continue;
            foreach (int c in m.Children(node))
                if (m.IsDirectory(c)) stack.Push((c, depth + 1));
        }
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
