using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace MediaImporter.Tests;

public sealed class QueueTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            return;
        var database = new NpgsqlConnectionStringBuilder(connection).Database;
        if (database is null || !database.Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Queue tests require an isolated database whose name contains test.");
        await using var source = NpgsqlDataSource.Create(connection);
        await TestDatabase.EnsureQueueAsync(source);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Duplicate_enqueue_is_idempotent_and_keeps_the_pinned_adapter_revision()
    {
        await using var fixture = new Fixture();
        var first = fixture.Snapshot("Original configuration");
        var changed = fixture.Snapshot("Changed configuration");
        Assert.Equal(1, await fixture.Store.EnqueueAsync(first,
            [new(1001), new(1001)], false, default));
        Assert.Equal(0, await fixture.Store.EnqueueAsync(changed, [new(1001)], false, default));

        var job = Assert.Single(await fixture.Store.ListJobsAsync(fixture.Provider, null, 100, default));
        Assert.Equal(first.Version, job.AdapterVersion);
        Assert.Equal(first.Json, job.AdapterJson);
        Assert.Equal(first.Version, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(job.AdapterJson))));
        Assert.NotEqual(changed.Version, job.AdapterVersion);
    }

    [PostgresFact]
    public async Task A_new_source_modification_requeues_an_idle_job_but_repeated_pages_leave_it_completed()
    {
        await using var fixture = new Fixture();
        var first = fixture.Snapshot("First revision");
        var changed = fixture.Snapshot("Next revision");
        await fixture.Store.EnqueueAsync(first, [new(1001, LastModified: 100)], false, default);
        var job = Assert.IsType<ImportJob>(await fixture.ClaimJobAsync());
        await fixture.Store.FinishJobAsync(job, Completed(job), false, default);
        var saved = Assert.IsType<ImportJob>(await fixture.Store.GetJobAsync(job.Id, default));
        Assert.NotNull(saved.FirstCompletedUtc);

        Assert.Equal(0, await fixture.Store.EnqueueAsync(changed, [new(1001, LastModified: 100)], false, default));
        Assert.Equal("completed", (await fixture.Store.GetJobAsync(job.Id, default))!.State);
        Assert.Equal(1, await fixture.Store.EnqueueAsync(changed, [new(1001, LastModified: 101)], false, default));
        var queued = Assert.IsType<ImportJob>(await fixture.Store.GetJobAsync(job.Id, default));
        Assert.Equal("pending", queued.State);
        Assert.Equal(changed.Json, queued.AdapterJson);
        Assert.Equal(saved.FirstCompletedUtc, queued.FirstCompletedUtc);
    }

    [PostgresFact]
    public async Task Refresh_cannot_replace_the_revision_of_a_currently_leased_job()
    {
        await using var fixture = new Fixture();
        var first = fixture.Snapshot("Active revision");
        await fixture.Store.EnqueueAsync(first, [new(1001)], false, default);
        var active = Assert.IsType<ImportJob>(await fixture.ClaimJobAsync());
        var changed = fixture.Snapshot("Later revision");

        Assert.Equal(0, await fixture.Store.EnqueueAsync(changed, [new(1001)], true, default));
        var current = Assert.IsType<ImportJob>(await fixture.Store.GetJobAsync(active.Id, default));
        Assert.Equal(active.LeaseToken, current.LeaseToken);
        Assert.Equal(first.Json, current.AdapterJson);
        Assert.Equal("importing", current.State);
    }

    [PostgresFact]
    public async Task Concurrent_claimers_receive_distinct_jobs_and_exclusive_lease_tokens()
    {
        await using var fixture = new Fixture();
        await fixture.Store.EnqueueAsync(fixture.Snapshot(),
            Enumerable.Range(1, 12).Select(id => new DiscoveryEntry(id)).ToArray(), false, default);

        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.ClaimJobAsync()));
        Assert.All(claims, claim => Assert.NotNull(claim));
        Assert.Equal(12, claims.Select(claim => claim!.Id).Distinct().Count());
        Assert.Equal(12, claims.Select(claim => claim!.LeaseToken).Distinct().Count());
        Assert.Null(await fixture.ClaimJobAsync());
    }

    [PostgresFact]
    public async Task Expired_lease_is_reclaimed_and_old_tokens_cannot_mutate_the_new_attempt()
    {
        await using var fixture = new Fixture();
        await fixture.Store.EnqueueAsync(fixture.Snapshot(), [new(1001)], false, default);
        var first = Assert.IsType<ImportJob>(await fixture.ClaimJobAsync());
        await fixture.SqlAsync("""
            UPDATE "ImporterJobs" SET "LeaseUntilUtc"=now()-interval '1 second'
            WHERE "Provider"=@provider
            """);
        var next = Assert.IsType<ImportJob>(await fixture.ClaimJobAsync());
        Assert.Equal(first.Id, next.Id);
        Assert.NotEqual(first.LeaseToken, next.LeaseToken);

        Assert.False(await fixture.Store.RenewJobAsync(first, default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store.SetStageAsync(first, "obsolete-stage", default));
        await fixture.Store.FinishJobAsync(first, Completed(first), false, default);
        await fixture.Store.ReleaseJobAsync(first, default);
        var current = Assert.IsType<ImportJob>(await fixture.Store.GetJobAsync(next.Id, default));
        Assert.Equal("importing", current.State);
        Assert.Equal("metadata", current.Stage);
        Assert.Equal(next.LeaseToken, current.LeaseToken);
        Assert.Null(current.ResultJson);

        await fixture.Store.FinishJobAsync(next, Completed(next), false, default);
        Assert.Equal("completed", (await fixture.Store.GetJobAsync(next.Id, default))!.State);
    }

    [PostgresFact]
    public async Task Cancelling_a_leased_job_fences_later_completion_and_renewal()
    {
        await using var fixture = new Fixture();
        await fixture.Store.EnqueueAsync(fixture.Snapshot(), [new(1001)], false, default);
        var job = Assert.IsType<ImportJob>(await fixture.ClaimJobAsync());
        Assert.True(await fixture.Store.CancelJobAsync(job.Id, default));
        Assert.False(await fixture.Store.RenewJobAsync(job, default));
        await fixture.Store.FinishJobAsync(job, Completed(job), false, default);

        Assert.Equal("cancelled", (await fixture.Store.GetJobAsync(job.Id, default))!.State);
        Assert.Null(await fixture.ClaimJobAsync());
        Assert.Equal(1, await fixture.Store.EnqueueAsync(fixture.Snapshot(), [new(1001)], true, default));
        var retried = Assert.IsType<ImportJob>(await fixture.ClaimJobAsync());
        Assert.NotEqual(job.LeaseToken, retried.LeaseToken);
    }

    [PostgresFact]
    public async Task Retry_backoff_is_respected_and_maximum_failures_become_terminal()
    {
        await using var fixture = new Fixture();
        await fixture.Store.EnqueueAsync(fixture.Snapshot(), [new(1001)], false, default);
        ImportJob? last = null;
        for (var attempt = 1; attempt <= fixture.Options.MaximumAttempts; attempt++)
        {
            var job = Assert.IsType<ImportJob>(await fixture.ClaimJobAsync());
            await fixture.Store.FinishJobAsync(job, new ImportResult
            {
                Provider = job.Provider, ExternalId = job.ExternalId, Status = "retry", Error = "TransientSourceError"
            }, false, default);
            last = Assert.IsType<ImportJob>(await fixture.Store.GetJobAsync(job.Id, default));
            Assert.Equal(attempt, last.Attempts);
            Assert.Equal(attempt == fixture.Options.MaximumAttempts ? "failed" : "retry", last.State);
            Assert.Null(await fixture.ClaimJobAsync());
            await fixture.SqlAsync("UPDATE \"ImporterJobs\" SET \"DueUtc\"=now()-interval '1 second' WHERE \"Provider\"=@provider");
        }

        Assert.NotNull(last);
        Assert.Null(await fixture.ClaimJobAsync());
        using var result = JsonDocument.Parse(last.ResultJson!);
        Assert.Equal("failed", result.RootElement.GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task Discovery_page_commit_is_atomic_and_stale_tokens_cannot_enqueue_ids()
    {
        await using var fixture = new Fixture();
        var snapshot = fixture.Snapshot();
        await fixture.Store.StartDiscoveryAsync(snapshot, null, true, default);
        var first = Assert.IsType<DiscoveryRun>(await fixture.ClaimDiscoveryAsync());
        await fixture.SqlAsync("""
            UPDATE "ImporterDiscoveryRuns" SET "LeaseUntilUtc"=now()-interval '1 second'
            WHERE "Provider"=@provider
            """);
        var next = Assert.IsType<DiscoveryRun>(await fixture.ClaimDiscoveryAsync());
        var page = new DiscoveryPage([new(1001), new(1002)], "2");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.CommitDiscoveryPageAsync(first, snapshot, page, default));
        Assert.Empty(await fixture.Store.ListJobsAsync(fixture.Provider, null, 100, default));
        await fixture.Store.CommitDiscoveryPageAsync(next, snapshot, page, default);
        var committed = await fixture.RunAsync();
        Assert.Equal("2", committed.Cursor);
        Assert.Equal(2, committed.Discovered);
        Assert.Equal(2, (await fixture.Store.ListJobsAsync(fixture.Provider, null, 100, default)).Count);
    }

    [PostgresFact]
    public async Task Bounded_discovery_buffers_partial_page_and_resumes_without_skipping_the_remainder()
    {
        await using var fixture = new Fixture();
        var snapshot = fixture.Snapshot("Pinned discovery revision");
        await fixture.Store.StartDiscoveryAsync(snapshot, 3, true, default);
        var initial = Assert.IsType<DiscoveryRun>(await fixture.ClaimDiscoveryAsync());
        await fixture.Store.CommitDiscoveryPageAsync(initial, snapshot,
            new DiscoveryPage(Enumerable.Range(1, 5).Select(id => new DiscoveryEntry(id)).ToArray(), "2"), default);
        var bounded = await fixture.RunAsync();
        Assert.Equal("completed", bounded.State);
        Assert.Equal(3, bounded.Discovered);
        Assert.Equal(new long[] { 4, 5 }, bounded.PendingItems!.Select(item => item.ExternalId));
        Assert.Equal("2", bounded.PendingCursor);
        Assert.Null(bounded.Cursor);

        var changed = fixture.Snapshot("New configuration must not replace the ongoing discovery snapshot");
        await fixture.Store.StartDiscoveryAsync(changed, 4, false, default);
        var partial = Assert.IsType<DiscoveryRun>(await fixture.ClaimDiscoveryAsync());
        Assert.Equal(snapshot.Json, partial.AdapterJson);
        await fixture.Store.CommitDiscoveryPageAsync(partial, snapshot,
            new DiscoveryPage(partial.PendingItems!, partial.PendingCursor), default);
        Assert.Equal(4, (await fixture.RunAsync()).Discovered);

        await fixture.Store.StartDiscoveryAsync(changed, null, false, default);
        var unlimited = Assert.IsType<DiscoveryRun>(await fixture.ClaimDiscoveryAsync());
        Assert.Null(unlimited.MaximumItems);
        Assert.Equal(new long[] { 5 }, unlimited.PendingItems!.Select(item => item.ExternalId));
        await fixture.Store.CommitDiscoveryPageAsync(unlimited, snapshot,
            new DiscoveryPage(unlimited.PendingItems!, unlimited.PendingCursor), default);
        var remaining = await fixture.RunAsync();
        Assert.Equal("2", remaining.Cursor);
        Assert.Empty(remaining.PendingItems!);
        Assert.Equal("pending", remaining.State);

        var finalPage = Assert.IsType<DiscoveryRun>(await fixture.ClaimDiscoveryAsync());
        await fixture.Store.CommitDiscoveryPageAsync(finalPage, snapshot,
            new DiscoveryPage(Enumerable.Range(6, 3).Select(id => new DiscoveryEntry(id)).ToArray(), null), default);
        var done = await fixture.RunAsync();
        Assert.Equal("completed", done.State);
        Assert.Equal(8, done.Discovered);
        var jobs = await fixture.Store.ListJobsAsync(fixture.Provider, null, 100, default);
        Assert.Equal(Enumerable.Range(1, 8).Select(id => (long)id), jobs.Select(job => job.ExternalId).Order());
        Assert.All(jobs, job => Assert.Equal(snapshot.Version, job.AdapterVersion));
        await fixture.Store.StartDiscoveryAsync(snapshot, null, false, default);
        Assert.Null(await fixture.ClaimDiscoveryAsync());
        await fixture.Store.StartDiscoveryAsync(snapshot, 2, true, default);
        Assert.Equal(0, Assert.IsType<DiscoveryRun>(await fixture.ClaimDiscoveryAsync()).Discovered);
    }

    [PostgresFact]
    public async Task Discovery_failure_preserves_checkpoint_and_stops_at_maximum_attempts()
    {
        await using var fixture = new Fixture();
        var snapshot = fixture.Snapshot();
        await fixture.Store.StartDiscoveryAsync(snapshot, null, true, default);
        var initial = Assert.IsType<DiscoveryRun>(await fixture.ClaimDiscoveryAsync());
        await fixture.Store.CommitDiscoveryPageAsync(initial, snapshot, new DiscoveryPage([new(1001)], "2"), default);
        for (var attempt = 1; attempt <= fixture.Options.MaximumAttempts; attempt++)
        {
            var current = Assert.IsType<DiscoveryRun>(await fixture.ClaimDiscoveryAsync());
            await fixture.Store.FailDiscoveryAsync(current, new IOException("Injected source failure"), default);
            var failed = await fixture.RunAsync();
            Assert.Equal("2", failed.Cursor);
            Assert.Equal(1, failed.Discovered);
            Assert.Equal(attempt, failed.Attempts);
            Assert.Equal(attempt == fixture.Options.MaximumAttempts ? "failed" : "retry", failed.State);
            Assert.Null(await fixture.ClaimDiscoveryAsync());
            await fixture.SqlAsync("UPDATE \"ImporterDiscoveryRuns\" SET \"DueUtc\"=now()-interval '1 second' WHERE \"Provider\"=@provider");
        }
        Assert.Null(await fixture.ClaimDiscoveryAsync());
    }

    private static ImportResult Completed(ImportJob job) => new()
    {
        Provider = job.Provider, ExternalId = job.ExternalId, AdapterVersion = job.AdapterVersion,
        MediaId = job.Provider + "-" + job.ExternalId, Status = "completed"
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public string Provider { get; } = "q" + Guid.NewGuid().ToString("N")[..15];
        public ImporterOptions Options { get; } = new() { MaximumAttempts = 3, LeaseSeconds = 60 };
        public NpgsqlDataSource DataSource { get; }
        public ImportJobStore Store { get; }

        public Fixture()
        {
            var connection = Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")!;
            var database = new NpgsqlConnectionStringBuilder(connection).Database;
            if (database is null || !database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Queue tests require an isolated database whose name contains test.");
            DataSource = NpgsqlDataSource.Create(connection);
            Store = new ImportJobStore(DataSource, Options);
        }

        public AdapterSnapshot Snapshot(string name = "Queue test adapter")
        {
            var definition = new AdapterDefinition
            {
                Provider = Provider, DisplayName = name, Enabled = true, Kind = "anime",
                BaseUrl = "https://example.org", SourceUrlTemplate = "https://example.org/items/{{externalId}}"
            };
            var json = JsonSerializer.Serialize(definition, AdapterRegistry.Json);
            var version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            return new AdapterSnapshot(definition, version, json);
        }

        public Task<ImportJob?> ClaimJobAsync() => Store.ClaimJobAsync([Provider], default);
        public Task<DiscoveryRun?> ClaimDiscoveryAsync() => Store.ClaimDiscoveryAsync([Provider], default);
        public async Task<DiscoveryRun> RunAsync()
            => Assert.Single(await Store.ListDiscoveryAsync(default), run => run.Provider == Provider);

        public async Task SqlAsync(string sql)
        {
            await using var command = DataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("provider", Provider);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await SqlAsync("DELETE FROM \"ImporterJobs\" WHERE \"Provider\"=@provider");
            await SqlAsync("DELETE FROM \"ImporterDiscoveryRuns\" WHERE \"Provider\"=@provider");
            await DataSource.DisposeAsync();
        }
    }
}
