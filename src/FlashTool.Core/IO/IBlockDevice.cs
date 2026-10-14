namespace FlashTool.Core.IO;

/// <summary>
/// Random-access, sector-addressed storage (a physical disk or an image file).
/// Offsets and lengths passed to <see cref="Read"/> and <see cref="Write"/>
/// must be multiples of <see cref="SectorSize"/>; buffers should be
/// aligned (see <see cref="AlignedBuffer"/>) because physical disks are
/// opened without OS caching.
/// </summary>
public interface IBlockDevice : IDisposable
{
    /// <summary>Total size in bytes.</summary>
    long Length { get; }

    /// <summary>Logical sector size in bytes (usually 512 or 4096).</summary>
    int SectorSize { get; }

    bool CanWrite { get; }

    /// <summary>Reads exactly <c>buffer.Length</c> bytes at <paramref name="offset"/>.</summary>
    void Read(long offset, Span<byte> buffer);

    /// <summary>Writes the whole buffer at <paramref name="offset"/>.</summary>
    void Write(long offset, ReadOnlySpan<byte> buffer);

    /// <summary>Flushes any write caches down to the media.</summary>
    void Flush();
}
