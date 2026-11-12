using System.Text;
using System.Text.Json;
using MediaImporter.Database;
using MediaImporter.Images;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace MediaImporter.Tests;

public sealed class StandalonePostgresFactAttribute : FactAttribute
{
    public StandalonePostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")))
            Skip = "Set MEDIA_IMPORTER_TEST_CONNECTION to an isolated PostgreSQL database.";
    }
}

public sealed partial class StandalonePersistenceTests
{
    [StandalonePostgresFact]
    public async Task Blank_database_schema_initializes_only_service_owned_tables()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.True(await StandaloneSchema.IsReadyAsync(fixture.Source, default));
        await StandaloneSchema.InitializeAsync(fixture.Source, default);
        var tables = await fixture.StringsAsync("SELECT tablename FROM pg_tables WHERE schemaname=current_schema()");
        Assert.Contains("ImporterItems", tables);
        Assert.Contains("ImporterAssets", tables);
        Assert.Contains("ImporterFiles", tables);
        Assert.All(tables, name => Assert.StartsWith("Importer", name));
    }

    [StandalonePostgresFact]
    public async Task Reimport_updates_the_same_identity_and_preserves_order_tags_and_local_file_details()
    {
        await using var fixture = await Fixture.CreateAsync();
        var poster = await fixture.FileAsync("poster");
        var results = new[] { poster, Link("screenshot", 1), Link("screenshot", 0) };
        var item = fixture.Item(new() { ["poster"] = "complete", ["screenshot"] = "complete" });
        var first = await fixture.Persistence.SaveAsync(item, results, "revision1", default);
        var second = await fixture.Persistence.SaveAsync(item with { Title = "Updated title" }, results, "revision2", default);
        Assert.True(first.Created); Assert.False(second.Created);
        Assert.Equal(first.MediaId, second.MediaId);
        Assert.Empty(second.CleanupFiles);
        var stored = Assert.IsType<ImportResult>(await fixture.Persistence.ReadAsync(first.MediaId, default));
        Assert.Equal("Updated title", stored.Item!.Title);
        Assert.Equal(new[] { "tag-one", "tag-two" }, stored.Item.Tags);
        Assert.Equal("revision2", stored.AdapterVersion);
        Assert.Equal(new[] { 0, 1 }, stored.Assets.Where(asset => asset.Role == "screenshot").Select(asset => asset.Position));
        var image = Assert.Single(stored.Assets, asset => asset.Role == "poster");
        Assert.Equal(poster.Hash, image.Hash); Assert.Equal(poster.ContentType, image.ContentType);
        Assert.Equal(1, await fixture.CountAsync("ImporterItems"));
        Assert.Equal(3, await fixture.CountAsync("ImporterAssets"));
        Assert.Equal(1, await fixture.CountAsync("ImporterFiles"));
    }

    [StandalonePostgresFact]
    public async Task Retry_and_omitted_roles_keep_existing_assets_while_explicit_empty_clears_one_role()
    {
        await using var fixture = await Fixture.CreateAsync();
        var poster = await fixture.FileAsync("poster");
        var initial = await fixture.Persistence.SaveAsync(fixture.Item(new() { ["poster"] = "complete", ["screenshot"] = "complete" }),
            [poster, Link("screenshot", 0), Link("screenshot", 1)], "one", default);
        var retry = await fixture.Persistence.SaveAsync(fixture.Item(new() { ["poster"] = "retry" }),
            [poster with { Hash = null, RelativePath = null, State = "retry", Error = "temporary" }], "two", default);
        Assert.Equal(3, retry.Assets.Length);
        Assert.Equal(poster.Hash, Assert.Single(retry.Assets, asset => asset.Role == "poster").Hash);
        Assert.Equal("retry", retry.AssetStatus["poster"]);
        Assert.Equal("complete", retry.AssetStatus["screenshot"]);
        var clear = await fixture.Persistence.SaveAsync(fixture.Item(new() { ["screenshot"] = "complete" }), [], "three", default);
        Assert.Single(clear.Assets); Assert.Equal("poster", clear.Assets[0].Role);
        Assert.Equal(poster.Hash, clear.Assets[0].Hash);
        Assert.Equal("partial", (await fixture.Persistence.ReadAsync(initial.MediaId, default))!.Status);
    }

    [StandalonePostgresFact]
    public async Task A_missing_replacement_retains_the_previous_valid_image()
    {
        await using var fixture = await Fixture.CreateAsync();
        var poster = await fixture.FileAsync("poster");
        await fixture.Persistence.SaveAsync(fixture.Item(new() { ["poster"] = "complete" }), [poster], "one", default);
        var result = await fixture.Persistence.SaveAsync(fixture.Item(new() { ["poster"] = "complete" }),
            [poster with { Hash = null, RelativePath = null, State = "missing", Bytes = 0, Error = "not_found" }], "two", default);
        var retained = Assert.Single(result.Assets);
        Assert.Equal(poster.Hash, retained.Hash);
        Assert.Equal("stored", retained.State);
        Assert.Equal("not_found", retained.Error);
        Assert.Empty(result.CleanupFiles);
    }

    [StandalonePostgresFact]
    public async Task Cleanup_waits_until_the_last_card_reference_has_been_removed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var poster = await fixture.FileAsync("shared");
        var first = fixture.Item(new() { ["poster"] = "complete" });
        var second = first with { ExternalId = 2 };
        await fixture.Persistence.SaveAsync(first, [poster], "one", default);
        await fixture.Persistence.SaveAsync(second, [poster], "one", default);
        var clearFirst = await fixture.Persistence.SaveAsync(first, [], "two", default);
        await fixture.Persistence.CleanupAsync(clearFirst, default);
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, poster.RelativePath!)));
        var clearSecond = await fixture.Persistence.SaveAsync(second, [], "two", default);
        await fixture.Persistence.CleanupAsync(clearSecond, default);
        Assert.False(File.Exists(Path.Combine(fixture.MediaRoot, poster.RelativePath!)));
        Assert.Equal(0, await fixture.CountAsync("ImporterFiles"));
    }

    [StandalonePostgresFact]
    public async Task Orphan_sweep_removes_unreferenced_publications_and_keeps_export_reservations()
    {
        await using var fixture = await Fixture.CreateAsync();
        var orphan = await fixture.FileAsync("orphan");
        var exported = await fixture.FileAsync("exported-before-state-commit");
        await using (var reserve = fixture.Source.CreateCommand("INSERT INTO \"ImporterExportedFiles\" (\"Hash\",\"RelativePath\") VALUES (@hash,@path)"))
        {
            reserve.Parameters.AddWithValue("hash", exported.Hash!); reserve.Parameters.AddWithValue("path", exported.RelativePath!);
            await reserve.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, await fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
        Assert.False(File.Exists(Path.Combine(fixture.MediaRoot, orphan.RelativePath!)));
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, exported.RelativePath!)));
    }

    [StandalonePostgresFact]
    public async Task Unknown_files_in_a_reused_media_root_are_retained_by_default()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        var external = await fixture.FileAsync("existing-target-file");
        Assert.Equal(0, await fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, external.RelativePath!)));
        Assert.Equal(0, await fixture.CountAsync("ImporterFiles"));
    }

    [StandalonePostgresFact]
    public async Task A_read_only_plan_preserves_target_state_export_reservations_and_unpublished_files()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SqlAsync("CREATE TABLE targetcards (id text PRIMARY KEY,title text NOT NULL); INSERT INTO targetcards VALUES ('known-id','Existing target title')");
        var old = await fixture.FileAsync("previous-original");
        var saved = await fixture.Persistence.SaveAsync(fixture.Item(new() { ["poster"] = "complete" }), [old], "one", default);
        var hash = new string('c', 64);
        var candidate = old with { Hash = hash, RelativePath = fixture.Provider + "/" + hash, Bytes = 123 };
        var destination = CardDestination(fixture.Schema);
        var plan = await fixture.Persistence.PlanAsync(fixture.Item(new() { ["poster"] = "complete" }) with { Title = "New title" },
            [candidate], "two", default, destination);

        Assert.Equal("known-id", plan.MediaId);
        Assert.Equal(1, Assert.Single(plan.Database!.Tables).Updates);
        Assert.Contains(plan.Files, file => file.Operation == "publish" && file.Hash == hash);
        Assert.False(File.Exists(Path.Combine(fixture.MediaRoot, candidate.RelativePath!)));
        Assert.Equal(new[] { "Existing target title" }, await fixture.StringsAsync("SELECT title FROM targetcards"));
        var state = (await fixture.Persistence.ReadAsync(saved.MediaId, default))!;
        Assert.Equal("one", state.AdapterVersion);
        Assert.Equal(old.Hash, Assert.Single(state.Assets).Hash);
        Assert.Equal(1, await fixture.CountAsync("ImporterItems"));
        Assert.Equal(1, await fixture.CountAsync("ImporterFiles"));
        Assert.Equal(0, await fixture.CountAsync("ImporterExportedFiles"));
    }

    [StandalonePostgresFact]
    public async Task Skipping_preview_image_processing_defers_the_role_and_keeps_its_previous_material()
    {
        await using var fixture = await Fixture.CreateAsync();
        var poster = await fixture.FileAsync("known-poster");
        var saved = await fixture.Persistence.SaveAsync(fixture.Item(new() { ["poster"] = "complete" }), [poster], "one", default);
        var unresolved = poster with { State = "planned", Hash = null, RelativePath = null, ContentType = null, Bytes = 0 };
        var plan = await fixture.Persistence.PlanAsync(fixture.Item(new() { ["poster"] = "complete" }), [unresolved], "two", default);
        Assert.Equal(new[] { "poster" }, plan.DeferredRoles);
        Assert.Equal("complete", plan.AssetStatus["poster"]);
        Assert.Equal(poster.Hash, Assert.Single(plan.Assets).Hash);
        Assert.Equal("requires_download", Assert.Single(plan.Files).Operation);
        Assert.Contains(plan.Warnings, warning => warning == "plan_requires_image_download:poster");
        Assert.Equal("one", (await fixture.Persistence.ReadAsync(saved.MediaId, default))!.AdapterVersion);
        Assert.Equal(0, await fixture.CountAsync("ImporterExportedFiles"));
    }

    [StandalonePostgresFact]
    public async Task Resolved_destination_identity_replaces_an_earlier_generic_state_identity()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SqlAsync("CREATE TABLE targetcards (id text PRIMARY KEY,title text NOT NULL)");
        var initial = await fixture.Persistence.SaveAsync(fixture.Item([]), [], "one", default);
        var saved = await fixture.Persistence.SaveAsync(fixture.Item([]), [], "two", default, destination: CardDestination(fixture.Schema));
        Assert.Equal("known-id", saved.MediaId);
        Assert.Null(await fixture.Persistence.ReadAsync(initial.MediaId, default));
        Assert.NotNull(await fixture.Persistence.ReadAsync("known-id", default));
        Assert.Equal(new[] { "known-id" }, await fixture.StringsAsync("SELECT id FROM targetcards"));
        Assert.Equal(1, await fixture.CountAsync("ImporterItems"));
    }

    [StandalonePostgresFact]
    public async Task A_cancelled_job_cannot_commit_a_stale_import_attempt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ImportJobStore(fixture.Source, new ImporterOptions());
        var definition = new AdapterDefinition { Provider = fixture.Provider };
        var snapshot = new AdapterSnapshot(definition, "revision", JsonSerializer.Serialize(definition, AdapterRegistry.Json));
        await store.EnqueueAsync(snapshot, [new(1)], false, default);
        var job = Assert.IsType<ImportJob>(await store.ClaimJobAsync([fixture.Provider], default));
        Assert.True(await store.CancelJobAsync(job.Id, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Persistence.SaveAsync(fixture.Item([]), [], "revision", default, job));
        Assert.Equal(0, await fixture.CountAsync("ImporterItems"));
        Assert.Equal(0, await fixture.CountAsync("ImporterExportedFiles"));
    }

    [StandalonePostgresFact]
    public async Task Invalid_stored_paths_are_rejected_before_any_item_or_file_reference_is_committed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var poster = await fixture.FileAsync("poster");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Persistence.SaveAsync(
            fixture.Item(new() { ["poster"] = "complete" }), [poster with { RelativePath = "../" + poster.Hash }], "one", default));
        Assert.Equal(0, await fixture.CountAsync("ImporterItems"));
        Assert.Equal(0, await fixture.CountAsync("ImporterFiles"));
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, poster.RelativePath!)));
    }

    [StandalonePostgresFact]
    public async Task Failed_target_delivery_keeps_previous_state_and_pins_attempted_files_for_crash_safe_retention()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SqlAsync("""
            CREATE TABLE "targetcards" ("provider" text NOT NULL,"external_id" bigint NOT NULL,"title" text NOT NULL CHECK ("title" <> 'reject'),
                PRIMARY KEY ("provider","external_id"))
            """);
        var original = await fixture.FileAsync("original");
        var previous = await fixture.Persistence.SaveAsync(fixture.Item(new() { ["poster"] = "complete" }), [original], "one", default);
        var replacement = await fixture.FileAsync("replacement");
        var destination = new DatabaseDestination { Connection = "target", Tables = [new()
        {
            Name = "card", Schema = fixture.Schema, Table = "targetcards", MatchColumns = ["provider", "external_id"],
            Columns = new() { ["provider"] = new() { Source = "item.provider" }, ["external_id"] = new() { Source = "item.externalId" },
                ["title"] = new() { Source = "item.title" } }
        }] };
        await Assert.ThrowsAsync<PostgresException>(() => fixture.Persistence.SaveAsync(
            fixture.Item(new() { ["poster"] = "complete" }) with { Title = "reject" }, [replacement], "two", default, destination: destination));
        var retained = (await fixture.Persistence.ReadAsync(previous.MediaId, default))!;
        Assert.Equal("Original title", retained.Item!.Title);
        Assert.Equal(original.Hash, Assert.Single(retained.Assets).Hash);
        Assert.Equal(0, await fixture.CountAsync("targetcards"));
        Assert.Equal(2, await fixture.CountAsync("ImporterExportedFiles"));
        Assert.Equal(0, await fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, replacement.RelativePath!)));
    }

    private static AssetResult Link(string role, int position) => new()
    { Role = role, Position = position, SourceUrl = $"https://example.org/{role}/{position}", Storage = "link", State = "remote" };

    private static DatabaseDestination CardDestination(string schema) => new()
    {
        Connection = "target", MediaId = new() { Constant = JsonSerializer.SerializeToElement("known-id") },
        Tables = [new() { Name = "card", Schema = schema, Table = "targetcards", MatchColumns = ["id"],
            Columns = new() { ["id"] = new() { Source = "mediaId" }, ["title"] = new() { Source = "item.title" } } }]
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public string Schema { get; } = "st_test_" + Guid.NewGuid().ToString("N");
        public string Provider { get; } = "s" + Guid.NewGuid().ToString("N")[..15];
        public string MediaRoot { get; } = Path.Combine(Path.GetTempPath(), "MediaImporterStandalone", Guid.NewGuid().ToString("N"));
        public NpgsqlDataSource Source { get; private set; } = null!;
        public StandalonePersistence Persistence { get; private set; } = null!;
        private ImagePipeline pipeline = null!;
        private string adminConnection = "";

        public static async Task<Fixture> CreateAsync(bool? cleanupUnregisteredFiles = true, int cleanupBatchSize = 256)
        {
            var fixture = new Fixture();
            fixture.adminConnection = Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")!;
            var builder = new NpgsqlConnectionStringBuilder(fixture.adminConnection);
            if (builder.Database is null || !builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Standalone persistence tests require a database whose name contains test.");
            await using (var admin = new NpgsqlConnection(fixture.adminConnection))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{fixture.Schema}\"", admin);
                await create.ExecuteNonQueryAsync();
            }
            builder.SearchPath = fixture.Schema;
            fixture.Source = NpgsqlDataSource.Create(builder.ConnectionString);
            await StandaloneSchema.InitializeAsync(fixture.Source, default);
            await new ImportJobStore(fixture.Source, new ImporterOptions()).InitializeAsync(PostgresProviderRateLimiter.SchemaSql, default);
            var values = new Dictionary<string, string?>
            { ["Importer:MediaRoot"] = fixture.MediaRoot, ["ConnectionStrings:target"] = builder.ConnectionString,
                ["Importer:CleanupBatchSize"] = cleanupBatchSize.ToString() };
            if (cleanupUnregisteredFiles.HasValue)
                values["Importer:CleanupUnregisteredFiles"] = cleanupUnregisteredFiles.Value.ToString();
            var settings = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            fixture.pipeline = new ImagePipeline(new ImageStorageOptions
            { MediaRoot = fixture.MediaRoot, MinimumFreeBytes = 0, CoverBudgetBytes = 10 * 1024 * 1024 }, new FixtureCodec());
            fixture.Persistence = new StandalonePersistence(fixture.Source, settings, TimeProvider.System,
                fixture.pipeline, new PostgresDatabaseWriter(settings));
            return fixture;
        }

        public AdapterItem Item(Dictionary<string, string> statuses) => new()
        {
            Provider = Provider, ExternalId = 1, Kind = "game", Title = "Original title", SourceUrl = "https://example.org/item/1",
            Tags = ["tag-one", "tag-two"], Genres = ["genre"], AssetStatus = statuses
        };

        public async Task<AssetResult> FileAsync(string contents)
        {
            var prepared = await pipeline.PrepareAsync(Path.Combine(MediaRoot, Provider), "original",
                _ => Task.FromResult(Encoding.UTF8.GetBytes(contents)), default);
            var published = await pipeline.PublishAsync(prepared, default);
            await pipeline.CompleteAsync(prepared, default);
            return new AssetResult { Role = "poster", Position = 0, SourceUrl = "https://example.org/poster.jpg", Storage = "local",
                State = "stored", Hash = published.Hash, RelativePath = published.RelativePath,
                ContentType = published.ContentType, Bytes = published.Bytes, RequestedProfile = "original", ActualProfile = "original" };
        }

        public async Task<AssetResult> ManagedFileAsync(string contents, ImportJob? job = null)
        {
            var prepared = await pipeline.PrepareAsync(Path.Combine(MediaRoot, Provider), "original",
                _ => Task.FromResult(Encoding.UTF8.GetBytes(contents)), default);
            var published = await Persistence.PublishAsync(prepared, job, default);
            await pipeline.CompleteAsync(prepared, default);
            return new AssetResult { Role = "poster", Position = 0, SourceUrl = "https://example.org/poster.jpg", Storage = "local",
                State = "stored", Hash = published.Hash, RelativePath = published.RelativePath,
                ContentType = published.ContentType, Bytes = published.Bytes, RequestedProfile = "original", ActualProfile = "original" };
        }

        public async Task<int> CountAsync(string table)
        {
            await using var command = Source.CreateCommand($"SELECT count(*)::int FROM \"{table}\"");
            return (int)(await command.ExecuteScalarAsync())!;
        }
        public async Task SqlAsync(string sql)
        { await using var command = Source.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
        public async Task<string[]> StringsAsync(string sql)
        {
            var result = new List<string>(); await using var command = Source.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(reader.GetString(0));
            return result.ToArray();
        }
        public async ValueTask DisposeAsync()
        {
            pipeline.Dispose(); await Source.DisposeAsync();
            await using var admin = new NpgsqlConnection(adminConnection); await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{Schema}\" CASCADE", admin); await drop.ExecuteNonQueryAsync();
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MediaImporterStandalone")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(MediaRoot).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to remove a directory outside standalone test storage.");
            if (Directory.Exists(MediaRoot)) Directory.Delete(MediaRoot, true);
        }
    }
    private sealed class FixtureCodec : IImporterImageCodec
    {
        public Task<ImageDescription> ValidateOriginalAsync(string path, CancellationToken ct) => Task.FromResult(new ImageDescription("image/jpeg", 1, 1, false, 1, 8));
        public Task EncodeAsync(string originalPath, string outputPath, ImageEncodingProfile profile, CancellationToken ct) => throw new NotSupportedException();
        public Task ValidateEncodedAsync(string originalPath, string candidatePath, ImageEncodingProfile profile, CancellationToken ct) => throw new NotSupportedException();
    }
}
