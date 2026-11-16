using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MediaImporter.Monitoring;
using MediaImporter.Images;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MediaImporter.Tests;

public sealed class MonitoringTests
{
    private const string Management = "monitoring-test-management-key-at-least-32-characters";
    private static MonitoringAccess Access() => new(new ImporterOptions { ApiKey = Management }, new ConfigurationBuilder().Build());

    [Fact]
    public void Read_credentials_are_separate_from_management_and_accept_bearer_case_insensitively()
    {
        var access = Access(); var context = new DefaultHttpContext();
        Assert.NotEqual(Management,access.ReadKey);
        context.Request.Headers.Authorization="bearer "+access.ReadKey;
        Assert.True(access.CanRead(context)); Assert.False(access.CanManage(context));
        context.Request.Headers.Authorization="Bearer "+Management;
        Assert.False(access.CanRead(context)); Assert.True(access.CanManage(context));
    }

    [Fact]
    public async Task Http_measurements_count_partial_bodies_and_never_retain_urls_headers_or_payloads()
    {
        var telemetry = new MonitoringTelemetry();
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://example.test/private?key=secret-value") { Content = new StringContent("request body") };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "private-header");
        using (var trace = telemetry.Start("source", message))
        {
            await message.Content.CopyToAsync(Stream.Null);
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1000]) };
            trace.Headers(response); trace.Read(120); trace.Fail(new IOException("private exception detail"));
            Assert.Single(telemetry.Active);
        }
        var entry = Assert.Single(telemetry.Drain());
        Assert.Equal(120, entry.Received); Assert.Equal(12, entry.Sent);
        var json = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain("secret-value", json); Assert.DoesNotContain("private-header", json);
        Assert.DoesNotContain("request body", json); Assert.DoesNotContain("private exception", json);
        Assert.DoesNotContain("/private", json); Assert.Empty(telemetry.Active); Assert.Empty(telemetry.Drain());
    }

    [Fact]
    public async Task Server_only_exposes_its_own_data_and_never_hosts_a_client_or_connection_registry()
    {
        var root=Path.Combine(Path.GetTempPath(),"cw-source-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();
        builder.Configuration["Importer:AdaptersPath"]=root;builder.Configuration["Importer:EncodersPath"]=root;
        builder.Services.AddSingleton(new ImporterOptions{ApiKey=Management,MediaRoot=root});
        builder.Services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=unused_monitor_test;Username=test;Timeout=1"));
        builder.Services.AddSingleton<AdapterRegistry>();builder.Services.AddSingleton<EncoderRegistry>();
        builder.Services.AddSingleton<ImportJobStore>();builder.Services.AddSingleton<ExecutionDiagnostics>();builder.Services.AddMonitoring();
        builder.Services.RemoveAll<IHostedService>();
        await using var app=builder.Build();app.MapMonitoring();await app.StartAsync();using var client=app.GetTestClient();
        Assert.Equal("monitoring-server",(await client.GetFromJsonAsync<JsonElement>("/")).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/monitor/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/monitor/api/environments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.PostAsJsonAsync("/monitor/api/login",new{key=Management})).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/monitor/snapshot")).StatusCode);
        client.DefaultRequestHeaders.Authorization=new("Bearer",Access().ReadKey);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/importer/monitoring-key")).StatusCode);
        client.DefaultRequestHeaders.Authorization=new("Bearer",Management);
        Assert.Equal(Access().ReadKey,(await client.GetFromJsonAsync<JsonElement>("/api/v1/importer/monitoring-key")).GetProperty("key").GetString());
        await app.StopAsync();Directory.Delete(root,true);
    }

    [PostgresFact]
    public async Task Completion_events_are_atomic_fenced_distinguish_first_success_and_survive_migration()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); var source = fixture.Source;
        await using var command = source.CreateCommand("""
            INSERT INTO "ImporterJobs" ("Id","Provider","ExternalId","AdapterVersion","AdapterJson","State","DueUtc","LeaseToken")
                VALUES (gen_random_uuid(),'steam',42,'v','{}','importing',now(),gen_random_uuid());
            UPDATE "ImporterJobs" SET "State"='partial';
            UPDATE "ImporterJobs" SET "State"='importing';
            UPDATE "ImporterJobs" SET "State"='completed';
            UPDATE "ImporterJobs" SET "State"='completed' WHERE "LeaseToken"=gen_random_uuid();
            UPDATE "ImporterJobs" SET "State"='importing';
            UPDATE "ImporterJobs" SET "State"='completed';
            SELECT string_agg("Kind",',' ORDER BY "Utc","Kind") FROM "MonitorCompletions";
            """);
        var kinds = (string)(await command.ExecuteScalarAsync())!;
        Assert.Contains("import", kinds); Assert.Contains("refresh", kinds);
        await MonitoringSchema.InitializeAsync(source, default);
        await using var count = source.CreateCommand("SELECT count(*) FROM \"MonitorCompletions\"");
        Assert.Equal(2L, await count.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Http_persistence_is_idempotent_and_rates_use_real_1_5_15_minute_windows()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); var store = fixture.Store;
        var entry = new HttpMeasurement(Guid.NewGuid(), DateTime.UtcNow, "steam", "images", "example.test", 200, null, 120, 12, 50);
        await store.SaveHttpAsync([entry], default); await store.SaveHttpAsync([entry], default);
        await using var seed = fixture.Source.CreateCommand("""
            UPDATE "MonitorIdentity" SET "SinceUtc"=now()-interval '1 hour';
            INSERT INTO "ImporterJobs" ("Id","Provider","ExternalId","AdapterVersion","AdapterJson","State","DueUtc") VALUES (gen_random_uuid(),'steam',42,'v','{}','pending',now());
            INSERT INTO "ImporterItems" VALUES ('steam',42,'steam-42','v','{"fieldStatus":{"title":"retry","description":"complete"}}','{"poster":"retry"}','[]',now());
            INSERT INTO "ImporterAssets" VALUES ('steam',42,'poster',0,'{"role":"poster","state":"stored"}',NULL,NULL);
            INSERT INTO "MonitorCompletions" ("Provider","ExternalId","Kind","Utc") VALUES
                ('steam',1,'import',now()-interval '20 seconds'),('steam',2,'import',now()-interval '2 minutes'),
                ('steam',3,'import',now()-interval '8 minutes'),('steam',1,'refresh',now()-interval '20 seconds');
            """); await seed.ExecuteNonQueryAsync();
        var snapshot = await store.CollectAsync(default);
        var adapter = Assert.Single(snapshot["adapters"]!.AsArray())!;
        Assert.Equal(1L, adapter["traffic"]![0]!["requests"]!.GetValue<long>());
        Assert.Equal(1L, adapter["items"]![0]!["incompleteFields"]!.GetValue<long>());
        Assert.Equal(1L, adapter["items"]![0]!["assetRetryCards"]!.GetValue<long>());
        Assert.Equal("stored", adapter["assets"]![0]!["state"]!.GetValue<string>());
        var rates = adapter["rates"]!.AsArray();
        Assert.Equal(1d, rates[0]!["importsPerMinute"]!.GetValue<double>());
        Assert.Equal(0.4d, rates[1]!["importsPerMinute"]!.GetValue<double>());
        Assert.Equal(0.2d, rates[2]!["importsPerMinute"]!.GetValue<double>());
        Assert.Equal(1d, rates[0]!["refreshesPerMinute"]!.GetValue<double>());
        await store.SaveSnapshotAsync(snapshot, default);
        Assert.Equal(snapshot["serverId"]!.ToJsonString(), (await store.LatestAsync(default))!["serverId"]!.ToJsonString());
        var identity = snapshot["serverId"]!.ToJsonString(); await MonitoringSchema.InitializeAsync(fixture.Source, default);
        Assert.Equal(identity, (await store.CollectAsync(default))["serverId"]!.ToJsonString());
        await using var serverSchema=fixture.Source.CreateCommand("""SELECT to_regclass('"MonitorEnvironments"') IS NULL""");
        Assert.Equal(true,await serverSchema.ExecuteScalarAsync());
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        public required NpgsqlDataSource Source { get; init; }
        public required MonitoringStore Store { get; init; }
        public required string Schema { get; init; }
        public required string Root { get; init; }
        public static async Task<DatabaseFixture> CreateAsync()
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")!);
            if (connection.Database?.Contains("test",StringComparison.OrdinalIgnoreCase)!=true) throw new InvalidOperationException("Isolated test DB required.");
            var schema = "monitor_test_"+Guid.NewGuid().ToString("N");
            await using(var admin = NpgsqlDataSource.Create(connection.ConnectionString)) { await using var create = admin.CreateCommand($"CREATE SCHEMA {schema}"); await create.ExecuteNonQueryAsync(); }
            connection.SearchPath=schema; var source=NpgsqlDataSource.Create(connection.ConnectionString);
            await StandaloneSchema.InitializeAsync(source,default); await new ImportJobStore(source,new()).InitializeAsync(PostgresProviderRateLimiter.SchemaSql,default);
            await MonitoringSchema.InitializeAsync(source,default);
            var root=Path.Combine(Path.GetTempPath(),schema);Directory.CreateDirectory(root);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Importer:AdaptersPath"]=root,["Importer:EncodersPath"]=root }).Build();
            var encoders=new EncoderRegistry(config,NullLogger<EncoderRegistry>.Instance);
            var registry=new AdapterRegistry(config,NullLogger<AdapterRegistry>.Instance,encoders);
            var store=new MonitoringStore(source,registry,encoders,new(source,new()),new(),new(){ApiKey=Management,MediaRoot=root},new(),config);
            return new(){Source=source,Store=store,Schema=schema,Root=root};
        }
        public async ValueTask DisposeAsync()
        {
            await using(var command=Source.CreateCommand($"DROP SCHEMA {Schema} CASCADE")) await command.ExecuteNonQueryAsync();
            await Source.DisposeAsync();Directory.Delete(Root,true);
        }
    }
}
