using System.Runtime.InteropServices;

namespace FlashTool.Core.Containers;

/// <summary>
/// Finds native libraries shipped through NuGet "runtimes/{rid}/native"
/// folders, which are not on the default DLL search path for RID-neutral builds.
/// </summary>
internal static class NativeLibraryLocator
{
    public static string? Find(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDir, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64",
        };

        var nested = Path.Combine(baseDir, "runtimes", $"{os}-{arch}", "native", fileName);
        return File.Exists(nested) ? nested : null;
    }
}
