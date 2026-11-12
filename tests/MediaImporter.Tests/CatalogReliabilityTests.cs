using System.Text.Json;
using MediaImporter;

namespace MediaImporter.Tests;

public sealed partial class StandalonePersistenceTests
{
    [StandalonePostgresFact]
    public async Task Failed_optional_fields_preserve_values_and_raw_response_but_successful_empty_values_clear_them()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        var original = fixture.Item([]) with { Description = "Keep description", Tags = ["keep-tag"], Genres = ["genre"],
            Metadata = JsonSerializer.SerializeToElement(new { details = new { description = "Keep description" }, main = new { title = "Old" } }),
            FieldStatus = new() { ["title"] = "complete", ["description"] = "complete", ["tags"] = "complete", ["genres"] = "complete" } };
        var saved = await fixture.Persistence.SaveAsync(original, [], "one", default);
        var failed = original with { Title = "New title", Description = "", Tags = [], Genres = [],
            Metadata = JsonSerializer.SerializeToElement(new { main = new { title = "New" } }),
            FieldStatus = new() { ["title"] = "complete", ["description"] = "retry", ["tags"] = "retry" } };
        var preview = await fixture.Persistence.PlanAsync(failed, [], "two", default);
        Assert.Equal("Keep description", preview.Item!.Description);
        var updated = await fixture.Persistence.SaveAsync(failed, [], "two", default);
        Assert.Equal("Keep description", updated.Item!.Description);
        var state = (await fixture.Persistence.ReadAsync(saved.MediaId, default))!;
        Assert.Equal("partial", state.Status);
        Assert.Equal("New title", state.Item!.Title);
        Assert.Equal(new[] { "keep-tag" }, state.Item.Tags);
        Assert.Equal(new[] { "genre" }, state.Item.Genres);
        Assert.Equal("Keep description", state.Item.Metadata!.Value.GetProperty("details").GetProperty("description").GetString());
        await fixture.Persistence.SaveAsync(failed with { FieldStatus = new() { ["title"] = "complete", ["description"] = "complete", ["tags"] = "complete" } },
            [], "three", default);
        state = (await fixture.Persistence.ReadAsync(saved.MediaId, default))!;
        Assert.Equal("completed", state.Status);
        Assert.Equal("", state.Item!.Description);
        Assert.Empty(state.Item.Tags);
    }

    [StandalonePostgresFact]
    public async Task Publication_intent_recovers_own_orphan_without_enabling_unknown_file_deletion()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        var own = await fixture.ManagedFileAsync("owned-after-failed-save");
        var external = await fixture.FileAsync("unregistered-external");
        Assert.Equal(1, await fixture.CountAsync("ImporterPublications"));
        Assert.Equal(0, await fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
        await fixture.SqlAsync("UPDATE \"ImporterPublications\" SET \"ExpiresUtc\"=now()-interval '1 minute'");
        Assert.Equal(1, await fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
        Assert.False(File.Exists(Path.Combine(fixture.MediaRoot, own.RelativePath!)));
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, external.RelativePath!)));
        Assert.Equal(0, await fixture.CountAsync("ImporterPublications"));
    }

    [StandalonePostgresFact]
    public async Task Reusing_an_unknown_file_does_not_claim_ownership_after_an_abandoned_publication()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        var external = await fixture.FileAsync("same-existing-data");
        var reused = await fixture.ManagedFileAsync("same-existing-data");
        Assert.Equal(external.Hash, reused.Hash);
        await fixture.SqlAsync("UPDATE \"ImporterPublications\" SET \"ExpiresUtc\"=now()-interval '1 minute'");
        Assert.Equal(0, await fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, external.RelativePath!)));
        Assert.Equal(0, await fixture.CountAsync("ImporterPublications"));
    }

    [StandalonePostgresFact]
    public async Task Publication_pin_follows_the_live_job_lease_even_after_its_expiry_deadline()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        var store = new ImportJobStore(fixture.Source, new ImporterOptions());
        var definition = new AdapterDefinition { Provider = fixture.Provider };
        var snapshot = new AdapterSnapshot(definition, "revision", JsonSerializer.Serialize(definition, AdapterRegistry.Json));
        await store.EnqueueAsync(snapshot, [new(1)], false, default);
        var job = Assert.IsType<ImportJob>(await store.ClaimJobAsync([fixture.Provider], default));
        var image = await fixture.ManagedFileAsync("live-job-file", job);
        await fixture.SqlAsync("UPDATE \"ImporterPublications\" SET \"ExpiresUtc\"=now()-interval '1 minute'");
        Assert.Equal(0, await fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, image.RelativePath!)));
        Assert.True(await store.CancelJobAsync(job.Id, default));
        Assert.Equal(1, await fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
    }

    [StandalonePostgresFact]
    public async Task Item_cursor_can_enumerate_more_than_one_hundred_rows_with_identical_timestamps()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        await fixture.SqlAsync($$"""
            INSERT INTO "ImporterItems" ("Provider","ExternalId","MediaId","AdapterVersion","MetadataJson","AssetStatusJson","WarningsJson","UpdatedUtc")
            SELECT '{{fixture.Provider}}',n,'{{fixture.Provider}}-'||n,'one',jsonb_build_object('title','Card '||n),'{}','[]','2026-01-01T00:00:00Z'
            FROM generate_series(1,125) n
            """);
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await fixture.Persistence.ListPageAsync(17, cursor, default);
            ids.AddRange(page.Items.Select(value => value.MediaId));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(125, ids.Count);
        Assert.Equal(125, ids.Distinct().Count());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Persistence.ListPageAsync(17, "invalid!", default));
    }

    [StandalonePostgresFact]
    public async Task Concurrent_reads_never_mix_metadata_and_assets_from_different_commits()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        var original = fixture.Item(new() { ["screenshot"] = "complete" });
        await fixture.Persistence.SaveAsync(original with { Title = "0" }, [Link("screenshot", 0) with { SourceUrl = "https://example.org/0" }], "0", default);
        var mediaId = fixture.Provider + "-1";
        var writing = Task.Run(async () =>
        {
            for (var i = 1; i <= 30; i++)
                await fixture.Persistence.SaveAsync(original with { Title = i.ToString() },
                    [Link("screenshot", 0) with { SourceUrl = "https://example.org/" + i }], i.ToString(), default);
        });
        for (var i = 0; i < 70; i++)
        {
            var read = (await fixture.Persistence.ReadAsync(mediaId, default))!;
            Assert.Equal("https://example.org/" + read.Item!.Title, Assert.Single(read.Assets).SourceUrl);
        }
        await writing;
    }

    [StandalonePostgresFact]
    public async Task Item_pagination_does_not_skip_an_unseen_card_updated_between_pages()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        for (var i = 1; i <= 5; i++)
            await fixture.Persistence.SaveAsync(fixture.Item([]) with { ExternalId = i }, [], "initial", default);
        var first = await fixture.Persistence.ListPageAsync(2, null, default);
        Assert.Equal(new long[] { 1, 2 }, first.Items.Select(value => value.ExternalId));
        await fixture.Persistence.SaveAsync(fixture.Item([]) with { ExternalId = 4, Title = "Updated after first page" }, [], "changed", default);
        var second = await fixture.Persistence.ListPageAsync(2, first.NextCursor, default);
        Assert.Equal(new long[] { 3, 4 }, second.Items.Select(value => value.ExternalId));
        Assert.Equal("Updated after first page", second.Items[1].Title);
        var third = await fixture.Persistence.ListPageAsync(2, second.NextCursor, default);
        Assert.Equal(5, Assert.Single(third.Items).ExternalId);
        Assert.Null(third.NextCursor);
    }

    [StandalonePostgresFact]
    public async Task Export_release_requires_an_unchanged_reviewed_reservation_and_keeps_the_physical_file()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        var image = await fixture.ManagedFileAsync("export-to-review");
        await fixture.SqlAsync($$"""
            INSERT INTO "ImporterExportedFiles" ("Hash","RelativePath") VALUES ('{{image.Hash}}','{{image.RelativePath}}');
            UPDATE "ImporterPublications" SET "ExpiresUtc"=now()-interval '1 minute'
            """);
        var reviewed = Assert.Single((await fixture.Persistence.ListExportReservationsAsync(30, null, default)).Items);
        var selection = new ExportReleaseSelection(reviewed.File, reviewed.Revision);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Persistence.ReleaseExportReservationsAsync([selection], "yes", default));
        await fixture.SqlAsync("UPDATE \"ImporterExportedFiles\" SET \"Revision\"=gen_random_uuid()");
        Assert.Equal(0, (await fixture.Persistence.ReleaseExportReservationsAsync([selection], "external-references-verified-absent", default)).Released);
        reviewed = Assert.Single((await fixture.Persistence.ListExportReservationsAsync(30, null, default)).Items);
        Assert.Equal(1, (await fixture.Persistence.ReleaseExportReservationsAsync([new(reviewed.File, reviewed.Revision)],
            "external-references-verified-absent", default)).Released);
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, image.RelativePath!)));
        Assert.Equal(1, await fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
    }

    [StandalonePostgresFact]
    public async Task Garbage_collection_queries_owned_candidates_in_bounded_batches()
    {
        await using var fixture = await Fixture.CreateAsync(null, 4);
        for (var i = 0; i < 11; i++) await fixture.ManagedFileAsync("batch-file-" + i);
        await fixture.SqlAsync("UPDATE \"ImporterPublications\" SET \"ExpiresUtc\"=now()-interval '1 minute'");
        Assert.Equal(11, await fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
        var stats = Assert.IsType<SweepStatistics>(fixture.Persistence.LastSweep);
        Assert.Equal(11, stats.Candidates);
        Assert.Equal(3, stats.Batches);
        Assert.Equal(11, stats.Removed);
    }

    [StandalonePostgresFact]
    public async Task Standalone_migration_upgrades_version_one_and_readiness_rejects_missing_columns()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        await fixture.SqlAsync("DROP TABLE \"ImporterPublications\"; ALTER TABLE \"ImporterExportedFiles\" DROP COLUMN \"Revision\"; UPDATE \"ImporterSchemaVersions\" SET \"Version\"=1 WHERE \"Component\"='standalone'");
        Assert.False(await StandaloneSchema.IsReadyAsync(fixture.Source, default));
        await StandaloneSchema.InitializeAsync(fixture.Source, default);
        Assert.True(await StandaloneSchema.IsReadyAsync(fixture.Source, default));
        await fixture.SqlAsync("ALTER TABLE \"ImporterAssets\" DROP COLUMN \"AssetJson\"");
        Assert.False(await StandaloneSchema.IsReadyAsync(fixture.Source, default));
    }

    [StandalonePostgresFact]
    public async Task Batched_cleanup_can_run_while_a_leased_import_commits_its_published_image()
    {
        await using var fixture = await Fixture.CreateAsync(null, 4);
        for (var i = 0; i < 13; i++) await fixture.ManagedFileAsync("concurrent-cleanup-orphan-" + i);
        var store = new ImportJobStore(fixture.Source, new ImporterOptions());
        var definition = new AdapterDefinition { Provider = fixture.Provider };
        var snapshot = new AdapterSnapshot(definition, "revision", JsonSerializer.Serialize(definition, AdapterRegistry.Json));
        await store.EnqueueAsync(snapshot, [new(1)], false, default);
        var job = Assert.IsType<ImportJob>(await store.ClaimJobAsync([fixture.Provider], default));
        var active = await fixture.ManagedFileAsync("active-during-cleanup", job);
        await fixture.SqlAsync("UPDATE \"ImporterPublications\" SET \"ExpiresUtc\"=now()-interval '1 minute'");
        var sweep = Task.Run(() => fixture.Persistence.SweepOrphansAsync(TimeSpan.Zero, default));
        var saved = await fixture.Persistence.SaveAsync(fixture.Item(new() { ["poster"] = "complete" }), [active], "revision", default, job);
        Assert.Equal(13, await sweep);
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, active.RelativePath!)));
        Assert.Equal(active.Hash, Assert.Single((await fixture.Persistence.ReadAsync(saved.MediaId, default))!.Assets).Hash);
        var stats = Assert.IsType<SweepStatistics>(fixture.Persistence.LastSweep);
        Assert.Equal(13, stats.Candidates);
        Assert.Equal(4, stats.Batches);
        Console.WriteLine($"Concurrent catalog cleanup: {stats.Candidates} candidates, {stats.Batches} batches, {stats.Removed} removed, {stats.ElapsedMilliseconds} ms.");
    }
}
