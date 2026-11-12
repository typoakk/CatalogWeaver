using System.Text.Json;
using MediaImporter.Database;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace MediaImporter.Tests;

public sealed class MappingPostgresFactAttribute : FactAttribute
{
    public MappingPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")))
            Skip = "Set MEDIA_IMPORTER_TEST_CONNECTION to an isolated PostgreSQL test database.";
    }
}

public sealed class DatabaseMappingTests
{
    [Theory]
    [InlineData("public; DROP TABLE media")]
    [InlineData("public\"; DROP TABLE media;--")]
    [InlineData("../public")]
    public void Mapping_rejects_SQL_fragments_and_paths_in_identifiers(string schema)
    {
        var destination = Definition("public");
        destination = destination with { Tables = [destination.Tables[0] with { Schema = schema }] };
        Assert.NotEmpty(PostgresDatabaseWriter.ValidateDefinition(destination));
    }

    [Fact]
    public void Material_replacement_requires_an_explicit_owner_and_role_predicate()
    {
        var destination = Definition("public");
        var assets = destination.Tables[1] with { ReplaceScope = new() { ["media_id"] = Source("result.media") } };
        Assert.Contains(PostgresDatabaseWriter.ValidateDefinition(destination with { Tables = [destination.Tables[0], assets] }),
            error => error.Contains("role", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parent_keys_cannot_be_read_before_their_item_mapping()
    {
        var destination = Definition("public");
        Assert.Contains(PostgresDatabaseWriter.ValidateDefinition(destination with { Tables = destination.Tables.Reverse().ToArray() }),
            error => error.Contains("earlier", StringComparison.Ordinal));
    }

    [MappingPostgresFact]
    public async Task Schema_inspection_and_mapped_upsert_preserve_target_DDL_and_generated_parent_identity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var schemaBefore = await fixture.Writer.InspectAsync("Target", default);
        var table = schemaBefore.Tables.Single(table => table.Schema == fixture.Schema && table.Name == "media");
        Assert.Equal(new[] { "id" }, table.PrimaryKey);
        Assert.Contains(table.UniqueKeys, key => key.Columns.SequenceEqual(new[] { "provider", "external_id" }));
        Assert.True(table.Columns.Single(column => column.Name == "id").Identity);
        Assert.Equal(200, table.Columns.Single(column => column.Name == "title").MaximumLength);
        var child = schemaBefore.Tables.Single(table => table.Schema == fixture.Schema && table.Name == "materials");
        Assert.Equal("media", Assert.Single(child.ForeignKeys).TargetTable);
        Assert.Empty(await fixture.Writer.ValidateAsync(fixture.Mapping, default));

        var item = Item("A title'); DROP TABLE media;--") with { Genres = ["adventure", "fantasy"] };
        var first = await fixture.Writer.WriteAsync(fixture.Mapping, item, [Screenshot(0), Screenshot(1)], "source-41", default);
        var second = await fixture.Writer.WriteAsync(fixture.Mapping, item with { Title = "Updated" }, [Screenshot(0), Screenshot(1)], "source-41", default);
        Assert.Equal(first.Returned["media"], second.Returned["media"]);
        Assert.Equal("1|Updated|2|2", await fixture.ScalarAsync("SELECT (SELECT count(*) FROM " + fixture.Table("media")
            + ")||'|'||(SELECT title FROM " + fixture.Table("media") + ")||'|'||(SELECT count(*) FROM " + fixture.Table("materials")
            + ")||'|'||(SELECT count(*) FROM " + fixture.Table("tags") + ")"));
        var schemaAfter = await fixture.Writer.InspectAsync("Target", default);
        Assert.Equal(JsonSerializer.Serialize(schemaBefore.Tables.Where(t => t.Schema == fixture.Schema)),
            JsonSerializer.Serialize(schemaAfter.Tables.Where(t => t.Schema == fixture.Schema)));
        Assert.Equal("adventure,fantasy", await fixture.ScalarAsync("SELECT array_to_string(genres,',') FROM " + fixture.Table("media")));
    }

    [MappingPostgresFact]
    public async Task Retry_and_omitted_material_roles_preserve_rows_and_complete_empty_clears_only_selected_role()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Writer.WriteAsync(fixture.Mapping, Item(), [Screenshot(0), Screenshot(1)], "source-41", default);
        var id = Convert.ToInt64(first.Returned["media"]);
        await fixture.ExecuteAsync("INSERT INTO " + fixture.Table("materials") + " (media_id,role,position,url) VALUES ("
            + id + ",'background',0,'https://example.org/background.jpg')");
        await fixture.Writer.WriteAsync(fixture.Mapping, Item() with { AssetStatus = new() { ["screenshot"] = "retry" } }, [], "source-41", default);
        Assert.Equal("3", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("materials")));
        await fixture.Writer.WriteAsync(fixture.Mapping, Item() with { AssetStatus = new() }, [], "source-41", default);
        Assert.Equal("3", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("materials")));
        await fixture.Writer.WriteAsync(fixture.Mapping, Item(), [], "source-41", default);
        Assert.Equal("background", await fixture.ScalarAsync("SELECT role FROM " + fixture.Table("materials")));
    }

    [MappingPostgresFact]
    public async Task Target_constraint_failure_rolls_back_parent_and_all_collection_writes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var invalid = Screenshot(0) with { SourceUrl = "invalid-url" };
        await Assert.ThrowsAsync<PostgresException>(() => fixture.Writer.WriteAsync(fixture.Mapping, Item(), [invalid], "source-41", default));
        Assert.Equal("0", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("media")));
        Assert.Equal("0", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("materials")));
    }

    [MappingPostgresFact]
    public async Task Validation_reports_missing_required_columns_without_changing_target_schema()
    {
        await using var fixture = await Fixture.CreateAsync();
        var columns = new Dictionary<string, DatabaseValue>(fixture.Mapping.Tables[0].Columns);
        columns.Remove("title");
        var destination = fixture.Mapping with { Tables = [fixture.Mapping.Tables[0] with { Columns = columns }] };
        Assert.Contains(await fixture.Writer.ValidateAsync(destination, default), error => error.Contains("title", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Writer.WriteAsync(destination, Item(), [], "source-41", default));
        Assert.Equal("0", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("media")));
    }

    [MappingPostgresFact]
    public async Task Null_policies_preserve_existing_values_and_can_supply_a_configured_default()
    {
        await using var fixture = await Fixture.CreateAsync();
        var columns = new Dictionary<string, DatabaseValue>(fixture.Mapping.Tables[0].Columns)
        { ["body"] = new() { Source = "item.metadata.summary", OnNull = "preserve" } };
        var destination = fixture.Mapping with { Tables = [fixture.Mapping.Tables[0] with { Columns = columns }] };
        await fixture.Writer.WriteAsync(destination, Item() with { Metadata = JsonSerializer.SerializeToElement(new { summary = "Keep me" }) }, [], "source-41", default);
        await fixture.Writer.WriteAsync(destination, Item(), [], "source-41", default);
        Assert.Equal("Keep me", await fixture.ScalarAsync("SELECT body FROM " + fixture.Table("media")));
        columns["body"] = new() { Source = "item.metadata.summary", OnNull = "default", Default = JsonSerializer.SerializeToElement("Fallback") };
        await fixture.Writer.WriteAsync(destination, Item(), [], "source-41", default);
        Assert.Equal("Fallback", await fixture.ScalarAsync("SELECT body FROM " + fixture.Table("media")));
        columns["title"] = new() { Source = "item.metadata.heading", OnNull = "preserve" };
        await fixture.Writer.WriteAsync(destination, Item() with { Metadata = JsonSerializer.SerializeToElement(new { heading = "Existing required title" }) }, [], "source-41", default);
        await fixture.Writer.WriteAsync(destination, Item(), [], "source-41", default);
        Assert.Equal("Existing required title", await fixture.ScalarAsync("SELECT title FROM " + fixture.Table("media")));
    }

    [MappingPostgresFact]
    public async Task Read_only_plan_does_not_consume_identity_values_fire_triggers_or_change_any_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("CREATE TABLE " + fixture.Table("audit") + " (calls integer NOT NULL); INSERT INTO " + fixture.Table("audit") + " VALUES(0);"
            + "CREATE FUNCTION \"" + fixture.Schema + "\".touch_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN UPDATE "
            + fixture.Table("audit") + " SET calls=calls+1; RETURN NEW; END; $$; CREATE TRIGGER test_audit BEFORE INSERT OR UPDATE ON "
            + fixture.Table("media") + " FOR EACH ROW EXECUTE FUNCTION \"" + fixture.Schema + "\".touch_audit();");
        var sequence = await fixture.ScalarAsync("SELECT last_value||'|'||is_called FROM " + fixture.Table("media_id_seq"));
        var plan = await fixture.Writer.PlanAsync(fixture.Mapping, Item(), [Screenshot(0)], "source-41", default);
        Assert.Equal(1, plan.Tables[0].Inserts);
        Assert.True(plan.Tables[1].Unresolved > 0);
        Assert.Equal(sequence, await fixture.ScalarAsync("SELECT last_value||'|'||is_called FROM " + fixture.Table("media_id_seq")));
        Assert.Equal("0", await fixture.ScalarAsync("SELECT calls FROM " + fixture.Table("audit")));
        Assert.Equal("0", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("media")));

        await fixture.Writer.WriteAsync(fixture.Mapping, Item(), [Screenshot(0)], "source-41", default);
        Assert.Equal("1", await fixture.ScalarAsync("SELECT calls FROM " + fixture.Table("audit")));
        var known = await fixture.Writer.PlanAsync(fixture.Mapping, Item(), [Screenshot(0)], "source-41", default);
        Assert.Equal(1, known.Tables[0].NoOps);
        Assert.Equal(1, known.Tables[1].Deletes);
        Assert.Equal(1, known.Tables[1].Inserts);
        var beforeNoOp = await fixture.ScalarAsync("SELECT last_value||'|'||is_called FROM " + fixture.Table("media_id_seq"));
        await fixture.Writer.WriteAsync(fixture.Mapping, Item(), [Screenshot(0)], "source-41", default);
        Assert.Equal("1", await fixture.ScalarAsync("SELECT calls FROM " + fixture.Table("audit")));
        Assert.Equal(beforeNoOp, await fixture.ScalarAsync("SELECT last_value||'|'||is_called FROM " + fixture.Table("media_id_seq")));
    }

    [MappingPostgresFact]
    public async Task Configured_identity_lookups_seed_maps_and_value_transforms_are_provider_independent()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("CREATE TABLE " + fixture.Table("ids") + " (provider text,external_id bigint,media_id text,UNIQUE(provider,external_id)); INSERT INTO "
            + fixture.Table("ids") + " VALUES ('source',41,'existing-alias')");
        var destination = fixture.Mapping with
        {
            MediaId = new() { Coalesce =
            [
                new() { Lookup = new() { Schema = fixture.Schema, Table = "ids", Column = "media_id", Where = new()
                    { ["provider"] = Source("item.provider"), ["external_id"] = Source("item.externalId") } } },
                new() { Source = "item.externalId", MapMissing = "null", Map = new()
                    { ["42"] = JsonSerializer.SerializeToElement("configured-seed") } },
                new() { Template = "{item.provider}-{item.externalId}" }
            ] }
        };
        Assert.Equal("existing-alias", await fixture.Writer.ResolveMediaIdAsync(destination, Item(), "unused", default));
        Assert.Equal("configured-seed", await fixture.Writer.ResolveMediaIdAsync(destination, Item() with { ExternalId = 42 }, "unused", default));
        Assert.Equal("another-43", await fixture.Writer.ResolveMediaIdAsync(destination, Item() with { Provider = "another", ExternalId = 43 }, "unused", default));
        var columns = new Dictionary<string, DatabaseValue>(destination.Tables[0].Columns)
        {
            ["title"] = new() { Source = "item.title", Limit = 4 },
            ["body"] = new() { Source = "item.genres", Join = " / " }
        };
        destination = destination with { Tables = [destination.Tables[0] with { Columns = columns }] };
        var written = await fixture.Writer.WriteAsync(destination, Item("Long title") with { Genres = ["one", "two"] }, [], "unused", default);
        Assert.Equal("existing-alias", written.MediaId);
        Assert.Equal("Long|one / two", await fixture.ScalarAsync("SELECT title||'|'||body FROM " + fixture.Table("media")));
    }

    [MappingPostgresFact]
    public async Task Insert_only_columns_and_role_gated_updates_preserve_user_data_and_clear_only_explicit_empty()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("ALTER TABLE " + fixture.Table("media") + " ADD accent text NOT NULL DEFAULT 'default';");
        var cardColumns = new Dictionary<string, DatabaseValue>(fixture.Mapping.Tables[0].Columns)
        { ["accent"] = new() { Constant = JsonSerializer.SerializeToElement("initial"), OnUpdate = "preserve" } };
        cardColumns.Remove("body");
        var destination = fixture.Mapping with { Tables =
        [
            fixture.Mapping.Tables[0] with { Columns = cardColumns },
            new() { Name = "poster", Schema = fixture.Schema, Table = "media", Operation = "update", Role = "poster",
                PreserveOnStates = ["missing"], MatchColumns = ["provider", "external_id"], Columns = new()
                { ["provider"] = Source("item.provider"), ["external_id"] = Source("item.externalId"),
                    ["body"] = new() { Source = "asset.url", Role = "poster", First = true, OnNull = "default", Default = JsonSerializer.SerializeToElement("") } } }
        ] };
        var item = Item() with { AssetStatus = new() { ["poster"] = "complete" } };
        await fixture.Writer.WriteAsync(destination, item, [new() { Role = "poster", SourceUrl = "https://example.org/poster.jpg", State = "remote" }], "source-41", default);
        await fixture.ExecuteAsync("UPDATE " + fixture.Table("media") + " SET accent='user-color'");
        await fixture.Writer.WriteAsync(destination, item with { AssetStatus = new() { ["poster"] = "retry" } }, [], "source-41", default);
        Assert.Equal("https://example.org/poster.jpg", await fixture.ScalarAsync("SELECT body FROM " + fixture.Table("media")));
        await fixture.Writer.WriteAsync(destination, item, [new() { Role = "poster", State = "missing" }], "source-41", default);
        Assert.Equal("https://example.org/poster.jpg", await fixture.ScalarAsync("SELECT body FROM " + fixture.Table("media")));
        await fixture.Writer.WriteAsync(destination, item, [], "source-41", default);
        Assert.Equal("user-color|", await fixture.ScalarAsync("SELECT accent||'|'||body FROM " + fixture.Table("media")));
    }

    [MappingPostgresFact]
    public async Task Metadata_inspection_reads_views_and_unpopulated_materialized_views_without_executing_them()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("CREATE FUNCTION \"" + fixture.Schema + "\".business_canary() RETURNS integer IMMUTABLE LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'business view was evaluated'; END; $$;"
            + "CREATE VIEW " + fixture.Table("business_view") + " AS SELECT \"" + fixture.Schema + "\".business_canary() AS v;"
            + "CREATE MATERIALIZED VIEW " + fixture.Table("materialized") + " AS SELECT 1::integer AS v WITH NO DATA;"
            + "CREATE UNIQUE INDEX materialized_key ON " + fixture.Table("materialized") + "(v);");
        var schema = await fixture.Writer.InspectAsync("Target", default);
        Assert.Equal("v", schema.Tables.Single(table => table.Schema == fixture.Schema && table.Name == "business_view").Kind);
        var materialized = schema.Tables.Single(table => table.Schema == fixture.Schema && table.Name == "materialized");
        Assert.Equal("m", materialized.Kind);
        Assert.Contains(materialized.Indexes, index => index.Name == "materialized_key");
        Assert.Equal("false", await fixture.ScalarAsync("SELECT relispopulated::text FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='"
            + fixture.Schema + "' AND c.relname='materialized'"));
    }

    [MappingPostgresFact]
    public async Task Metadata_inspection_tolerates_relations_disappearing_during_schema_discovery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var churn = Task.Run(async () =>
        {
            for (var i = 0; i < 12; i++)
            {
                await fixture.ExecuteAsync("CREATE TABLE " + fixture.Table("scratch") + "(id integer PRIMARY KEY); CREATE INDEX scratch_extra ON " + fixture.Table("scratch") + "(id);");
                await Task.Delay(5);
                await fixture.ExecuteAsync("DROP TABLE " + fixture.Table("scratch"));
            }
        });
        for (var i = 0; i < 3; i++)
        {
            var schema = await fixture.Writer.InspectAsync("Target", default);
            Assert.Contains(schema.Tables, table => table.Schema == fixture.Schema && table.Name == "media");
        }
        await churn;
    }

    [MappingPostgresFact]
    public async Task Read_only_insert_plan_validates_numbers_UUIDs_and_lengths_using_the_writer_conversions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("CREATE TABLE " + fixture.Table("typed") + " (id text PRIMARY KEY,amount integer,token uuid,label varchar(3))");
        var valid = new Dictionary<string, DatabaseValue>
        {
            ["id"] = new() { Constant = JsonSerializer.SerializeToElement("one") },
            ["amount"] = new() { Constant = JsonSerializer.SerializeToElement("42") },
            ["token"] = new() { Constant = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()) },
            ["label"] = new() { Constant = JsonSerializer.SerializeToElement("abc") }
        };
        foreach (var (column, value) in new[] { ("amount", "not-a-number"), ("amount", "999999999999999999999"),
            ("token", "not-a-uuid"), ("label", "too-long") })
        {
            var columns = new Dictionary<string, DatabaseValue>(valid) { [column] = new() { Constant = JsonSerializer.SerializeToElement(value) } };
            var destination = new DatabaseDestination { Connection = "Target", Tables = [new()
            { Name = "typed", Schema = fixture.Schema, Table = "typed", MatchColumns = ["id"], Columns = columns }] };
            var planned = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Writer.PlanAsync(destination, Item(), [], "one", default));
            var written = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Writer.WriteAsync(destination, Item(), [], "one", default));
            Assert.Equal(planned.Message, written.Message);
            Assert.Contains(column, planned.Message);
            Assert.DoesNotContain(value, planned.Message);
        }
        Assert.Equal("0", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("typed")));
    }

    [MappingPostgresFact]
    public async Task Unresolved_generated_parent_key_does_not_hide_an_invalid_known_child_value()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mapping = fixture.Mapping;
        var columns = new Dictionary<string, DatabaseValue>(mapping.Tables[1].Columns)
        { ["position"] = new() { Constant = JsonSerializer.SerializeToElement("invalid-position") } };
        mapping = mapping with { Tables = [mapping.Tables[0], mapping.Tables[1] with { Columns = columns }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Writer.PlanAsync(mapping, Item(), [Screenshot(0)], "source-41", default));
        Assert.Equal("0", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("media")));
        Assert.Equal("0", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("materials")));
    }

    [MappingPostgresFact]
    public async Task Failed_fields_preserve_the_actual_target_values_and_collections_without_relying_on_service_history()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Writer.WriteAsync(fixture.Mapping, Item(), [], "source-41", default);
        await fixture.ExecuteAsync("UPDATE " + fixture.Table("media") + " SET body='Externally edited body'");
        var failed = Item("New title") with { Description = "", Tags = [],
            FieldStatus = new() { ["title"] = "complete", ["description"] = "retry", ["tags"] = "retry" } };
        var plan = await fixture.Writer.PlanAsync(fixture.Mapping, failed, [], "source-41", default);
        Assert.Contains(plan.Tables, table => table.Name == "tags" && table.SkippedReason is not null);
        await fixture.Writer.WriteAsync(fixture.Mapping, failed, [], "source-41", default);
        Assert.Equal("Externally edited body", await fixture.ScalarAsync("SELECT body FROM " + fixture.Table("media")));
        Assert.Equal("New title", await fixture.ScalarAsync("SELECT title FROM " + fixture.Table("media")));
        Assert.Equal("2", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("tags")));
        await fixture.Writer.WriteAsync(fixture.Mapping, failed with
            { FieldStatus = new() { ["title"] = "complete", ["description"] = "complete", ["tags"] = "complete" } }, [], "source-41", default);
        Assert.Equal("", await fixture.ScalarAsync("SELECT body FROM " + fixture.Table("media")));
        Assert.Equal("0", await fixture.ScalarAsync("SELECT count(*) FROM " + fixture.Table("tags")));
    }

    private static AdapterItem Item(string title = "Example") => new()
    {
        Provider = "source", ExternalId = 41, Kind = "game", Title = title, Description = "Body", Tags = ["one", "two"],
        AssetStatus = new() { ["screenshot"] = "complete" }
    };
    private static AssetResult Screenshot(int position) => new()
    { Role = "screenshot", Position = position, Storage = "link", SourceUrl = "https://example.org/" + position + ".jpg" };
    private static DatabaseValue Source(string value) => new() { Source = value };

    private static DatabaseDestination Definition(string schema) => new()
    {
        Connection = "Target",
        Tables =
        [
            new()
            {
                Name = "media", Schema = schema, Table = "media", MatchColumns = ["provider", "external_id"], Returning = "id",
                Columns = new() { ["provider"] = Source("item.provider"), ["external_id"] = Source("item.externalId"),
                    ["title"] = Source("item.title"), ["body"] = Source("item.description"), ["genres"] = Source("item.genres") }
            },
            new()
            {
                Name = "screenshots", Schema = schema, Table = "materials", Scope = "assets", Role = "screenshot",
                MatchColumns = ["media_id", "role", "position"],
                ReplaceScope = new() { ["media_id"] = Source("result.media"), ["role"] = Source("asset.role") },
                Columns = new() { ["media_id"] = Source("result.media"), ["role"] = Source("asset.role"),
                    ["position"] = Source("asset.position"), ["url"] = Source("asset.url") }
            },
            new()
            {
                Name = "tags", Schema = schema, Table = "tags", Scope = "tags", MatchColumns = ["media_id", "value"],
                ReplaceScope = new() { ["media_id"] = Source("result.media") },
                Columns = new() { ["media_id"] = Source("result.media"), ["value"] = Source("row.value") }
            }
        ]
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public string Schema { get; } = "mapping_test_" + Guid.NewGuid().ToString("N");
        public DatabaseDestination Mapping => Definition(Schema);
        public required NpgsqlConnection Connection { get; init; }
        public required PostgresDatabaseWriter Writer { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")!;
            var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
            if (database is null || !database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Database mapping tests require an isolated test database.");
            var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
            var fixture = new Fixture { Connection = connection, Writer = new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Target"] = connectionString }).Build()) };
            await fixture.ExecuteAsync("CREATE SCHEMA \"" + fixture.Schema + "\"; CREATE TABLE " + fixture.Table("media") + """
                (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,provider text NOT NULL,external_id bigint NOT NULL,
                    title varchar(200) NOT NULL,body text,genres text[] NOT NULL DEFAULT '{}',UNIQUE(provider,external_id));
                """ + " CREATE TABLE " + fixture.Table("materials") + " (media_id bigint NOT NULL REFERENCES " + fixture.Table("media")
                + "(id),role text NOT NULL,position integer NOT NULL,url text NOT NULL CHECK(url LIKE 'https://%'),UNIQUE(media_id,role,position));"
                + " CREATE TABLE " + fixture.Table("tags") + " (media_id bigint NOT NULL REFERENCES " + fixture.Table("media")
                + "(id),value text NOT NULL,UNIQUE(media_id,value));");
            return fixture;
        }
        public string Table(string name) => '"' + Schema + "\".\"" + name + '"';
        public async Task ExecuteAsync(string sql) { await using var command = new NpgsqlCommand(sql, Connection); await command.ExecuteNonQueryAsync(); }
        public async Task<string> ScalarAsync(string sql) { await using var command = new NpgsqlCommand(sql, Connection); return Convert.ToString(await command.ExecuteScalarAsync())!; }
        public async ValueTask DisposeAsync()
        {
            await ExecuteAsync("DROP SCHEMA \"" + Schema + "\" CASCADE");
            await Connection.DisposeAsync();
        }
    }
}
