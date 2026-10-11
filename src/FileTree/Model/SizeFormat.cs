namespace FileTree.Model;

internal static class SizeFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        int unit = 0;
        while (v >= 1024 && unit < Units.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        return v >= 100 ? $"{v:F0} {Units[unit]}" : $"{v:F1} {Units[unit]}";
    }
}
