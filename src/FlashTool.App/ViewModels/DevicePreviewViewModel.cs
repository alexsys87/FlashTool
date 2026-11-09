using FlashTool.App.Localization;
using FlashTool.Core.Devices;
using FlashTool.Core.Imaging;
using FlashTool.Core.Partitioning;

namespace FlashTool.App.ViewModels;

/// <summary>
/// What is on a device right now (partition table, file systems, volume labels), so that it is seen
/// before the device is overwritten: the cheapest protection against picking the wrong one.
/// </summary>
public sealed class DevicePreviewViewModel(IDiskManager diskManager) : LocalizedViewModel
{
    private const int MaxDialogPartitions = 8;

    private int _request;
    private DeviceItemViewModel? _device;
    private ImageInfo? _info;
    private string? _error;
    private bool _loading;

    /// <summary>False when no device is selected, so there is nothing to show.</summary>
    public bool IsVisible => _device != null;

    /// <summary>One line: what kind of content the device holds, or that it is still being read, or why that failed.</summary>
    public string Summary
    {
        get
        {
            if (_device is null)
            {
                return string.Empty;
            }

            if (_loading)
            {
                return Loc.Get("Preview_Loading");
            }

            if (_error is not null)
            {
                return Loc.Format("Preview_Failed", _error);
            }

            return _info is { } info ? Describe(info) : string.Empty;
        }
    }

    public IReadOnlyList<PartitionRow> Partitions => _info?.Partitions
        .Select(p => new PartitionRow(
            p.Number.ToString(Loc.Instance.Culture),
            p.TypeDescription,
            p.FileSystem == FileSystemType.Unknown ? "—" : FileSystemName(p.FileSystem),
            Formatting.Size(p.Length),
            LabelOf(p)))
        .ToList() ?? [];

    public bool HasPartitions => Partitions.Count > 0;

    /// <summary>
    /// The same information as plain lines for a confirmation dialog. Empty while it is still being read
    /// or when it could not be read: the dialog then simply says less, it never waits.
    /// </summary>
    public string DialogText
    {
        get
        {
            if (_loading || _error is not null || _info is not { } info)
            {
                return string.Empty;
            }

            var lines = new List<string> { Describe(info) };
            lines.AddRange(info.Partitions.Take(MaxDialogPartitions).Select(PartitionLine));
            if (info.Partitions.Count > MaxDialogPartitions)
            {
                lines.Add(Loc.Format("Preview_More", info.Partitions.Count - MaxDialogPartitions));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Starts reading what is on <paramref name="device"/>; the result replaces whatever was shown before.
    /// Null clears the preview. A newer call makes the result of an older one obsolete.
    /// </summary>
    public Task LoadAsync(DeviceItemViewModel? device)
    {
        var request = ++_request;
        _device = device;
        _info = null;
        _error = null;
        _loading = device != null;
        OnPropertyChanged(string.Empty);

        return device is null ? Task.CompletedTask : ReadAsync(device, request);
    }

    private async Task ReadAsync(DeviceItemViewModel device, int request)
    {
        ImageInfo? info = null;
        string? error = null;
        try
        {
            info = await Task.Run(() =>
            {
                using var disk = diskManager.Open(device.Info, DiskAccessMode.Read);
                return ImageInspector.InspectDevice(disk);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = Formatting.Error(ex);
        }

        if (request != _request)
        {
            return;
        }

        _info = info;
        _error = error;
        _loading = false;
        OnPropertyChanged(string.Empty);
    }

    private static string Describe(ImageInfo info) => info.Kind switch
    {
        ImageKind.MbrDisk or ImageKind.GptDisk or ImageKind.HybridIso when info.TableType != PartitionTableType.None =>
            Loc.Format("Preview_Partitioned", info.TableType.ToString().ToUpperInvariant(), info.Partitions.Count),
        ImageKind.Superfloppy => Loc.Format("Preview_Superfloppy", WholeVolume(info)),
        ImageKind.Iso9660 or ImageKind.HybridIso => Loc.Get("Preview_Iso"),
        _ => Loc.Get("Preview_Unknown"),
    };

    private static string WholeVolume(ImageInfo info) =>
        info.RootLabel.Length > 0
            ? $"{FileSystemName(info.RootFileSystem)} {Loc.Format("Preview_LabelFormat", info.RootLabel)}"
            : FileSystemName(info.RootFileSystem);

    private static string PartitionLine(PartitionInfo p)
    {
        var kind = p.FileSystem == FileSystemType.Unknown ? p.TypeDescription : FileSystemName(p.FileSystem);
        var label = LabelOf(p);
        var labelPart = label.Length > 0 ? $" {Loc.Format("Preview_LabelFormat", label)}" : string.Empty;
        return $"  {p.Number.ToString(Loc.Instance.Culture)}. {kind}{labelPart} · {Formatting.Size(p.Length)}";
    }

    /// <summary>The volume label if the file system has one, otherwise the name from a GPT entry.</summary>
    private static string LabelOf(PartitionInfo p) => p.Label.Length > 0 ? p.Label : p.Name;

    private static string FileSystemName(FileSystemType fileSystem) => fileSystem switch
    {
        FileSystemType.Fat12 => "FAT12",
        FileSystemType.Fat16 => "FAT16",
        FileSystemType.Fat32 => "FAT32",
        FileSystemType.ExFat => "exFAT",
        FileSystemType.Ntfs => "NTFS",
        FileSystemType.Ext2 => "ext2",
        FileSystemType.Ext3 => "ext3",
        FileSystemType.Ext4 => "ext4",
        FileSystemType.Btrfs => "Btrfs",
        FileSystemType.Xfs => "XFS",
        FileSystemType.F2fs => "F2FS",
        FileSystemType.Squashfs => "SquashFS",
        FileSystemType.Swap => "swap",
        FileSystemType.Iso9660 => "ISO 9660",
        _ => "?",
    };
}
