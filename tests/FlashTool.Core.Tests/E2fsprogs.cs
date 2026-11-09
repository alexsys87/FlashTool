using System.Diagnostics;

namespace FlashTool.Core.Tests;

/// <summary>Runs e2fsprogs (mke2fs, e2fsck, debugfs, dumpe2fs) where they are installed, i.e. on Linux.</summary>
internal static class E2fsprogs
{
    private static readonly string[] ToolFolders = ["/usr/sbin", "/sbin", "/usr/bin", "/bin"];

    public static string? Find(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return ToolFolders
            .Select(d => Path.Combine(d, name))
            .FirstOrDefault(File.Exists);
    }

    public static (int ExitCode, string Output) Run(string tool, string arguments, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo(tool, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var text = stdout.Result + stderr;
        if (!allowFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{tool} {arguments} failed ({process.ExitCode}):\n{text}");
        }

        return (process.ExitCode, text);
    }
}
