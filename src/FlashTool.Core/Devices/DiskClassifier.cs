namespace FlashTool.Core.Devices;

/// <summary>
/// Decides which icon/category a disk gets. Kept platform-neutral so the
/// heuristics can be unit-tested.
/// </summary>
public static class DiskClassifier
{
    private static readonly string[] CardReaderHints =
    [
        "SD", "SDHC", "SDXC", "MICROSD", "MMC", "CARD", "READER", "MULTI", "CF", "MS PRO", "XD",
    ];

    public static DiskKind Classify(DiskBusType bus, bool removableMedia, string vendor, string model)
    {
        if (bus is DiskBusType.Sd or DiskBusType.Mmc)
        {
            return DiskKind.SdCard;
        }

        if (bus is DiskBusType.Usb or DiskBusType.Ieee1394)
        {
            if (LooksLikeCardReader(vendor, model))
            {
                return DiskKind.SdCard;
            }

            // Some fast USB sticks report "fixed" media, but almost all
            // external HDD/SSD enclosures do, so this is the best signal available.
            return removableMedia ? DiskKind.UsbFlash : DiskKind.ExternalDrive;
        }

        return removableMedia ? DiskKind.UsbFlash : DiskKind.InternalDrive;
    }

    public static bool LooksLikeCardReader(string vendor, string model)
    {
        var text = $" {vendor} {model} ".ToUpperInvariant();
        foreach (var hint in CardReaderHints)
        {
            // Match whole words only: "SD" must not match "SanDisk".
            var index = text.IndexOf(hint, StringComparison.Ordinal);
            while (index >= 0)
            {
                var before = text[index - 1];
                var afterIndex = index + hint.Length;
                var after = afterIndex < text.Length ? text[afterIndex] : ' ';
                if (!char.IsLetter(before) && !char.IsLetter(after))
                {
                    return true;
                }

                index = text.IndexOf(hint, index + 1, StringComparison.Ordinal);
            }
        }

        return false;
    }
}
