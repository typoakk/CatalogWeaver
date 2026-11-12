namespace MediaImporter.Images;

public sealed class ImageStorageOptions
{
    public string MediaRoot { get; set; } = "/data/media";
    public long CoverBudgetBytes { get; set; } = 220L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public int MaximumImageBytes { get; set; } = 8 * 1024 * 1024;
    public int MaximumEncodedBytes { get; set; } = 32 * 1024 * 1024;
    public int StagingRetentionHours { get; set; } = 24;
    public int ImageWorkerCount { get; set; } = 2;
    public int ImageProcessTimeoutSeconds { get; set; } = 120;
    public int ImageMemoryMiB { get; set; } = 256;
    public int ImageDiskMiB { get; set; } = 512;
    public int ImageThreads { get; set; } = 2;
}

public sealed record ImageDescription(string ContentType, uint Width, uint Height,
    bool HasAlpha, int Frames, uint Depth);

public interface IImporterImageCodec
{
    Task<ImageDescription> ValidateOriginalAsync(string path, CancellationToken cancellationToken);
    Task EncodeAsync(string originalPath, string outputPath, ImageEncodingProfile profile, CancellationToken cancellationToken);
    Task ValidateEncodedAsync(string originalPath, string candidatePath, ImageEncodingProfile profile, CancellationToken cancellationToken);
}

public sealed record PreparedImage(
    string WorkingDirectory,
    string OriginalPath,
    string SelectedPath,
    string DestinationDirectory,
    string Hash,
    string ContentType,
    long Bytes,
    string RequestedProfile,
    string ActualProfile,
    string[] Warnings);

public sealed record PublishedImage(string Hash, string ContentType, long Bytes,
    string RelativePath, string FullPath);

public sealed class ImageStorageFullException(string message) : IOException(message);
