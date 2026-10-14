using CommunityToolkit.Mvvm.ComponentModel;
using FlashTool.App.Localization;
using FlashTool.Core.Containers;
using FlashTool.Core.Imaging;
using FlashTool.Core.Partitioning;

namespace FlashTool.App.ViewModels;

/// <summary>A row of the partition table preview.</summary>
public sealed record PartitionRow(string Number, string Type, string FileSystem, string Size, string Name);

/// <summary>A file inside the opened image/archive together with its inspection result.</summary>
public sealed partial class ImageEntryViewModel : LocalizedViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindText), nameof(IsDiskImage), nameof(Partitions), nameof(DisplayText), nameof(IsNonHybridIso), nameof(IsUnknown))]
    private ImageInfo? _info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindText), nameof(DisplayText))]
    private bool _isChecking;

    public ImageEntryViewModel(ImageEntry entry) => Entry = entry;

    public ImageEntry Entry { get; }

    public string Name => Entry.Name;

    public string SizeText => Entry.Length.HasValue ? Formatting.Size(Entry.Length) : Loc.Get("Image_SizeUnknown");

    public string KindText => IsChecking ? Loc.Get("Write_EntryChecking") : Info is { } i ? Formatting.ImageKind(i.Kind) : string.Empty;

    public bool IsDiskImage => Info?.IsDiskImage == true;

    public bool IsUnknown => Info is { IsDiskImage: false };

    public bool IsNonHybridIso => Info?.IsNonHybridIso == true;

    public string DisplayText => $"{Name}  ·  {SizeText}  ·  {KindText}";

    public IReadOnlyList<PartitionRow> Partitions => Info?.Partitions
        .Select(p => new PartitionRow(
            p.Number.ToString(Loc.Instance.Culture),
            p.TypeDescription,
            p.FileSystem == FileSystemType.Unknown ? "—" : p.FileSystem.ToString(),
            Formatting.Size(p.Length),
            p.Name))
        .ToList() ?? [];
}
