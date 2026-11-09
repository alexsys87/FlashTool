using FlashTool.Core.Containers;
using FlashTool.Core.Operations;

namespace FlashTool.App.Services;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum LanguageMode
{
    System,
    English,
    Russian,
}

/// <summary>User preferences persisted between runs.</summary>
public sealed class AppSettings
{
    /// <summary>Format version of settings.json, see <see cref="SettingsService"/> migrations.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; }

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public LanguageMode Language { get; set; } = LanguageMode.System;

    public bool ShowNonRemovableDisks { get; set; }

    public bool VerifyAfterWrite { get; set; } = true;

    public bool ExpandLastPartition { get; set; }

    /// <summary>Experimental: write only the blocks that differ from what the device holds. Off by default.</summary>
    public bool DifferentialWrite { get; set; }

    public bool EjectAfterWrite { get; set; }

    public bool VerifyAfterRead { get; set; } = true;

    /// <summary>Save <c>&lt;image&gt;.sha256</c> next to every created image.</summary>
    public bool CreateChecksumFile { get; set; } = true;

    public bool OnlyAllocatedPartitions { get; set; } = true;

    /// <summary>Store free ext2/3/4 blocks as zeros when creating an image. Off by default: the image is then not an exact copy.</summary>
    public bool SkipFreeBlocks { get; set; }

    /// <summary>Write and verify by a .bmap found next to the image. Off by default.</summary>
    public bool UseBmap { get; set; }

    /// <summary>Save a .bmap next to every created image. Off by default.</summary>
    public bool CreateBmap { get; set; }

    public ContainerFormat ReadFormat { get; set; } = ContainerFormat.Xz;

    public CompressionPreset ReadCompression { get; set; } = CompressionPreset.Normal;

    public string? LastImageFolder { get; set; }

    public string? LastSaveFolder { get; set; }

    public WindowPlacement? Window { get; set; }

    /// <summary>Type name of the last opened page.</summary>
    public string? LastPage { get; set; }

    public bool NavigationPaneOpen { get; set; } = true;

    public ImageSelection WriteSelection { get; set; } = new();

    public ImageSelection VerifySelection { get; set; } = new();

    public string? ReadDeviceKey { get; set; }

    public string? ReadDestination { get; set; }

    /// <summary>Shows the "Erase" page. Off by default; turning it on asks for confirmation.</summary>
    public bool EraseEnabled { get; set; }

    public EraseMode EraseMode { get; set; } = EraseMode.Quick;

    public ErasePattern ErasePattern { get; set; } = ErasePattern.Random;

    public bool EraseVerify { get; set; } = true;

    public EraseFileSystem EraseFileSystem { get; set; } = EraseFileSystem.Auto;

    public ErasePartitionScheme ErasePartitionScheme { get; set; } = ErasePartitionScheme.Mbr;

    public string EraseLabel { get; set; } = string.Empty;

    public string? EraseDeviceKey { get; set; }

    /// <summary>Shows the "Device test" page. Off by default.</summary>
    public bool DeviceTestEnabled { get; set; }

    public DeviceTestMode DeviceTestMode { get; set; } = DeviceTestMode.Read;

    public EraseFileSystem DeviceTestFileSystem { get; set; } = EraseFileSystem.Auto;

    public string? DeviceTestDeviceKey { get; set; }
}

/// <summary>Main window bounds (in the "normal" state) and whether it was maximized.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>What a page had selected: the image (path or URL), the file inside it and the device.</summary>
public sealed class ImageSelection
{
    public string? Source { get; set; }

    public string? Entry { get; set; }

    public string? DeviceKey { get; set; }
}
