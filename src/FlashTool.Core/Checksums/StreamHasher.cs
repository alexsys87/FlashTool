using System.Security.Cryptography;
using FlashTool.Core.Operations;

namespace FlashTool.Core.Checksums;

internal static class StreamHasher
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>SHA-256 of everything that can be read from <paramref name="stream"/>, reporting progress on the way.</summary>
    public static byte[] Sha256(Stream stream, ProgressReporter reporter, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long done = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            done += read;
            reporter.Report(done);
        }

        reporter.Report(done, force: true);
        return hash.GetHashAndReset();
    }
}
