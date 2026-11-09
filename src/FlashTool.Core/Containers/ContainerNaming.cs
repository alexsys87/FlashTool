namespace FlashTool.Core.Containers;

public static class ContainerNaming
{
    private static readonly string[] CompressionExtensions = [".xz", ".gz", ".zst", ".bz2", ".zip", ".7z"];

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

    /// <summary>"core-image.wic.zst" -> "core-image.wic"; a name without a compression extension is returned as is.</summary>
    public static string RemoveCompressionExtension(string fileName)
    {
        foreach (var ext in CompressionExtensions)
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && fileName.Length > ext.Length)
            {
                return fileName[..^ext.Length];
            }
        }

        return fileName;
    }

    public static bool HasImageExtension(string name) => ImageExtensions.Contains(Path.GetExtension(name));
}
