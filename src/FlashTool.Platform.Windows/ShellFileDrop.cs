using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FlashTool.Platform.Windows;

/// <summary>
/// Accepts files dropped from Explorer onto an elevated window.
/// </summary>
/// <remarks>
/// The app runs as administrator, and User Interface Privilege Isolation blocks OLE
/// drag and drop from the (non-elevated) Explorer into it. The old WM_DROPFILES
/// protocol still works once the window lets those messages through its filter and
/// has no OLE drop target registered (Explorer would prefer it and fail).
/// </remarks>
[SupportedOSPlatform("windows")]
public static unsafe partial class ShellFileDrop
{
    public const int WmDropFiles = 0x0233;
    private const int WmCopyData = 0x004A;
    private const int WmCopyGlobalData = 0x0049;
    private const uint MsgfltAllow = 1;

    /// <summary>Makes <paramref name="hwnd"/> accept WM_DROPFILES, also from non-elevated processes.</summary>
    public static void Enable(nint hwnd)
    {
        _ = RevokeOleDropTarget(hwnd);
        foreach (var message in new[] { WmDropFiles, WmCopyData, WmCopyGlobalData })
        {
            ChangeWindowMessageFilterEx(hwnd, (uint)message, MsgfltAllow, 0);
        }

        DragAcceptFiles(hwnd, true);
    }

    /// <summary>
    /// Removes the OLE drop target WPF registers for its windows; call it again after
    /// the window has loaded in case WPF registered one late.
    /// </summary>
    /// <returns>False when there was no drop target to remove.</returns>
    public static bool RevokeOleDropTarget(nint hwnd) => RevokeDragDrop(hwnd) == 0;

    /// <summary>Reads and releases the file list of a WM_DROPFILES message.</summary>
    public static IReadOnlyList<string> GetFiles(nint hDrop)
    {
        try
        {
            var count = DragQueryFile(hDrop, uint.MaxValue, null, 0);
            var files = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(hDrop, i, null, 0);
                if (length == 0)
                {
                    continue;
                }

                var buffer = new char[length + 1];
                fixed (char* p = buffer)
                {
                    length = DragQueryFile(hDrop, i, p, (uint)buffer.Length);
                }

                files.Add(new string(buffer, 0, (int)length));
            }

            return files;
        }
        finally
        {
            DragFinish(hDrop);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial void DragAcceptFiles(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    private static partial uint DragQueryFile(nint hDrop, uint index, char* file, uint length);

    [LibraryImport("shell32.dll")]
    private static partial void DragFinish(nint hDrop);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);

    [LibraryImport("ole32.dll")]
    private static partial int RevokeDragDrop(nint hwnd);
}
