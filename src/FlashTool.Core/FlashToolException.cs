namespace FlashTool.Core;

/// <summary>
/// Machine-readable error codes. The UI maps them to localized messages,
/// so the core library never has to deal with user-facing text.
/// </summary>
public enum FlashErrorCode
{
    Unknown,
    NotADiskImage,
    ImageTooLarge,
    UnexpectedEndOfImage,
    UnsupportedContainer,
    UnsupportedCompressionMethod,
    ArchiveHasNoImages,
    ContainerBackendUnavailable,
    SourceNotSeekable,
    HttpError,
    DeviceNotFound,
    DeviceAccessDenied,
    DeviceLockFailed,
    DeviceWriteProtected,
    DeviceIoError,
    TargetIsSystemDisk,
    VerificationFailed,
    PartitionTableUpdateFailed,
    FileSystemResizeFailed,
}

/// <summary>
/// Base exception for all expected (non-bug) failures raised by FlashTool.
/// </summary>
public class FlashToolException : Exception
{
    public FlashToolException(FlashErrorCode code, string? message = null, Exception? inner = null, params object?[] args)
        : base(message ?? code.ToString(), inner)
    {
        Code = code;
        Arguments = args;
    }

    public FlashErrorCode Code { get; }

    /// <summary>Optional values used to format the localized message.</summary>
    public IReadOnlyList<object?> Arguments { get; }
}

/// <summary>
/// Raised when the data read back from the target does not match the source.
/// </summary>
public sealed class VerificationFailedException : FlashToolException
{
    public VerificationFailedException(long offset)
        : base(FlashErrorCode.VerificationFailed, $"Verification failed at offset {offset}.", null, offset)
    {
        Offset = offset;
    }

    /// <summary>Byte offset of the first block that differs.</summary>
    public long Offset { get; }
}
