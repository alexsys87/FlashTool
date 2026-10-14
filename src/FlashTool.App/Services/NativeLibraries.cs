using System.IO;
using System.Reflection;
using FlashTool.Core.Containers;

namespace FlashTool.App.Services;

/// <summary>
/// Locates the native compression libraries (liblzma, 7z.dll). Regular builds
/// ship them as files next to the application; the single-file build embeds
/// them as resources and they are extracted to %LOCALAPPDATA%\FlashTool once.
/// </summary>
public static class NativeLibraries
{
    private const string ResourcePrefix = "FlashTool.Native.";

    public static void Initialize()
    {
        var arch = Environment.Is64BitProcess ? "x64" : "x86";

        var sevenZip = FindFile(Path.Combine(arch, "7z.dll")) ?? Extract("7z.dll");
        if (sevenZip != null)
        {
            SevenZipContainerHandler.Initialize(sevenZip);
        }

        var lzma = FindFile("liblzma.dll") ?? FindFile(Path.Combine("runtimes", $"win-{arch}", "native", "liblzma.dll")) ?? Extract("liblzma.dll");
        if (lzma != null)
        {
            XzContainerHandler.Initialize(lzma);
        }
    }

    private static string? FindFile(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        return File.Exists(path) ? path : null;
    }

    private static string? Extract(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(ResourcePrefix + name);
        if (resource == null || !Environment.Is64BitProcess)
        {
            // Embedded libraries are the x64 builds.
            return null;
        }

        try
        {
            var version = assembly.GetName().Version?.ToString() ?? "0";
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashTool", "native", version);
            var path = Path.Combine(folder, name);
            if (File.Exists(path) && new FileInfo(path).Length == resource.Length)
            {
                return path;
            }

            Directory.CreateDirectory(folder);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = File.Create(temp))
            {
                resource.CopyTo(file);
            }

            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The format just becomes unavailable; the rest of the application keeps working.
            return null;
        }
    }
}
