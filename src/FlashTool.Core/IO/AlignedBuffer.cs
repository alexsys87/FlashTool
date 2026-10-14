using System.Buffers;
using System.Runtime.InteropServices;

namespace FlashTool.Core.IO;

/// <summary>
/// Native memory block aligned to a page boundary. Required for unbuffered
/// (FILE_FLAG_NO_BUFFERING) disk I/O on Windows.
/// </summary>
public sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    public const int DefaultAlignment = 4096;

    private byte* _pointer;

    public AlignedBuffer(int size, int alignment = DefaultAlignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        Size = size;
        _pointer = (byte*)NativeMemory.AlignedAlloc((nuint)size, (nuint)alignment);
        new Span<byte>(_pointer, size).Clear();
    }

    public int Size { get; }

    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_pointer == null, this);
            return new Span<byte>(_pointer, Size);
        }
    }

    public override Span<byte> GetSpan() => Span;

    public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_pointer != null)
        {
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }
    }

    /// <summary>Releases the native memory.</summary>
    public void Free() => ((IDisposable)this).Dispose();
}
