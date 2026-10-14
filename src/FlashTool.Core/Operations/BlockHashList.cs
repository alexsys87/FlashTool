using System.IO.Hashing;

namespace FlashTool.Core.Operations;

/// <summary>
/// Per-block fingerprints of a data stream, recorded while copying and
/// compared while reading back. Keeps memory tiny (8 bytes per block) and
/// pinpoints the first corrupted block without re-reading the source,
/// which matters for downloads and archives.
/// </summary>
public sealed class BlockHashList
{
    private readonly List<ulong> _hashes = [];

    public BlockHashList(int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        BlockSize = blockSize;
    }

    public int BlockSize { get; }

    public long TotalLength { get; private set; }

    public int Count => _hashes.Count;

    /// <summary>Appends the next block. Only the final block may be shorter than <see cref="BlockSize"/>.</summary>
    public void Append(ReadOnlySpan<byte> block)
    {
        if (block.Length > BlockSize)
        {
            throw new ArgumentException("Block is larger than the block size.", nameof(block));
        }

        if (TotalLength % BlockSize != 0)
        {
            throw new InvalidOperationException("A short block was already appended; it must be the last one.");
        }

        _hashes.Add(XxHash3.HashToUInt64(block));
        TotalLength += block.Length;
    }

    /// <summary>Checks the block that starts at <paramref name="offset"/>.</summary>
    public bool Matches(long offset, ReadOnlySpan<byte> block)
    {
        if (offset % BlockSize != 0)
        {
            throw new ArgumentException("Offset must be block aligned.", nameof(offset));
        }

        var index = (int)(offset / BlockSize);
        if (index >= _hashes.Count)
        {
            return false;
        }

        var expectedLength = (int)Math.Min(BlockSize, TotalLength - offset);
        return block.Length == expectedLength && XxHash3.HashToUInt64(block) == _hashes[index];
    }
}
