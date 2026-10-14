namespace FlashTool.Core.Sources;

public sealed class FileStreamSource : IStreamSource
{
    public FileStreamSource(string path)
    {
        Location = Path.GetFullPath(path);
        Name = Path.GetFileName(Location);
        Length = new FileInfo(Location).Length;
    }

    public string Name { get; }

    public string Location { get; }

    public long? Length { get; }

    public bool IsSeekable => true;

    public bool IsRemote => false;

    public Stream Open(CancellationToken cancellationToken = default)
        => new FileStream(Location, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
}
