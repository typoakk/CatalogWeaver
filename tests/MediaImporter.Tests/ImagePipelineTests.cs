using ImageMagick;
using MediaImporter.Images;
using SkiaSharp;

namespace MediaImporter.Tests;

public sealed class ImagePipelineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "catalogweaver-images-" + Guid.NewGuid().ToString("N"));
    private readonly List<ImagePipeline> pipelines = [];

    [Fact]
    public async Task Original_profile_downloads_once_and_preserves_exact_bytes()
    {
        var codec = new ScriptedCodec();
        var pipeline = Pipeline(codec);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var downloads = 0;
        var prepared = await pipeline.PrepareAsync("shikimori", "original", _ =>
        {
            downloads++;
            return Task.FromResult(bytes);
        }, default);

        var published = await pipeline.PublishAsync(prepared, default);
        Assert.Equal(1, downloads);
        Assert.Equal(0, codec.Encodes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(published.FullPath));
        Assert.Equal("shikimori/" + prepared.Hash, published.RelativePath);
        Assert.True(File.Exists(prepared.OriginalPath));
        await pipeline.CompleteAsync(prepared, default);
        Assert.False(Directory.Exists(prepared.WorkingDirectory));
        Assert.True(File.Exists(published.FullPath));
    }

    [Theory]
    [InlineData("avif80")]
    [InlineData("webp80")]
    [InlineData("jpeg85")]
    [InlineData("png-lossless")]
    public async Task First_validation_failure_rechecks_same_file_without_reencoding(string profile)
    {
        var codec = new ScriptedCodec { FailedChecks = 1 };
        var pipeline = Pipeline(codec);
        var prepared = await pipeline.PrepareAsync("steam", profile, Download, default);

        Assert.Equal(1, codec.Encodes);
        Assert.Equal(2, codec.CheckedPaths.Count);
        Assert.Equal(codec.CheckedPaths[0], codec.CheckedPaths[1]);
        Assert.Equal(profile, prepared.ActualProfile);
        Assert.True(File.Exists(prepared.OriginalPath));
    }

    [Fact]
    public async Task Two_failed_checks_reencode_original_then_validate_new_file()
    {
        var codec = new ScriptedCodec { FailedChecks = 2 };
        var pipeline = Pipeline(codec);
        var prepared = await pipeline.PrepareAsync("steam", "avif80", Download, default);

        Assert.Equal(2, codec.Encodes);
        Assert.Equal(3, codec.CheckedPaths.Count);
        Assert.Equal(codec.CheckedPaths[0], codec.CheckedPaths[1]);
        Assert.NotEqual(codec.CheckedPaths[1], codec.CheckedPaths[2]);
        Assert.False(File.Exists(codec.CheckedPaths[0]));
        Assert.True(File.Exists(prepared.SelectedPath));
        Assert.True(File.Exists(prepared.OriginalPath));
    }

    [Fact]
    public async Task Exhausted_checks_keep_valid_original_and_remove_both_failed_candidates()
    {
        var codec = new ScriptedCodec { FailedChecks = int.MaxValue };
        var pipeline = Pipeline(codec);
        var prepared = await pipeline.PrepareAsync("steam", "avif80", Download, default);

        Assert.Equal(2, codec.Encodes);
        Assert.Equal(4, codec.CheckedPaths.Count);
        Assert.Equal("original", prepared.ActualProfile);
        Assert.Equal(prepared.OriginalPath, prepared.SelectedPath);
        Assert.Contains("image_fallback_original", prepared.Warnings);
        Assert.Single(Directory.EnumerateFiles(prepared.WorkingDirectory),
            path => !path.EndsWith(".lease", StringComparison.Ordinal));
        var published = await pipeline.PublishAsync(prepared, default);
        await pipeline.CompleteAsync(prepared, default);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(published.FullPath));
    }

    [Fact]
    public async Task Encoding_failure_retries_twice_and_keeps_original()
    {
        var codec = new ScriptedCodec { ThrowOnEncoding = true };
        var prepared = await Pipeline(codec).PrepareAsync("steam", "avif80", Download, default);

        Assert.Equal(2, codec.Encodes);
        Assert.Empty(codec.CheckedPaths);
        Assert.Equal("original", prepared.ActualProfile);
        Assert.Single(Directory.EnumerateFiles(prepared.WorkingDirectory),
            path => !path.EndsWith(".lease", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successful_larger_avif_is_published_and_original_remains_until_commit_cleanup()
    {
        var codec = new ScriptedCodec();
        var pipeline = Pipeline(codec);
        var prepared = await pipeline.PrepareAsync("steam", "avif80", Download, default);
        var published = await pipeline.PublishAsync(prepared, default);

        Assert.Equal("avif80", prepared.ActualProfile);
        Assert.Equal("image/avif", published.ContentType);
        Assert.True(published.Bytes > new FileInfo(prepared.OriginalPath).Length);
        Assert.True(File.Exists(prepared.OriginalPath));
        Assert.True(File.Exists(prepared.SelectedPath));
        await pipeline.CompleteAsync(prepared, default);
        Assert.False(Directory.Exists(prepared.WorkingDirectory));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "steam")));
    }

    [Fact]
    public async Task Cancellation_propagates_and_removes_abandoned_staging()
    {
        using var cancellation = new CancellationTokenSource();
        var codec = new ScriptedCodec { DuringEncoding = cancellation.Cancel };
        var pipeline = Pipeline(codec);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.PrepareAsync("steam", "avif80", Download, cancellation.Token));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, ".staging")));
        Assert.Equal(1, codec.Encodes);
    }

    [Fact]
    public async Task Budget_is_shared_between_adapters_and_service_instances()
    {
        var options = Options();
        options.CoverBudgetBytes = 5;
        using var first = new ImagePipeline(options, new ScriptedCodec());
        using var second = new ImagePipeline(options, new ScriptedCodec());
        var initial = await first.PrepareAsync("steam", "original", Download, default);
        await first.PublishAsync(initial, default);
        var next = await second.PrepareAsync("shikimori", "original", Download, default);

        await Assert.ThrowsAsync<ImageStorageFullException>(() => second.PublishAsync(next, default));
        Assert.True(File.Exists(next.OriginalPath));
    }

    [Fact]
    public async Task Repeated_publication_deduplicates_by_hash()
    {
        var pipeline = Pipeline(new ScriptedCodec());
        var first = await pipeline.PrepareAsync("steam", "original", Download, default);
        var second = await pipeline.PrepareAsync("steam", "original", Download, default);
        var original = await pipeline.PublishAsync(first, default);
        var duplicate = await pipeline.PublishAsync(second, default);

        Assert.Equal(original, duplicate);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "steam")));
    }

    [Fact]
    public async Task Publication_rejects_staged_bytes_changed_after_validation()
    {
        var pipeline = Pipeline(new ScriptedCodec());
        var prepared = await pipeline.PrepareAsync("steam", "original", Download, default);
        await File.WriteAllBytesAsync(prepared.SelectedPath, new byte[] { 7, 8, 9 });

        await Assert.ThrowsAsync<InvalidDataException>(() => pipeline.PublishAsync(prepared, default));
    }

    [Fact]
    public async Task Recovery_preserves_active_attempt_and_reclaims_released_staging()
    {
        var pipeline = Pipeline(new ScriptedCodec());
        var prepared = await pipeline.PrepareAsync("steam", "original", Download, default);

        Assert.Equal(0, await pipeline.RecoverStagingAsync(TimeSpan.Zero, default));
        Assert.True(File.Exists(prepared.OriginalPath));
        await pipeline.ReleaseAsync(prepared, default);
        Assert.Equal(1, await pipeline.RecoverStagingAsync(TimeSpan.Zero, default));
        Assert.False(Directory.Exists(prepared.WorkingDirectory));
    }

    [Fact]
    public async Task Deletion_updates_shared_storage_budget()
    {
        var options = Options();
        options.CoverBudgetBytes = 5;
        using var pipeline = new ImagePipeline(options, new ScriptedCodec());
        var prepared = await pipeline.PrepareAsync("steam", "original", Download, default);
        var published = await pipeline.PublishAsync(prepared, default);
        await pipeline.DeletePublishedAsync(published.RelativePath, default);

        var another = await pipeline.PrepareAsync("shikimori", "original", Download, default);
        var result = await pipeline.PublishAsync(another, default);
        Assert.False(File.Exists(published.FullPath));
        Assert.True(File.Exists(result.FullPath));
    }

    [Fact]
    public async Task Destination_traversal_is_rejected_before_download()
    {
        var pipeline = Pipeline(new ScriptedCodec());
        var downloaded = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => pipeline.PrepareAsync("../outside", "original", _ =>
        {
            downloaded = true;
            return Download(default);
        }, default));
        Assert.False(downloaded);
    }

    [Theory]
    [InlineData("avif0")]
    [InlineData("avif101")]
    [InlineData("avif080")]
    [InlineData("avif80;command")]
    [InlineData("AVIF80")]
    public async Task Unknown_profile_is_rejected_before_download(string profile)
    {
        var downloaded = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => Pipeline(new ScriptedCodec()).PrepareAsync("steam", profile, _ =>
        {
            downloaded = true;
            return Download(default);
        }, default));
        Assert.False(downloaded);
    }

    [Fact]
    public async Task Native_codec_keeps_valid_larger_avif()
    {
        using var image = new MagickImage(MagickColors.Blue, 1, 1);
        var source = image.ToByteArray(MagickFormat.Png24);
        var pipeline = Pipeline(new ImporterImageCodec());
        var prepared = await pipeline.PrepareAsync("steam", "avif80", _ => Task.FromResult(source), default);

        Assert.Equal("avif80", prepared.ActualProfile);
        Assert.True(prepared.Bytes > source.Length);
        using var decoded = new MagickImage(await File.ReadAllBytesAsync(prepared.SelectedPath));
        Assert.Equal(1u, decoded.Width);
        Assert.Equal(1u, decoded.Height);
    }

    [Fact]
    public async Task Native_codec_preserves_transparency_or_falls_back_to_exact_original()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, new SKColor(40, 90, 150, (byte)(x * 6 + y)));
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var source = png.ToArray();
        var pipeline = Pipeline(new ImporterImageCodec());
        var prepared = await pipeline.PrepareAsync("steam", "avif80", _ => Task.FromResult(source), default);
        using var original = new MagickImage(source);
        using var decoded = new MagickImage(await File.ReadAllBytesAsync(prepared.SelectedPath));
        using var expectedPixels = original.GetPixels();
        using var actualPixels = decoded.GetPixels();

        Assert.Equal(expectedPixels.ToByteArray("A"), actualPixels.ToByteArray("A"));
        if (prepared.ActualProfile == "original")
            Assert.Equal(source, await File.ReadAllBytesAsync(prepared.SelectedPath));
    }

    [Fact]
    public async Task Native_codec_applies_exif_orientation_before_validating_output_dimensions()
    {
        using var image = new MagickImage(MagickColors.Blue, 12, 8);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        image.SetProfile(exif);
        image.Orientation = Orientation.RightTop;
        var source = image.ToByteArray(MagickFormat.Jpeg);
        using var original = new MagickImage(source);
        Assert.Equal(Orientation.RightTop, original.Orientation);
        var prepared = await Pipeline(new ImporterImageCodec()).PrepareAsync("steam", "avif80",
            _ => Task.FromResult(source), default);

        Assert.Equal("avif80", prepared.ActualProfile);
        using var decoded = new MagickImage(await File.ReadAllBytesAsync(prepared.SelectedPath));
        Assert.Equal(8u, decoded.Width);
        Assert.Equal(12u, decoded.Height);
    }

    [Fact]
    public async Task High_depth_png_retains_exact_original_instead_of_quantizing_to_eight_bits()
    {
        using var image = new MagickImage(MagickColors.Blue, 4, 4);
        image.Depth = 16;
        var source = image.ToByteArray(MagickFormat.Png48);
        Assert.Equal(16, source[24]);
        var prepared = await Pipeline(new ImporterImageCodec()).PrepareAsync("steam", "avif80",
            _ => Task.FromResult(source), default);

        Assert.Equal("original", prepared.ActualProfile);
        Assert.Equal(source, await File.ReadAllBytesAsync(prepared.SelectedPath));
        Assert.Contains("image_fallback_original", prepared.Warnings);
    }

    [Fact]
    public async Task Animated_gif_retains_exact_original_instead_of_flattening_animation()
    {
        using var frames = new MagickImageCollection();
        frames.Add(new MagickImage(MagickColors.Blue, 4, 4));
        frames.Add(new MagickImage(MagickColors.Red, 4, 4));
        var source = frames.ToByteArray(MagickFormat.Gif);
        var prepared = await Pipeline(new ImporterImageCodec()).PrepareAsync("steam", "avif80",
            _ => Task.FromResult(source), default);

        Assert.Equal("original", prepared.ActualProfile);
        Assert.Equal(source, await File.ReadAllBytesAsync(prepared.SelectedPath));
        Assert.Contains("image_fallback_original", prepared.Warnings);
    }

    [Fact]
    public async Task Corrupt_original_is_rejected_and_staging_is_cleaned()
    {
        var pipeline = Pipeline(new ImporterImageCodec());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            pipeline.PrepareAsync("steam", "original", Download, default));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, ".staging")));
    }

    [Fact]
    public async Task Native_source_decode_failure_is_reported_as_invalid_image()
    {
        var source = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
        var pipeline = Pipeline(new ImporterImageCodec());
        await Assert.ThrowsAsync<InvalidDataException>(() => pipeline.PrepareAsync("steam", "original",
            _ => Task.FromResult(source), default));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, ".staging")));
    }

    [Theory]
    [InlineData("webp80", "image/webp")]
    [InlineData("webp-lossless", "image/webp")]
    [InlineData("jpeg85", "image/jpeg")]
    [InlineData("png-lossless", "image/png")]
    public async Task Native_modules_write_the_requested_format_with_one_download(string profile, string contentType)
    {
        using var image = new MagickImage(MagickColors.Blue, 32, 24);
        var source = image.ToByteArray(MagickFormat.Png24);
        var downloads = 0;
        var pipeline = Pipeline(new ImporterImageCodec());
        var prepared = await pipeline.PrepareAsync("source", profile, _ =>
        {
            downloads++;
            return Task.FromResult(source);
        }, default);
        Assert.Equal(1, downloads);
        Assert.Equal(profile, prepared.ActualProfile);
        Assert.Equal(contentType, prepared.ContentType);
        using var decoded = new MagickImage(await File.ReadAllBytesAsync(prepared.SelectedPath));
        Assert.Equal(32u, decoded.Width);
        Assert.Equal(24u, decoded.Height);
        var published = await pipeline.PublishAsync(prepared, default);
        await pipeline.CompleteAsync(prepared, default);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "source")));
        Assert.True(File.Exists(published.FullPath));
    }

    [Theory]
    [InlineData("png-lossless")]
    [InlineData("webp-lossless")]
    public async Task Lossless_modules_preserve_rgba_pixels(string profile)
    {
        using var image = new MagickImage(new MagickColor(40, 90, 150, 100), 12, 8);
        var source = image.ToByteArray(MagickFormat.Png32);
        var prepared = await Pipeline(new ImporterImageCodec()).PrepareAsync("source", profile, _ => Task.FromResult(source), default);
        Assert.Equal(profile, prepared.ActualProfile);
        using var original = new MagickImage(source);
        using var decoded = new MagickImage(await File.ReadAllBytesAsync(prepared.SelectedPath));
        using var before = original.GetPixels();
        using var after = decoded.GetPixels();
        Assert.Equal(before.ToByteArray("RGBA"), after.ToByteArray("RGBA"));
    }

    [Fact]
    public async Task Jpeg_does_not_silently_remove_transparency()
    {
        using var image = new MagickImage(new MagickColor(40, 90, 150, 100), 12, 8);
        var source = image.ToByteArray(MagickFormat.Png32);
        var prepared = await Pipeline(new ImporterImageCodec()).PrepareAsync("source", "jpeg85", _ => Task.FromResult(source), default);
        Assert.Equal("original", prepared.ActualProfile);
        Assert.Equal(source, await File.ReadAllBytesAsync(prepared.SelectedPath));
        Assert.Contains("image_fallback_original", prepared.Warnings);
    }

    [Theory]
    [InlineData("png-lossless")]
    [InlineData("webp80")]
    [InlineData("webp-lossless")]
    [InlineData("jpeg85")]
    [InlineData("avif80")]
    public async Task Repeated_encoding_of_identical_source_has_a_stable_hash_across_clock_ticks(string profile)
    {
        using var image = new MagickImage(MagickColors.Blue, 32, 24);
        var source = image.ToByteArray(MagickFormat.Jpeg);
        var pipeline = Pipeline(new ImporterImageCodec());
        var first = await pipeline.PrepareAsync("source", profile, _ => Task.FromResult(source), default);
        await Task.Delay(1100);
        var second = await pipeline.PrepareAsync("source", profile, _ => Task.FromResult(source), default);
        Assert.Equal(profile, first.ActualProfile);
        Assert.Equal(profile, second.ActualProfile);
        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal((await pipeline.PublishAsync(first, default)).FullPath, (await pipeline.PublishAsync(second, default)).FullPath);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "source")));
    }

    private ImagePipeline Pipeline(IImporterImageCodec codec)
    {
        var pipeline = new ImagePipeline(Options(), codec);
        pipelines.Add(pipeline);
        return pipeline;
    }

    private ImageStorageOptions Options() => new()
    {
        MediaRoot = root,
        MinimumFreeBytes = 0,
        CoverBudgetBytes = 1024 * 1024
    };

    private static Task<byte[]> Download(CancellationToken cancellationToken)
        => Task.FromResult(new byte[] { 1, 2, 3 });

    public void Dispose()
    {
        foreach (var pipeline in pipelines)
            pipeline.Dispose();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    private sealed class ScriptedCodec : IImporterImageCodec
    {
        public int FailedChecks { get; init; }
        public bool ThrowOnEncoding { get; init; }
        public Action? DuringEncoding { get; init; }
        public int Encodes { get; private set; }
        public List<string> CheckedPaths { get; } = [];

        public Task<ImageDescription> ValidateOriginalAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new ImageDescription("image/png", 1, 1, false, 1, 8));

        public async Task EncodeAsync(string originalPath, string outputPath, ImageEncodingProfile profile,
            CancellationToken cancellationToken)
        {
            Encodes++;
            DuringEncoding?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnEncoding)
                throw new InvalidDataException("Injected encoder failure.");
            await File.WriteAllBytesAsync(outputPath, new byte[64], cancellationToken);
        }

        public Task ValidateEncodedAsync(string originalPath, string candidatePath, ImageEncodingProfile profile, CancellationToken cancellationToken)
        {
            CheckedPaths.Add(candidatePath);
            if (CheckedPaths.Count <= FailedChecks)
                throw new InvalidDataException("Injected validation failure.");
            return Task.CompletedTask;
        }
    }
}
