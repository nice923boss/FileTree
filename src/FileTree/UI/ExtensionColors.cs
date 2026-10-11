namespace FileTree.UI;

/// <summary>Stable color per file extension (FNV-1a hash of the lower-case extension into a fixed palette).</summary>
internal static class ExtensionColors
{
    public static readonly Color Folder = Color.FromArgb(232, 178, 62);
    public static readonly Color NoExtension = Color.FromArgb(160, 160, 160);

    private static readonly Color[] Palette =
    [
        Color.FromArgb(78, 121, 167), Color.FromArgb(242, 142, 43), Color.FromArgb(225, 87, 89),
        Color.FromArgb(118, 183, 178), Color.FromArgb(89, 161, 79), Color.FromArgb(237, 201, 72),
        Color.FromArgb(176, 122, 161), Color.FromArgb(255, 157, 167), Color.FromArgb(156, 117, 95),
        Color.FromArgb(31, 119, 180), Color.FromArgb(44, 160, 44), Color.FromArgb(214, 39, 40),
        Color.FromArgb(148, 103, 189), Color.FromArgb(23, 190, 207), Color.FromArgb(188, 189, 34),
        Color.FromArgb(127, 60, 141),
    ];

    public static Color For(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1) return NoExtension;
        uint h = 2166136261;
        foreach (char c in fileName.AsSpan(dot + 1))
        {
            h ^= char.ToLowerInvariant(c);
            h *= 16777619;
        }
        return Palette[h % (uint)Palette.Length];
    }
}
