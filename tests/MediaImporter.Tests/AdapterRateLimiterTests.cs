using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using MediaImporter;
using Npgsql;

public sealed class AdapterRateLimiterTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        if (new NpgsqlConnectionStringBuilder(connection).Database is not { } name
            || !name.Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Rate tests require an isolated test database.");
        await using var source = NpgsqlDataSource.Create(connection);
        await TestDatabase.EnsureQueueAsync(source);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [RateDatabaseFact]
    public async Task Pacing_and_retry_after_survive_new_limiter_instances()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")!);
        var provider = NewProvider();
        try
        {
            var limits = new RateLimitDefinition { RequestsPerSecond = 5, RequestsPerMinute = 300 };
            await using (var lease = await new PostgresProviderRateLimiter(database).AcquireAsync(provider, limits, CancellationToken.None))
                await lease.DeferAsync(TimeSpan.FromMilliseconds(350), CancellationToken.None);
            var watch = Stopwatch.StartNew();
            await using (await new PostgresProviderRateLimiter(database).AcquireAsync(provider, limits, CancellationToken.None)) { }
            Assert.True(watch.ElapsedMilliseconds >= 300, "The second instance must honor the provider's persisted Retry-After.");
            Assert.Equal(2, await CountAsync(database, provider));
        }
        finally { await DeleteAsync(database, provider); }
    }

    [RateDatabaseFact]
    public async Task Http_429_pause_is_observed_by_a_different_transport_instance()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")!);
        var provider = NewProvider();
        try
        {
            var configuration = new ConfigurationBuilder().Build();
            using var failingHttp = new HttpClient(new AdapterRuntimeTests.FakeHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(350));
                return response;
            }));
            using var succeedingHttp = new HttpClient(new AdapterRuntimeTests.FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }));
            var snapshot = new AdapterSnapshot(new()
            {
                Provider = provider, BaseUrl = "https://example.org", AllowedHosts = ["example.org"],
                RateLimit = new() { RequestsPerSecond = 1000, RequestsPerMinute = 300 }
            }, "revision", "{}");
            var failing = new AdapterTransport(failingHttp, new PostgresProviderRateLimiter(database), configuration);
            var error = await Assert.ThrowsAsync<AdapterReadException>(() => failing.DownloadImageAsync(snapshot, "https://example.org/one.jpg", CancellationToken.None));
            Assert.Equal(429, error.HttpStatusCode);
            var watch = Stopwatch.StartNew();
            var succeeding = new AdapterTransport(succeedingHttp, new PostgresProviderRateLimiter(database), configuration);
            await succeeding.DownloadImageAsync(snapshot, "https://example.org/two.jpg", CancellationToken.None);
            Assert.True(watch.ElapsedMilliseconds >= 280);
        }
        finally { await DeleteAsync(database, provider); }
    }

    [RateDatabaseFact]
    public async Task Rolling_minute_budget_blocks_without_spending_an_extra_slot_on_cancellation()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")!);
        var provider = NewProvider();
        try
        {
            var limiter = new PostgresProviderRateLimiter(database);
            var limits = new RateLimitDefinition { RequestsPerSecond = 1000, RequestsPerMinute = 2 };
            await using (await limiter.AcquireAsync(provider, limits, CancellationToken.None)) { }
            await using (await limiter.AcquireAsync(provider, limits, CancellationToken.None)) { }
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.AcquireAsync(provider, limits, cancel.Token));
            Assert.Equal(2, await CountAsync(database, provider));
            await using (await new PostgresProviderRateLimiter(database).AcquireAsync(provider,
                new() { RequestsPerSecond = 1000, RequestsPerMinute = 3 }, CancellationToken.None)) { }
            Assert.Equal(3, await CountAsync(database, provider));
        }
        finally { await DeleteAsync(database, provider); }
    }

    [RateDatabaseFact]
    public async Task Concurrent_transports_share_actual_dispatch_spacing()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")!);
        var provider = NewProvider();
        try
        {
            var starts = new ConcurrentQueue<long>();
            var configuration = new ConfigurationBuilder().Build();
            using var firstHttp = new HttpClient(new AdapterRuntimeTests.FakeHandler(_ => { starts.Enqueue(Stopwatch.GetTimestamp()); return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }; }));
            using var secondHttp = new HttpClient(new AdapterRuntimeTests.FakeHandler(_ => { starts.Enqueue(Stopwatch.GetTimestamp()); return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }; }));
            var first = new AdapterTransport(firstHttp, new PostgresProviderRateLimiter(database), configuration);
            var second = new AdapterTransport(secondHttp, new PostgresProviderRateLimiter(database), configuration);
            var snapshot = new AdapterSnapshot(new()
            {
                Provider = provider, BaseUrl = "https://example.org", AllowedHosts = ["example.org"],
                RateLimit = new() { RequestsPerSecond = 5, RequestsPerMinute = 300 }
            }, "revision", "{}");
            await Task.WhenAll(first.DownloadImageAsync(snapshot, "https://example.org/one.jpg", CancellationToken.None),
                second.DownloadImageAsync(snapshot, "https://example.org/two.jpg", CancellationToken.None));
            var ordered = starts.Order().ToArray();
            Assert.Equal(2, ordered.Length);
            Assert.True(Stopwatch.GetElapsedTime(ordered[0], ordered[1]) >= TimeSpan.FromMilliseconds(170));
        }
        finally { await DeleteAsync(database, provider); }
    }

    private static string NewProvider() => "rate-" + Guid.NewGuid().ToString("N")[..18];

    private static async Task<int> CountAsync(NpgsqlDataSource source, string provider)
    {
        await using var command = source.CreateCommand("SELECT jsonb_array_length(\"Starts\") FROM \"ImporterRateStates\" WHERE \"Provider\" = $1");
        command.Parameters.AddWithValue(provider);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DeleteAsync(NpgsqlDataSource source, string provider)
    {
        await using var command = source.CreateCommand("DELETE FROM \"ImporterRateStates\" WHERE \"Provider\" = $1");
        command.Parameters.AddWithValue(provider);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class RateDatabaseFactAttribute : FactAttribute
{
    public RateDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")))
            Skip = "Set MEDIA_IMPORTER_TEST_CONNECTION to an isolated PostgreSQL database for durable rate tests.";
    }
}
