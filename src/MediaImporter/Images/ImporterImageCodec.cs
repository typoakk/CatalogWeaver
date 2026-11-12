using System.Buffers.Binary;
using ImageMagick;

namespace MediaImporter.Images;

public sealed class ImporterImageCodec : IImporterImageCodec
{
    private const uint MaximumDimension = 8192;
    private const ulong MaximumPixels = 16_777_216;

    public Task<ImageDescription> ValidateOriginalAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(path);
            var contentType = SourceContentType(bytes);
            using var headers = new MagickImageCollection();
            headers.Ping(bytes);
            if (headers.Count == 0)
                throw new InvalidDataException("Image contains no frames.");
            var header = headers[0];
            CheckDimensions(header.Width, header.Height);
            using var decoded = ReadImage(bytes);
            CheckDimensions(decoded.Width, decoded.Height);
            // Reading the pixel collection forces a complete decode of the first frame.
            using var pixels = decoded.GetPixels();
            cancellationToken.ThrowIfCancellationRequested();
            return new ImageDescription(contentType, decoded.Width, decoded.Height,
                decoded.HasAlpha, headers.Count, header.Depth);
        }, cancellationToken);

    public Task EncodeAsync(string originalPath, string outputPath, ImageEncodingProfile profile,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = File.ReadAllBytes(originalPath);
            using var headers = new MagickImageCollection();
            headers.Ping(source);
            if (headers.Count != 1 || headers[0].Depth > 8)
                throw new InvalidDataException("Animated and high-depth images retain the original format.");
            using var image = ReadImage(source);
            CheckDimensions(image.Width, image.Height);
            image.AutoOrient();
            if (profile.RejectAlpha && image.HasAlpha)
                throw new InvalidDataException("Selected encoder cannot preserve an alpha channel; retain the original image.");
            if (profile.Quality is { } quality) image.Quality = (uint)quality;
            foreach (var (key, value) in profile.Defines) image.Settings.SetDefine(key, value);
            image.Write(outputPath, Enum.Parse<MagickFormat>(profile.Format));
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);

    public Task ValidateEncodedAsync(string originalPath, string candidatePath,
        ImageEncodingProfile profile, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = File.ReadAllBytes(candidatePath);
            if (ContentType(output, allowAdditionalFormats: true) != profile.ContentType)
                throw new InvalidDataException("Encoder did not produce the requested image format.");
            using var original = ReadImage(File.ReadAllBytes(originalPath));
            original.AutoOrient();
            using var decoded = new MagickImage(output, Enum.Parse<MagickFormat>(profile.Format));
            CheckDimensions(decoded.Width, decoded.Height);
            if (decoded.Width != original.Width || decoded.Height != original.Height
                || decoded.HasAlpha != original.HasAlpha)
                throw new InvalidDataException("Encoded image changed dimensions or transparency.");
            using var decodedPixels = decoded.GetPixels();
            if (original.HasAlpha)
            {
                using var originalPixels = original.GetPixels();
                var expectedAlpha = originalPixels.ToByteArray("A");
                var actualAlpha = decodedPixels.ToByteArray("A");
                if (expectedAlpha is null || actualAlpha is null
                    || !expectedAlpha.AsSpan().SequenceEqual(actualAlpha))
                    throw new InvalidDataException("Encoded image changed the transparency channel.");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);

    internal static string SourceContentType(ReadOnlySpan<byte> bytes)
        => ContentType(bytes, allowAdditionalFormats: false);

    private static string ContentType(ReadOnlySpan<byte> bytes, bool allowAdditionalFormats)
    {
        if (bytes.Length > 3 && bytes[0] == 0xff && bytes[1] == 0xd8)
            return "image/jpeg";
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return "image/png";
        if (bytes.Length > 12 && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        if (IsAvif(bytes))
            return "image/avif";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
            return "image/gif";
        if (!allowAdditionalFormats)
            throw new InvalidDataException("Unsupported source image format.");
        using var headers = new MagickImageCollection();
        headers.Ping(bytes.ToArray());
        if (headers.Count == 0) throw new InvalidDataException("Image contains no frames.");
        return ImageEncodingProfile.ContentTypeFor(headers[0].Format.ToString());
    }

    internal static bool IsAvif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || !bytes[4..8].SequenceEqual("ftyp"u8))
            return false;
        var size = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (size < 16 || size > bytes.Length)
            return false;
        if (bytes[8..12].SequenceEqual("avif"u8) || bytes[8..12].SequenceEqual("avis"u8))
            return true;
        for (var offset = 16; offset + 4 <= size; offset += 4)
            if (bytes.Slice(offset, 4).SequenceEqual("avif"u8))
                return true;
        return false;
    }

    private static MagickImage ReadImage(byte[] bytes)
    {
        SourceContentType(bytes);
        return new MagickImage(bytes, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
    }

    private static void CheckDimensions(uint width, uint height)
    {
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension
            || (ulong)width * height > MaximumPixels)
            throw new InvalidDataException("Image dimensions exceed the configured image limits.");
    }
}
