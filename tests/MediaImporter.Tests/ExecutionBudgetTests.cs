using System.Diagnostics;
using MediaImporter;
using MediaImporter.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaImporter.Tests;

public sealed class ExecutionBudgetTests
{
    [Fact]
    public async Task Preview_waiting_for_capacity_also_spends_its_total_budget()
    {
        using var previews = new PreviewExecutor(new ImporterOptions { PreviewCount = 1, PreviewTimeoutSeconds = 1 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = previews.ExecuteAsync(async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; }, default);
        await entered.Task;
        var queued = previews.ExecuteAsync(ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult(1); }, default);
        Assert.Equal("preview_timeout", (await Assert.ThrowsAsync<AdapterReadException>(() => running)).Code);
        try { await queued; } catch (AdapterReadException error) { Assert.Equal("preview_timeout", error.Code); }
        // Waiting and execution together cannot outlive a fresh one-second operation budget.
        Assert.True(queued.IsCompleted);
    }

    [Fact]
    public async Task Preview_concurrency_is_shared_across_callers_and_cancellation_releases_capacity()
    {
        using var previews = new PreviewExecutor(new ImporterOptions { PreviewCount = 1, PreviewTimeoutSeconds = 30 });
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = previews.ExecuteAsync(async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; }, cancel.Token);
        await entered.Task;
        var secondEntered = false;
        var second = previews.ExecuteAsync(ct => { secondEntered = true; return Task.FromResult(2); }, default);
        Assert.False(secondEntered);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(2, await second);
    }

    [Fact]
    public async Task Deadline_terminates_a_noncooperative_process_before_returning()
    {
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo("sh");
        if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("/c"); start.ArgumentList.Add("ping -n 30 127.0.0.1 >nul"); }
        else { start.ArgumentList.Add("-c"); start.ArgumentList.Add("sleep 30"); }
        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ProcessDeadlineException>(() => BoundedProcess.RunAsync(start, "", TimeSpan.FromMilliseconds(300), default));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        try { using var child = Process.GetProcessById(error.ProcessId); Assert.True(child.HasExited); }
        catch (ArgumentException) { } // The OS already reaped the terminated child.
    }

    [Fact]
    public async Task Isolated_native_worker_validates_encodes_and_checks_real_pixels()
    {
        var root = Path.Combine(Path.GetTempPath(), "catalogweaver-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "original.png");
            using (var bitmap = new SKBitmap(16, 12))
            {
                bitmap.Erase(SKColors.CornflowerBlue);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(source, data.ToArray());
            }
            using var codec = new ProcessImageCodec(new ImageStorageOptions());
            var original = await codec.ValidateOriginalAsync(source, default);
            Assert.Equal((uint)16, original.Width);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Importer:EncodersPath"] = Path.Combine(AppContext.BaseDirectory, "encoders") }).Build();
            using var encoders = new EncoderRegistry(configuration, NullLogger<EncoderRegistry>.Instance);
            var profile = encoders.Resolve("avif80");
            var candidate = Path.Combine(root, "candidate.avif");
            await codec.EncodeAsync(source, candidate, profile, default);
            await codec.ValidateEncodedAsync(source, candidate, profile, default);
            Assert.True(new FileInfo(candidate).Length > 0);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Excessive_worker_output_fails_without_waiting_for_the_execution_deadline()
    {
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo("sh");
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        start.ArgumentList.Add(OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,3000) do @echo bounded-output-must-not-fill-a-pipe-and-stall"
            : "i=0; while [ $i -lt 3000 ]; do echo bounded-output-must-not-fill-a-pipe-and-stall; i=$((i+1)); done; sleep 30");
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidDataException>(() => BoundedProcess.RunAsync(start, "", TimeSpan.FromSeconds(20), default));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }
}
