namespace FlashTool.Core.Containers;

public static class ContainerNaming
{
    private static readonly string[] CompressionExtensions = [".xz", ".gz", ".zip", ".7z"];

    /// <summary>Image file extensions recognized as likely disk images inside archives.</summary>
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".img", ".iso", ".bin", ".raw", ".dd", ".image", ".hdd", ".sdcard", ".wic", ".rootfs",
    };

    /// <summary>
    /// Derives the name of the image inside a single-stream container:
    /// "raspios.img.xz" -> "raspios.img", "backup.7z" -> "backup.img".
    /// </summary>
    public static string GetInnerName(string containerFileName)
    {
        var name = Path.GetFileName(containerFileName);
        foreach (var ext in CompressionExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^ext.Length];
                break;
            }
        }

        if (string.IsNullOrEmpty(Path.GetExtension(name)))
        {
            name += ".img";
        }

        return name;
    }

    public static bool HasImageExtension(string name) => ImageExtensions.Contains(Path.GetExtension(name));
}
