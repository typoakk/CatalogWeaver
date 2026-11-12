using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ImageMagick;
using MediaImporter;
using MediaImporter.Database;
using MediaImporter.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

public sealed class BackgroundImportTests
{
    [PostgresFact]
    public async Task Background_discovery_download_encoding_and_save_are_idempotent_end_to_end()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Jobs.StartDiscoveryAsync(fixture.Snapshot, null, true, default);
        await fixture.StartAsync();
        await fixture.WaitCompletedAsync();
        var stored = (await fixture.Persistence.ReadAsync(fixture.Provider + "-1", default))!;
        Assert.Equal("Example card", stored.Item!.Title);
        Assert.Equal("completed", stored.Status);
        var image = Assert.Single(stored.Assets);
        Assert.Equal("avif80", image.ActualProfile);
        Assert.Equal("image/avif", image.ContentType);
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, image.RelativePath!)));
        Assert.Equal(1L, await fixture.State.ScalarAsync("SELECT count(*) FROM \"ImporterItems\""));
        Assert.Equal(1L, await fixture.State.ScalarAsync("SELECT count(*) FROM \"ImporterFiles\""));
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.MediaRoot, ".staging")));
        await fixture.Jobs.EnqueueAsync(fixture.Snapshot, [new(1)], true, default);
        await fixture.WaitCompletedAsync();
        Assert.Equal(1L, await fixture.State.ScalarAsync("SELECT count(*) FROM \"ImporterItems\""));
        Assert.Equal(1L, await fixture.State.ScalarAsync("SELECT count(*) FROM \"ImporterAssets\""));
        Assert.Equal(image.Hash, Assert.Single((await fixture.Persistence.ReadAsync(fixture.Provider + "-1", default))!.Assets).Hash);
        Assert.True(fixture.Diagnostics.IsHealthy());
    }

    [PostgresFact]
    public async Task Discovery_recovers_after_page_failure_and_secondary_database_failure_without_service_restart()
    {
        await using var fixture = await Fixture.CreateAsync(withProxy: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        fixture.OnDiscovery = async ct =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                throw new HttpRequestException("Injected source failure, no credentials.");
            }
            return Json("[{\"id\":1}]");
        };
        await fixture.Jobs.StartDiscoveryAsync(fixture.Snapshot, 1, true, default);
        await fixture.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        fixture.Proxy!.Disconnect();
        release.TrySetResult();
        await WaitAsync(() => Task.FromResult(fixture.Diagnostics.Snapshot().Any(value => value.Name == "discovery" && value.Failures > 0)));
        Assert.DoesNotContain(fixture.Diagnostics.Snapshot(), value => value.State == "stopped");
        fixture.Proxy.Reconnect();
        await fixture.State.SqlAsync("UPDATE \"ImporterDiscoveryRuns\" SET \"LeaseUntilUtc\"=now()-interval '1 second',\"DueUtc\"=now()-interval '1 second'");
        await fixture.WaitCompletedAsync();
        Assert.True(requests >= 2);
        Assert.True(fixture.Diagnostics.IsHealthy());
        Assert.Equal(1L, await fixture.State.ScalarAsync("SELECT count(*) FROM \"ImporterItems\""));
    }

    [PostgresFact]
    public async Task A_new_worker_recovers_an_expired_attempt_and_keeps_its_pinned_configuration()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Jobs.EnqueueAsync(fixture.Snapshot, [new(1)], true, default);
        var abandoned = (await fixture.Jobs.ClaimJobAsync([fixture.Provider], default))!;
        Assert.NotNull(abandoned.LeaseToken);
        await fixture.State.SqlAsync("UPDATE \"ImporterJobs\" SET \"LeaseUntilUtc\"=now()-interval '1 second'");
        await fixture.StartAsync();
        await fixture.WaitCompletedAsync();
        var restored = (await fixture.Jobs.GetJobAsync(abandoned.Id, default))!;
        Assert.Equal(abandoned.AdapterVersion, restored.AdapterVersion);
        Assert.Equal(abandoned.AdapterJson, restored.AdapterJson);
        Assert.Equal("completed", restored.State);
        Assert.NotNull(restored.FirstCompletedUtc);
        Assert.Equal(1L, await fixture.State.ScalarAsync("SELECT count(*) FROM \"ImporterItems\""));
    }

    [PostgresFact]
    public async Task Cancellation_at_save_boundary_prevents_the_old_attempt_from_committing()
    {
        await using var fixture = await Fixture.CreateAsync(blockSaving: true);
        await fixture.Jobs.EnqueueAsync(fixture.Snapshot, [new(1)], true, default);
        await fixture.StartAsync();
        await fixture.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var job = Assert.Single(await fixture.Jobs.ListJobsAsync(fixture.Provider, null, 10, default));
        Assert.Equal("saving", job.Stage);
        Assert.True(await fixture.Jobs.CancelJobAsync(job.Id, default));
        fixture.AllowSave.TrySetResult();
        await fixture.SaveExited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("cancelled", (await fixture.Jobs.GetJobAsync(job.Id, default))!.State);
        Assert.Equal(0L, await fixture.State.ScalarAsync("SELECT count(*) FROM \"ImporterItems\""));
        Assert.Equal(0L, await fixture.State.ScalarAsync("SELECT count(*) FROM \"ImporterAssets\""));
    }

    [PostgresFact]
    public async Task Disabled_workers_leave_restored_jobs_untouched_and_report_healthy_idle_loops()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Options.WorkersEnabled = false;
        await fixture.Jobs.EnqueueAsync(fixture.Snapshot, [new(1)], true, default);
        await fixture.StartAsync();
        await WaitAsync(() => Task.FromResult(fixture.Diagnostics.Snapshot().Length == 3
            && fixture.Diagnostics.Snapshot().All(value => value.State == "idle")));
        Assert.True(fixture.Diagnostics.IsHealthy());
        Assert.Equal("pending", Assert.Single(await fixture.Jobs.ListJobsAsync(fixture.Provider, null, 10, default)).State);
        Assert.Equal(0L, await fixture.State.ScalarAsync("SELECT count(*) FROM \"ImporterItems\""));
        fixture.Options.WorkersEnabled = true;
        await fixture.WaitCompletedAsync();
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        while (!await condition()) await Task.Delay(50, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "CatalogWeaverRecovery", Guid.NewGuid().ToString("N"));
        private ServiceProvider services = null!;
        private ImporterWorker? worker;
        private readonly bool blockSaving;
        public string Provider { get; } = "background";
        public string MediaRoot => Path.Combine(root, "media");
        public RecoveryTestState State { get; private set; } = null!;
        public NpgsqlDataSource Source { get; private set; } = null!;
        public ImportJobStore Jobs => services.GetRequiredService<ImportJobStore>();
        public StandalonePersistence Persistence => services.GetRequiredService<StandalonePersistence>();
        public ExecutionDiagnostics Diagnostics => services.GetRequiredService<ExecutionDiagnostics>();
        public ImporterOptions Options => services.GetRequiredService<ImporterOptions>();
        public AdapterSnapshot Snapshot { get; private set; } = null!;
        public LocalDatabaseProxy? Proxy { get; private set; }
        public Func<CancellationToken, Task<HttpResponseMessage>>? OnDiscovery { get; set; }
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Fixture(bool blockSaving) { this.blockSaving = blockSaving; }

        public static async Task<Fixture> CreateAsync(bool withProxy = false, bool blockSaving = false)
        {
            var fixture = new Fixture(blockSaving);
            await fixture.InitializeAsync(withProxy);
            return fixture;
        }

        private async Task InitializeAsync(bool withProxy)
        {
            State = await RecoveryTestState.CreateAsync();
            await StandaloneSchema.InitializeAsync(State.Source, default);
            await new ImportJobStore(State.Source, new()).InitializeAsync(PostgresProviderRateLimiter.SchemaSql, default);
            var connection = new NpgsqlConnectionStringBuilder(State.ConnectionString);
            if (withProxy)
            {
                Proxy = new LocalDatabaseProxy(connection.Host!, connection.Port);
                connection.Host = "127.0.0.1";
                connection.Port = Proxy.Port;
                connection.Timeout = 1;
                connection.CommandTimeout = 2;
            }
            Source = NpgsqlDataSource.Create(connection.ConnectionString);
            Directory.CreateDirectory(MediaRoot);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Importer:AdaptersPath"] = Path.Combine(root, "adapters"),
                ["Importer:EncodersPath"] = Path.Combine(AppContext.BaseDirectory, "encoders"),
                ["Importer:MediaRoot"] = MediaRoot,
                ["Importer:RequestTimeoutSeconds"] = "5"
            }).Build();
            var options = new ImporterOptions { WorkerCount = 2, LeaseSeconds = 30, ImportTimeoutSeconds = 30, MaximumAttempts = 3, MediaRoot = MediaRoot };
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddSingleton(options);
            collection.AddSingleton(TimeProvider.System);
            collection.AddSingleton(Source);
            collection.AddSingleton<ExecutionDiagnostics>();
            collection.AddSingleton<ImportJobStore>();
            collection.AddSingleton<EncoderRegistry>();
            collection.AddSingleton<AdapterRegistry>();
            collection.AddSingleton<IProviderRateLimiter, PostgresProviderRateLimiter>();
            collection.AddSingleton(new HttpClient(new SourceHandler(this)));
            collection.AddTransient<AdapterTransport>();
            collection.AddTransient<AdapterRuntime>();
            collection.AddSingleton(new ImageStorageOptions { MediaRoot = MediaRoot, MinimumFreeBytes = 0 });
            collection.AddSingleton<IImporterImageCodec, ProcessImageCodec>();
            collection.AddSingleton<ImagePipeline>();
            collection.AddSingleton<PostgresDatabaseWriter>();
            collection.AddScoped<StandalonePersistence>();
            collection.AddScoped<IImportPersistence>(provider => blockSaving
                ? new SaveGate(provider.GetRequiredService<StandalonePersistence>(), this)
                : provider.GetRequiredService<StandalonePersistence>());
            collection.AddScoped<ImportOrchestrator>();
            services = collection.BuildServiceProvider();
            var definition = new AdapterDefinition
            {
                Provider = Provider, DisplayName = "Background recovery source", Enabled = true, Kind = "game",
                BaseUrl = "https://example.org", SourceUrlTemplate = "https://example.org/items/{{externalId}}",
                RateLimit = new() { RequestsPerSecond = 1000, RequestsPerMinute = 60000 },
                Destinations = new() { Files = new() { Directory = Provider } },
                Requests = new()
                {
                    ["discover"] = new() { Path = "/ids", ResponseKind = "array", Query = new()
                    { ["page"] = JsonSerializer.SerializeToElement("{{page}}") } },
                    ["item"] = new() { Path = "/items/{{externalId}}", Required = true, ResponseKind = "object", IdentityPath = "$.id" }
                },
                Mapping = new()
                {
                    Fields = new()
                    {
                        ["externalId"] = new() { Path = "$.id" }, ["title"] = new() { Path = "$.title" },
                        ["description"] = new() { Path = "$.description" }
                    },
                    Assets = new() { ["poster"] = new() { Request = "item", UrlPath = "$.poster", RequireUrl = true } }
                },
                AssetPolicies = new() { ["poster"] = new() { Storage = "local", ImageProfile = "avif80", MaxItems = 1 } }
            };
            Snapshot = await services.GetRequiredService<AdapterRegistry>().SaveAsync(Provider,
                JsonSerializer.Serialize(definition, AdapterRegistry.Json), default);
        }

        public async Task StartAsync()
        {
            worker = ActivatorUtilities.CreateInstance<ImporterWorker>(services);
            await worker.StartAsync(default);
        }

        public async Task WaitCompletedAsync()
        {
            await WaitAsync(async () => (await Jobs.ListJobsAsync(Provider, null, 10, default)).SingleOrDefault()?.State == "completed");
            await WaitAsync(() => Task.FromResult(Diagnostics.Snapshot().All(value => value.State == "idle")));
        }

        public async ValueTask DisposeAsync()
        {
            AllowSave.TrySetResult();
            if (worker is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await worker.StopAsync(timeout.Token);
                worker.Dispose();
            }
            await services.DisposeAsync();
            if (Proxy is not null) await Proxy.DisposeAsync();
            await State.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private sealed class SaveGate(StandalonePersistence inner, Fixture fixture) : IImportPersistence
        {
            public Task<PublishedImage> PublishAsync(PreparedImage image, ImportJob? job, CancellationToken ct) => inner.PublishAsync(image, job, ct);
            public async Task<PersistenceResult> SaveAsync(AdapterItem item, IReadOnlyList<AssetResult> assets, string version,
                CancellationToken ct, ImportJob? job = null, DatabaseDestination? destination = null)
            {
                fixture.SaveEntered.TrySetResult();
                try
                {
                    await fixture.AllowSave.Task.WaitAsync(ct);
                    return await inner.SaveAsync(item, assets, version, ct, job, destination);
                }
                finally { fixture.SaveExited.TrySetResult(); }
            }
            public Task CleanupAsync(PersistenceResult result, CancellationToken ct) => inner.CleanupAsync(result, ct);
            public Task<int> SweepOrphansAsync(CancellationToken ct) => inner.SweepOrphansAsync(ct);
        }

        private sealed class SourceHandler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/ids":
                        if (fixture.OnDiscovery is not null) return await fixture.OnDiscovery(ct);
                        return Json(request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal) ? "[]" : "[{\"id\":1}]");
                    case "/items/1":
                        return Json("{\"id\":1,\"title\":\"Example card\",\"description\":\"Description\",\"poster\":\"https://example.org/poster.png\"}");
                    case "/poster.png":
                        using (var image = new MagickImage(MagickColors.Blue, 8, 8))
                            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(image.ToByteArray(MagickFormat.Png)) };
                    default:
                        return new(HttpStatusCode.NotFound);
                }
            }
        }
    }

    /// <summary>A per-fixture network outage; never stops PostgreSQL or disconnects other tests.</summary>
    private sealed class LocalDatabaseProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentDictionary<TcpClient, byte> sockets = new();
        private readonly ConcurrentBag<Task> sessions = [];
        private readonly string host;
        private readonly int backendPort;
        private readonly Task accepting;
        private volatile bool unavailable;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public LocalDatabaseProxy(string host, int port)
        {
            this.host = host;
            backendPort = port;
            listener.Start();
            accepting = AcceptAsync();
        }

        public void Disconnect()
        {
            unavailable = true;
            foreach (var socket in sockets.Keys) socket.Dispose();
        }

        public void Reconnect() => unavailable = false;

        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var socket = await listener.AcceptTcpClientAsync(stop.Token);
                    if (unavailable) socket.Dispose();
                    else sessions.Add(ForwardAsync(socket));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task ForwardAsync(TcpClient front)
        {
            using var back = new TcpClient();
            sockets[front] = 0; sockets[back] = 0;
            try
            {
                if (unavailable) return;
                await back.ConnectAsync(host, backendPort, stop.Token);
                if (unavailable) return;
                var upload = front.GetStream().CopyToAsync(back.GetStream(), stop.Token);
                var download = back.GetStream().CopyToAsync(front.GetStream(), stop.Token);
                await Task.WhenAny(upload, download);
                front.Dispose(); back.Dispose();
                try { await Task.WhenAll(upload, download); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                sockets.TryRemove(front, out _); sockets.TryRemove(back, out _);
                front.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            foreach (var socket in sockets.Keys) socket.Dispose();
            await accepting;
            await Task.WhenAll(sessions);
            stop.Dispose();
        }
    }
}
