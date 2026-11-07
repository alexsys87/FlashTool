using System.Security.Cryptography;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Checksums;

/// <summary>How the check of the source file against its expected SHA-256 ended.</summary>
public enum SourceChecksumStatus
{
    /// <summary>No value was given, so nothing was checked.</summary>
    NotChecked,

    /// <summary>The source matches the expected value.</summary>
    Verified,

    /// <summary>
    /// A value was given but the source could not be hashed as a whole, for example a zip or 7z that
    /// is read by URL with random access.
    /// </summary>
    Unverifiable,
}

/// <summary>
/// Checks the bytes of a source file as published (the archive, not the unpacked image) against an
/// expected SHA-256. A local file is read through once before anything is written; a download is
/// hashed while it streams (see <see cref="HashingStreamSource"/>) and judged at the end of the stream.
/// </summary>
internal sealed class SourceChecksumCheck
{
    private const int BufferSize = 1024 * 1024;

    private readonly IStreamSource _source;
    private readonly byte[] _expected;

    public SourceChecksumCheck(IStreamSource source, byte[] expected)
    {
        _source = source;
        _expected = expected;
    }

    public SourceChecksumStatus Status { get; private set; } = SourceChecksumStatus.Unverifiable;

    /// <summary>
    /// Hashes a local source and throws if it does not match, before the device has been touched.
    /// Remote sources are left for <see cref="VerifyStreamed"/>.
    /// </summary>
    public async Task VerifyUpFrontAsync(IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_source.IsRemote)
        {
            return;
        }

        var actual = await Task.Run(() => Hash(progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!actual.AsSpan().SequenceEqual(_expected))
        {
            throw Mismatch(actual, FlashErrorCode.SourceChecksumMismatch);
        }

        Status = SourceChecksumStatus.Verified;
    }

    /// <summary>
    /// For a source that streamed in: call once it has been read to its end. Throws on a mismatch;
    /// when the stream did not cover the whole file the status stays <see cref="SourceChecksumStatus.Unverifiable"/>.
    /// </summary>
    public void VerifyStreamed()
    {
        if (Status == SourceChecksumStatus.Verified || _source is not HashingStreamSource hashing || !hashing.TryGetSha256(out var actual))
        {
            return;
        }

        if (!actual.AsSpan().SequenceEqual(_expected))
        {
            throw Mismatch(actual, FlashErrorCode.SourceChecksumMismatchAfterWrite);
        }

        Status = SourceChecksumStatus.Verified;
    }

    private byte[] Hash(IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var stream = _source.Open(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var reporter = new ProgressReporter(progress, OperationPhase.CheckingSource, _source.Length);
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

    private FlashToolException Mismatch(byte[] actual, FlashErrorCode code) =>
        new(code, "The source file does not match the expected SHA-256.", null, Sha256Hex.Format(_expected), Sha256Hex.Format(actual));
}
