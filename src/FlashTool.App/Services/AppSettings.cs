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
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public LanguageMode Language { get; set; } = LanguageMode.System;

    public bool ShowNonRemovableDisks { get; set; }

    public bool VerifyAfterWrite { get; set; } = true;

    public bool ExpandLastPartition { get; set; } = true;

    public bool VerifyAfterRead { get; set; } = true;

    public bool OnlyAllocatedPartitions { get; set; } = true;

    public ContainerFormat ReadFormat { get; set; } = ContainerFormat.Xz;

    public CompressionPreset ReadCompression { get; set; } = CompressionPreset.Normal;

    public string? LastImageFolder { get; set; }

    public string? LastSaveFolder { get; set; }
}
