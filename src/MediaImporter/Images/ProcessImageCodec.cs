using System.Diagnostics;
using System.Text.Json;
using ImageMagick;

namespace MediaImporter.Images;

/// <summary>All providers share bounded, disposable native workers inside the service container.</summary>
public sealed class ProcessImageCodec : IImporterImageCodec, IDisposable
{
    private readonly ImageStorageOptions options;
    private readonly SemaphoreSlim slots;
    public ProcessImageCodec(ImageStorageOptions options)
    {
        if (options.ImageWorkerCount is < 1 or > 16 || options.ImageProcessTimeoutSeconds is < 1 or > 3600
            || options.ImageMemoryMiB is < 32 or > 4096 || options.ImageDiskMiB is < 0 or > 8192
            || options.ImageThreads is < 1 or > 16) throw new InvalidDataException("Invalid native image worker limits.");
        this.options = options;
        slots = new(options.ImageWorkerCount, options.ImageWorkerCount);
    }

    public async Task<ImageDescription> ValidateOriginalAsync(string path, CancellationToken cancellationToken)
        => (await RunAsync(new("validate", path), cancellationToken)).Description
            ?? throw new InvalidDataException("Image worker returned no description.");
    public async Task EncodeAsync(string originalPath, string outputPath, ImageEncodingProfile profile, CancellationToken cancellationToken)
        => _ = await RunAsync(new("encode", originalPath, outputPath, profile), cancellationToken);
    public async Task ValidateEncodedAsync(string originalPath, string candidatePath, ImageEncodingProfile profile, CancellationToken cancellationToken)
        => _ = await RunAsync(new("check", originalPath, candidatePath, profile), cancellationToken);

    private async Task<WorkerResponse> RunAsync(WorkerRequest request, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
            start.ArgumentList.Add(typeof(ImporterProgram).Assembly.Location);
            start.ArgumentList.Add("--image-worker");
            request = request with { MemoryMiB = options.ImageMemoryMiB, DiskMiB = options.ImageDiskMiB, Threads = options.ImageThreads };
            var output = await BoundedProcess.RunAsync(start, JsonSerializer.Serialize(request),
                TimeSpan.FromSeconds(options.ImageProcessTimeoutSeconds), ct);
            return JsonSerializer.Deserialize<WorkerResponse>(output) ?? throw new InvalidDataException("Invalid image worker response.");
        }
        finally { slots.Release(); }
    }

    public static async Task<int> RunWorkerAsync()
    {
        try
        {
            var input = new char[65537];
            var count = 0;
            while (count < input.Length)
            {
                var read = await Console.In.ReadAsync(input.AsMemory(count));
                if (read == 0) break;
                count += read;
            }
            if (count > 65536) return 2;
            var request = JsonSerializer.Deserialize<WorkerRequest>(input.AsSpan(0, count)) ?? throw new InvalidDataException();
            if (request.MemoryMiB is < 32 or > 4096 || request.DiskMiB is < 0 or > 8192 || request.Threads is < 1 or > 16)
                return 2;
            ResourceLimits.Memory = (ulong)request.MemoryMiB * 1024 * 1024;
            ResourceLimits.Disk = (ulong)request.DiskMiB * 1024 * 1024;
            ResourceLimits.Thread = (ulong)request.Threads;
            ResourceLimits.Width = ResourceLimits.Height = 8192;
            ResourceLimits.ListLength = 256;
            var codec = new ImporterImageCodec();
            ImageDescription? description = null;
            switch (request.Operation)
            {
                case "validate": description = await codec.ValidateOriginalAsync(request.Original, default); break;
                case "encode" when request.Output is not null && request.Profile is not null:
                    ImageEncodingProfile.Validate(request.Profile);
                    await codec.EncodeAsync(request.Original, request.Output, request.Profile, default); break;
                case "check" when request.Output is not null && request.Profile is not null:
                    ImageEncodingProfile.Validate(request.Profile);
                    await codec.ValidateEncodedAsync(request.Original, request.Output, request.Profile, default); break;
                default: return 2;
            }
            await Console.Out.WriteAsync(JsonSerializer.Serialize(new WorkerResponse(description)));
            return 0;
        }
        catch { return 2; } // Native errors may contain local paths; the parent reports a safe stable error.
    }

    public void Dispose() => slots.Dispose();
    private sealed record WorkerRequest(string Operation, string Original, string? Output = null, ImageEncodingProfile? Profile = null,
        int MemoryMiB = 256, int DiskMiB = 512, int Threads = 2);
    private sealed record WorkerResponse(ImageDescription? Description);
}
