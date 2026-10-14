using System.Collections.Concurrent;
using FlashTool.Core.IO;

namespace FlashTool.Core.Operations;

/// <summary>
/// Two-stage producer/consumer copy loop with a fixed pool of aligned
/// buffers: the producer (read + decompress / download) runs concurrently
/// with the consumer (hash + write), so the slowest side sets the pace.
/// </summary>
internal static class TransferPipeline
{
    /// <param name="chunkSize">Buffer size; every chunk except the last one is full.</param>
    /// <param name="depth">Number of buffers in flight.</param>
    /// <param name="produce">Fills a buffer and returns the byte count; fewer than <paramref name="chunkSize"/> bytes means end of data.</param>
    /// <param name="consume">Processes a chunk: (buffer, count, offset of the chunk).</param>
    /// <returns>Total bytes transferred.</returns>
    public static async Task<long> RunAsync(
        int chunkSize,
        int depth,
        Func<AlignedBuffer, int> produce,
        Action<AlignedBuffer, int, long> consume,
        CancellationToken cancellationToken)
    {
        var buffers = new List<AlignedBuffer>(depth);
        using var free = new BlockingCollection<AlignedBuffer>();
        using var filled = new BlockingCollection<(AlignedBuffer Buffer, int Count)>(depth);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            for (var i = 0; i < depth; i++)
            {
                var buffer = new AlignedBuffer(chunkSize);
                buffers.Add(buffer);
                free.Add(buffer, CancellationToken.None);
            }

            var producer = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        while (true)
                        {
                            var buffer = free.Take(cts.Token);
                            var count = produce(buffer);
                            if (count > 0)
                            {
                                filled.Add((buffer, count), cts.Token);
                            }

                            if (count < chunkSize)
                            {
                                break;
                            }
                        }
                    }
                    catch
                    {
                        cts.Cancel();
                        throw;
                    }
                    finally
                    {
                        filled.CompleteAdding();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            var consumer = Task.Factory.StartNew(
                () =>
                {
                    long offset = 0;
                    try
                    {
                        foreach (var (buffer, count) in filled.GetConsumingEnumerable(cts.Token))
                        {
                            consume(buffer, count, offset);
                            offset += count;
                            free.Add(buffer, CancellationToken.None);
                        }
                    }
                    catch
                    {
                        cts.Cancel();
                        throw;
                    }

                    return offset;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            try
            {
                await Task.WhenAll(producer, consumer).ConfigureAwait(false);
            }
            catch
            {
                // Prefer the real failure over the cancellation it caused on the other side.
                var real = new[] { producer.Exception, consumer.Exception }
                    .Where(e => e != null)
                    .SelectMany(e => e!.InnerExceptions)
                    .FirstOrDefault(e => e is not OperationCanceledException);
                if (real != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(real).Throw();
                }

                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            return consumer.Result;
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                buffer.Free();
            }
        }
    }
}
