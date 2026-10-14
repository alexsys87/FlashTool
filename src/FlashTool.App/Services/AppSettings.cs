using FlashTool.Core.Containers;

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

    public bool EjectAfterWrite { get; set; }

    public bool VerifyAfterRead { get; set; } = true;

    public bool OnlyAllocatedPartitions { get; set; } = true;

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
