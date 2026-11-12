using System.Diagnostics;
using Npgsql;

namespace MediaImporter;

public sealed class ImporterWorker(IServiceScopeFactory scopes, AdapterRegistry registry,
    ImportJobStore jobs, NpgsqlDataSource dataSource, ImporterOptions options,
    ILogger<ImporterWorker> logger, ExecutionDiagnostics? diagnostics = null) : BackgroundService
{
    private readonly ExecutionDiagnostics state = diagnostics ?? new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, options.WorkerCount)
            .Select(index => SuperviseAsync("worker-" + index, RunJobsAsync, stoppingToken)).ToArray();
        return Task.WhenAll(workers.Append(SuperviseAsync("discovery", RunDiscoveryAsync, stoppingToken)));
    }

    private async Task SuperviseAsync(string name, Func<string, CancellationToken, Task> run, CancellationToken ct)
    {
        state.Register(name);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await run(name, ct);
                    if (!ct.IsCancellationRequested) throw new InvalidOperationException("Background loop exited unexpectedly.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    state.Failure(name, error);
                    LogFailure(name, "supervisor", error);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { state.Stopped(name); }
    }

    private string[] EnabledProviders() => options.WorkersEnabled ? registry.List()
        .Where(value => value.Enabled && value.Version is not null).Select(value => value.Provider).ToArray() : [];

    private async Task RunJobsAsync(string name, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await jobs.ClaimJobAsync(EnabledProviders(), stoppingToken);
                if (job is null) { state.Tick(name); await Task.Delay(1000, stoppingToken); continue; }
                await ProcessJobAsync(name, job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                state.Failure(name, error);
                LogFailure(name, "claim_or_finalize", error);
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task ProcessJobAsync(string name, ImportJob job, CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        var stage = "acquire_lock";
        void Progress(string value)
        {
            stage = value;
            state.Tick(name, value, job.Id, job.LeaseToken, job.Provider, job.ExternalId);
        }
        Progress(stage);
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        { ["JobId"] = job.Id, ["AttemptId"] = job.LeaseToken, ["Provider"] = job.Provider, ["ExternalId"] = job.ExternalId });
        await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
        var lockName = "catalogweaver-job:" + job.Provider + ":" + job.ExternalId;
        await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@key,0))", connection);
        claim.Parameters.AddWithValue("key", lockName);
        if (!(bool)(await claim.ExecuteScalarAsync(stoppingToken))!)
        {
            await jobs.ReleaseJobAsync(job, stoppingToken);
            state.Success(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return;
        }
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(options.ImportTimeoutSeconds));
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = MaintainLeaseAsync(token => jobs.RenewJobAsync(job, token), attempt, heartbeatStop.Token,
            () => state.Tick(name, stage, job.Id, job.LeaseToken, job.Provider, job.ExternalId));
        try
        {
            var adapter = registry.FromPinnedJson(job.AdapterJson, job.Provider, job.AdapterVersion);
            using var scope = scopes.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ImportOrchestrator>();
            var result = await orchestrator.ExecuteAsync(adapter, job.ExternalId, false, false,
                async (value, ct) => { Progress(value); await jobs.SetStageAsync(job, value, ct); }, attempt.Token, job);
            Progress("finalizing");
            using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await jobs.FinishJobAsync(job, result, false, finalize.Token, adapter.Definition.RefreshHours);
            state.Success(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            logger.LogInformation("Import attempt finished at {Stage} with {Status} in {ElapsedMs} ms",
                stage, result.Status, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await TryRecordAsync(name, "release_job", token => jobs.ReleaseJobAsync(job, token));
        }
        catch (Exception error)
        {
            var failure = ImportErrors.Classify(error);
            var result = new ImportResult
            {
                Provider = job.Provider, ExternalId = job.ExternalId, AdapterVersion = job.AdapterVersion,
                Status = failure.Retryable ? "retry" : "failed", Error = failure.Code
            };
            await TryRecordAsync(name, "fail_job", token => jobs.FinishJobAsync(job, result, !failure.Retryable, token));
            state.Failure(name, error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            LogFailure(name, stage, error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
            await TryRecordAsync(name, "release_lock", async token =>
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key,0))", connection);
                release.Parameters.AddWithValue("key", lockName);
                await release.ExecuteNonQueryAsync(token);
            });
        }
    }

    private async Task MaintainLeaseAsync(Func<CancellationToken, Task<bool>> renew, CancellationTokenSource attempt,
        CancellationToken ct, Action tick)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.LeaseSeconds / 3, 5, 30)));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!await renew(ct)) { await attempt.CancelAsync(); return; }
                tick();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            LogFailure("lease", "renew", error);
            await attempt.CancelAsync();
        }
    }

    private async Task RunDiscoveryAsync(string name, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DiscoveryRun? run = null;
            var started = Stopwatch.GetTimestamp();
            try
            {
                run = await jobs.ClaimDiscoveryAsync(EnabledProviders(), stoppingToken);
                if (run is null) { state.Tick(name); await Task.Delay(1000, stoppingToken); continue; }
                void Tick() => state.Tick(name, "discovering", run.Id, run.LeaseToken, run.Provider);
                Tick();
                var adapter = registry.FromPinnedJson(run.AdapterJson, run.Provider, run.AdapterVersion);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.ImportTimeoutSeconds));
                using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeat = MaintainLeaseAsync(token => jobs.RenewDiscoveryAsync(run, token), timeout, heartbeatStop.Token, Tick);
                try
                {
                    using var scope = scopes.CreateScope();
                    var runtime = scope.ServiceProvider.GetRequiredService<AdapterRuntime>();
                    var page = run.PendingItems is { Length: > 0 }
                        ? new DiscoveryPage(run.PendingItems, run.PendingCursor)
                        : await runtime.DiscoverAsync(adapter, run.Cursor, timeout.Token);
                    await jobs.CommitDiscoveryPageAsync(run, adapter, page, timeout.Token);
                    state.Success(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    logger.LogInformation("Discovery {Provider} attempt {AttemptId} committed {Count} IDs in {ElapsedMs} ms",
                        run.Provider, run.LeaseToken, page.Items.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                finally
                {
                    await heartbeatStop.CancelAsync();
                    try { await heartbeat; } catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (run is not null) await TryRecordAsync(name, "release_discovery", token => jobs.ReleaseDiscoveryAsync(run, token));
                break;
            }
            catch (Exception error)
            {
                // A second database failure must not escape this recovery branch.
                if (run is not null) await TryRecordAsync(name, "fail_discovery", token => jobs.FailDiscoveryAsync(run, error, token));
                state.Failure(name, error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                LogFailure(name, "discovery_page", error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task TryRecordAsync(string name, string stage, Func<CancellationToken, Task> action)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await action(timeout.Token);
        }
        catch (Exception error)
        {
            state.Failure(name, error);
            LogFailure(name, stage, error);
        }
    }

    private void LogFailure(string name, string stage, Exception error, double? elapsed = null)
    {
        var failure = ImportErrors.Classify(error);
        logger.LogWarning("Background {Loop} at {Stage}: {Code}; retryable={Retryable}; sqlState={SqlState}; {Reason}; elapsedMs={ElapsedMs}",
            name, stage, failure.Code, failure.Retryable, failure.SqlState, failure.SafeMessage, elapsed);
    }
}
