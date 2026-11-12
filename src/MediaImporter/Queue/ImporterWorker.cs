using Npgsql;

namespace MediaImporter;

public sealed class ImporterWorker(IServiceScopeFactory scopes, AdapterRegistry registry,
    ImportJobStore jobs, NpgsqlDataSource dataSource, ImporterOptions options,
    ILogger<ImporterWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, options.WorkerCount).Select(_ => RunJobsAsync(stoppingToken));
        return Task.WhenAll(workers.Append(RunDiscoveryAsync(stoppingToken)));
    }

    private string[] EnabledProviders() => registry.List()
        .Where(value => value.Enabled && value.Version is not null).Select(value => value.Provider).ToArray();

    private async Task RunJobsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await jobs.ClaimJobAsync(EnabledProviders(), stoppingToken);
                if (job is null) { await Task.Delay(1000, stoppingToken); continue; }
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Importer worker deferred ({ErrorType})", error.GetType().Name);
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessJobAsync(ImportJob job, CancellationToken stoppingToken)
    {
        // A session lock prevents an expired lease from running the same item concurrently.
        await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
        var lockName = "catalogweaver-job:" + job.Provider + ":" + job.ExternalId;
        await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@key,0))", connection);
        claim.Parameters.AddWithValue("key", lockName);
        if (!(bool)(await claim.ExecuteScalarAsync(stoppingToken))!)
        {
            await jobs.ReleaseJobAsync(job, stoppingToken);
            return;
        }
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(options.ImportTimeoutSeconds));
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = MaintainLeaseAsync(token => jobs.RenewJobAsync(job, token), attempt, heartbeatStop.Token);
        try
        {
            var adapter = registry.FromPinnedJson(job.AdapterJson, job.Provider, job.AdapterVersion);
            using var scope = scopes.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ImportOrchestrator>();
            var result = await orchestrator.ExecuteAsync(adapter, job.ExternalId, false, false,
                (stage, ct) => jobs.SetStageAsync(job, stage, ct), attempt.Token, job);
            await jobs.FinishJobAsync(job, result, false, CancellationToken.None, adapter.Definition.RefreshHours);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await jobs.ReleaseJobAsync(job, CancellationToken.None);
        }
        catch (Exception error)
        {
            var terminal = error is AdapterReadException { Retryable: false } or ArgumentException or InvalidDataException;
            var result = new ImportResult
            {
                Provider = job.Provider, ExternalId = job.ExternalId, AdapterVersion = job.AdapterVersion,
                Status = "retry", Error = error is AdapterReadException source ? source.Code : error.GetType().Name
            };
            await jobs.FinishJobAsync(job, result, terminal, CancellationToken.None);
            logger.LogWarning("Import {Provider}/{ExternalId} deferred ({ErrorType})", job.Provider, job.ExternalId, error.GetType().Name);
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key,0))", connection);
            release.Parameters.AddWithValue("key", lockName);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task MaintainLeaseAsync(Func<CancellationToken, Task<bool>> renew, CancellationTokenSource attempt, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.LeaseSeconds / 3)));
            while (await timer.WaitForNextTickAsync(ct))
                if (!await renew(ct)) { attempt.Cancel(); return; }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning("Importer lease renewal failed ({ErrorType})", error.GetType().Name);
            attempt.Cancel();
        }
    }

    private async Task RunDiscoveryAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DiscoveryRun? run = null;
            try
            {
                run = await jobs.ClaimDiscoveryAsync(EnabledProviders(), stoppingToken);
                if (run is null) { await Task.Delay(1000, stoppingToken); continue; }
                var adapter = registry.FromPinnedJson(run.AdapterJson, run.Provider, run.AdapterVersion);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.ImportTimeoutSeconds));
                using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeat = MaintainLeaseAsync(token => jobs.RenewDiscoveryAsync(run, token), timeout, heartbeatStop.Token);
                try
                {
                    using var scope = scopes.CreateScope();
                    var runtime = scope.ServiceProvider.GetRequiredService<AdapterRuntime>();
                    var page = run.PendingItems is { Length: > 0 }
                        ? new DiscoveryPage(run.PendingItems, run.PendingCursor)
                        : await runtime.DiscoverAsync(adapter, run.Cursor, timeout.Token);
                    await jobs.CommitDiscoveryPageAsync(run, adapter, page, timeout.Token);
                }
                finally
                {
                    heartbeatStop.Cancel();
                    try { await heartbeat; } catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (run is not null) await jobs.ReleaseDiscoveryAsync(run, CancellationToken.None);
                break;
            }
            catch (Exception error)
            {
                if (run is not null) await jobs.FailDiscoveryAsync(run, error, CancellationToken.None);
                logger.LogWarning("Importer discovery deferred ({ErrorType})", error.GetType().Name);
                await Task.Delay(2000, stoppingToken);
            }
        }
    }
}
