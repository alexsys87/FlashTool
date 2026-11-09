using System.IO.Hashing;
using FlashTool.Core.IO;

namespace FlashTool.Core.Operations;

/// <summary>
/// Writes only the planned ranges of each chunk and remembers a fingerprint of every piece it
/// wrote, so the device can be read back and compared piece by piece.
/// </summary>
internal sealed class PlannedWriter
{
    private readonly List<(long Start, long End)> _plan;
    private readonly List<(long Offset, int Length, ulong Hash)> _pieces = [];
    private int _index;

    /// <param name="plan">Sorted, merged, sector-aligned ranges [Start, End).</param>
    public PlannedWriter(List<(long Start, long End)> plan) => _plan = plan;

    /// <summary>Bytes that went to the device.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>Records data written elsewhere (the first chunk, which goes out last and whole).</summary>
    public void Record(long offset, ReadOnlySpan<byte> data, int writtenLength)
    {
        _pieces.Add((offset, data.Length, XxHash3.HashToUInt64(data)));
        BytesWritten += writtenLength;
    }

    /// <summary>
    /// Writes the planned parts of a chunk. <paramref name="aligned"/> is the chunk padded to whole
    /// sectors; only its first <paramref name="count"/> bytes are image data.
    /// </summary>
    /// <param name="differential">When set, each planned piece is compared with the device and only what differs is written.</param>
    public void Write(IBlockDevice device, long offset, ReadOnlySpan<byte> aligned, int count, DifferentialWriter? differential = null)
    {
        var end = offset + aligned.Length;
        while (_index < _plan.Count && _plan[_index].End <= offset)
        {
            _index++;
        }

        for (var i = _index; i < _plan.Count && _plan[i].Start < end; i++)
        {
            var start = Math.Max(_plan[i].Start, offset);
            var stop = Math.Min(_plan[i].End, end);
            var piece = aligned[(int)(start - offset)..(int)(stop - offset)];
            if (differential != null)
            {
                differential.Write(start, piece);
            }
            else
            {
                device.Write(start, piece);
            }

            BytesWritten += stop - start;

            var dataStop = Math.Min(stop, offset + count);
            if (dataStop > start)
            {
                var data = aligned[(int)(start - offset)..(int)(dataStop - offset)];
                _pieces.Add((start, data.Length, XxHash3.HashToUInt64(data)));
            }
        }
    }

    /// <summary>Reads every written piece back and compares it with what was written.</summary>
    public async Task VerifyAsync(IBlockDevice device, int chunk, int depth, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var pieces = _pieces.OrderBy(p => p.Offset).ToList();
        var total = pieces.Sum(p => (long)p.Length);
        var reporter = new ProgressReporter(progress, OperationPhase.Verifying, total);
        var produced = 0;
        var consumed = 0;

        await TransferPipeline.RunAsync(
            chunk,
            depth,
            buffer =>
            {
                if (produced >= pieces.Count)
                {
                    return 0;
                }

                var (offset, length, _) = pieces[produced++];
                device.Read(offset, buffer.Span[..(int)StreamExtensions.AlignUp(length, device.SectorSize)]);
                return length;
            },
            (buffer, count, position) =>
            {
                var (offset, _, hash) = pieces[consumed++];
                if (XxHash3.HashToUInt64(buffer.Span[..count]) != hash)
                {
                    throw new VerificationFailedException(offset);
                }

                reporter.Report(position + count);
            },
            cancellationToken).ConfigureAwait(false);

        reporter.Report(total, force: true);
    }
}
