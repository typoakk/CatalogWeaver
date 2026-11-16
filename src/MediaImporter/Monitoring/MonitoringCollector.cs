namespace MediaImporter.Monitoring;

public sealed class MonitoringCollector(MonitoringStore store, MonitoringTelemetry telemetry,
    ILogger<MonitoringCollector> logger) : BackgroundService
{
    public string? Error { get; private set; }
    private List<HttpMeasurement> pending = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextSnapshot = DateTime.MinValue; var nextCleanup = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (pending.Count == 0) pending = telemetry.Drain();
                await store.SaveHttpAsync(pending, stoppingToken); pending.Clear();
                if (DateTime.UtcNow >= nextSnapshot)
                {
                    await store.SaveSnapshotAsync(await store.CollectAsync(stoppingToken), stoppingToken);
                    nextSnapshot = DateTime.UtcNow.AddSeconds(10);
                }
                if (DateTime.UtcNow >= nextCleanup)
                {
                    await store.CleanupAsync(stoppingToken); nextCleanup = DateTime.UtcNow.AddHours(1);
                }
                Error = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                Error = ImportErrors.Classify(error).Code;
                logger.LogWarning("Monitoring collector: {Code}", Error);
            }
            try { await Task.Delay(2000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Monitoring connections: {Code}", ImportErrors.Classify(error).Code); }
        }
        using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await store.SaveHttpAsync(pending, flush.Token); await store.SaveHttpAsync(telemetry.Drain(), flush.Token); }
        catch (Exception) { logger.LogWarning("Final monitoring flush was unavailable."); }
    }
}
