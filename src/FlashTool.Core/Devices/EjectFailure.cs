namespace FlashTool.Core.Devices;

/// <summary>Why <see cref="IDiskManager.Eject"/> failed; the first argument of the DeviceEjectFailed error.</summary>
public enum EjectFailure
{
    /// <summary>A program still has files open on the device.</summary>
    InUse,

    /// <summary>The device cannot be ejected (fixed disk, no removable media).</summary>
    NotSupported,

    /// <summary>The OS refused for another reason.</summary>
    Refused,
}
