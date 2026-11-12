using System.Collections.Concurrent;
using System.Security.Cryptography;
using ImageMagick;

namespace MediaImporter.Images;

public sealed class ImagePipeline : IDisposable
{
    private readonly ImageStorageOptions options;
    private readonly IImporterImageCodec codec;
    private readonly ImageFileStore files;
    private readonly EncoderRegistry encoders;
    private readonly bool ownsEncoders;
    private readonly ConcurrentDictionary<string, FileStream> stageLeases = new(StringComparer.Ordinal);

    public ImagePipeline(ImageStorageOptions options, IImporterImageCodec codec, EncoderRegistry? encoders = null)
    {
        this.options = options;
        this.codec = codec;
        this.encoders = encoders ?? EncoderRegistry.Bundled();
        ownsEncoders = encoders is null;
        files = new ImageFileStore(options);
    }

    public async Task<PreparedImage> PrepareAsync(string destinationDirectory, string profile,
        Func<CancellationToken, Task<byte[]>> download, CancellationToken cancellationToken,
        ImageEncodingProfile? resolvedProfile = null)
    {
        var encoding = profile == "original" ? null : resolvedProfile ?? encoders.Resolve(profile);
        if (encoding is not null)
        {
            ImageEncodingProfile.Validate(encoding);
            if (encoding.Name != profile) throw new InvalidDataException("Image profile name differs from its captured instructions.");
        }
        var destination = files.ResolveDestination(destinationDirectory);
        var workingDirectory = files.CreateWorkingDirectory();
        var originalPath = Path.Combine(workingDirectory, "original.tmp");
        try
        {
            stageLeases[workingDirectory] = files.AcquireWorkingLease(workingDirectory);
            files.CheckFreeSpace(options.MaximumImageBytes);
            var source = await download(cancellationToken);
            if (source.Length is < 1 || source.Length > options.MaximumImageBytes)
                throw new InvalidDataException("Source image exceeds the image byte limit.");
            files.CheckFreeSpace(source.Length);
            await WriteDurableAsync(originalPath, source, cancellationToken);
            ImageDescription original;
            try
            {
                original = await codec.ValidateOriginalAsync(originalPath, cancellationToken);
            }
            catch (MagickException error)
            {
                throw new InvalidDataException("Source image failed to decode.", error);
            }
            var warnings = new List<string>();
            var selectedPath = originalPath;
            var selectedType = original.ContentType;
            var actualProfile = "original";

            if (encoding is not null)
            {
                var candidate = await PrepareEncodedAsync(originalPath, workingDirectory, encoding,
                    warnings, cancellationToken);
                if (candidate is not null)
                {
                    selectedPath = candidate;
                    selectedType = encoding.ContentType;
                    actualProfile = profile;
                }
                else
                    warnings.Add("image_fallback_original");
            }

            await using var selected = File.OpenRead(selectedPath);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(selected, cancellationToken));
            return new PreparedImage(workingDirectory, originalPath, selectedPath, destination,
                hash, selectedType, selected.Length, profile, actualProfile, warnings.ToArray());
        }
        catch
        {
            ReleaseLease(workingDirectory);
            files.RemoveWorkingDirectory(workingDirectory);
            throw;
        }
    }

    public Task<PublishedImage> PublishAsync(PreparedImage prepared, CancellationToken cancellationToken)
        => files.PublishAsync(prepared, cancellationToken);

    /// <summary>Called only after database references to the selected file have committed.</summary>
    public Task CompleteAsync(PreparedImage prepared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseLease(prepared.WorkingDirectory);
        files.RemoveWorkingDirectory(prepared.WorkingDirectory);
        return Task.CompletedTask;
    }

    /// <summary>Discards an abandoned preview or cancelled import before it publishes database references.</summary>
    public Task DiscardAsync(PreparedImage prepared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseLease(prepared.WorkingDirectory);
        files.RemoveWorkingDirectory(prepared.WorkingDirectory);
        return Task.CompletedTask;
    }

    /// <summary>Retains the staged files of an unsuccessful database attempt for later recovery.</summary>
    public Task ReleaseAsync(PreparedImage prepared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseLease(prepared.WorkingDirectory);
        return Task.CompletedTask;
    }

    public Task DeletePublishedAsync(string relativePath, CancellationToken cancellationToken)
        => files.DeletePublishedAsync(relativePath, cancellationToken);

    public Task<int> RecoverStagingAsync(CancellationToken cancellationToken)
        => RecoverStagingAsync(TimeSpan.FromHours(options.StagingRetentionHours), cancellationToken);

    public Task<int> RecoverStagingAsync(TimeSpan minimumAge, CancellationToken cancellationToken)
        => files.RecoverStagingAsync(minimumAge, cancellationToken);

    public void Dispose()
    {
        foreach (var workingDirectory in stageLeases.Keys)
            ReleaseLease(workingDirectory);
        if (ownsEncoders) encoders.Dispose();
    }

    private void ReleaseLease(string workingDirectory)
    {
        if (stageLeases.TryRemove(workingDirectory, out var lease))
            lease.Dispose();
    }

    private async Task<string?> PrepareEncodedAsync(string originalPath, string workingDirectory, ImageEncodingProfile profile,
        List<string> warnings, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var candidate = Path.Combine(workingDirectory, $"encoded-{attempt}.tmp");
            try
            {
                await codec.EncodeAsync(originalPath, candidate, profile, cancellationToken);
                if (!File.Exists(candidate) || new FileInfo(candidate).Length is < 1
                    || new FileInfo(candidate).Length > options.MaximumEncodedBytes)
                    throw new InvalidDataException("Encoded image exceeds the encoded byte limit.");
            }
            catch (Exception error) when (IsImageFailure(error))
            {
                warnings.Add($"image_encode_attempt_{attempt}:{error.GetType().Name}");
                File.Delete(candidate);
                continue;
            }

            for (var validation = 1; validation <= 2; validation++)
            {
                try
                {
                    await codec.ValidateEncodedAsync(originalPath, candidate, profile, cancellationToken);
                    return candidate;
                }
                catch (Exception error) when (IsImageFailure(error))
                {
                    warnings.Add($"image_check_attempt_{attempt}_{validation}:{error.GetType().Name}");
                }
            }
            File.Delete(candidate);
        }
        return null;
    }

    private static bool IsImageFailure(Exception error) => error is IOException or InvalidDataException
        or MagickException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException;

    private static async Task WriteDurableAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(true);
    }
}
