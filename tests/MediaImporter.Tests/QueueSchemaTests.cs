using MediaImporter;
using Npgsql;

public sealed class QueueSchemaTests
{
    [PostgresFact]
    public async Task Legacy_queue_upgrade_adds_schedule_control_without_losing_pinned_jobs_and_is_idempotent()
    {
        await using var state = await RecoveryTestState.CreateAsync();
        var jobs = new ImportJobStore(state.Source, new());
        await jobs.InitializeAsync(PostgresProviderRateLimiter.SchemaSql, default);
        await state.SqlAsync("""
            INSERT INTO "ImporterJobs" ("Id","Provider","ExternalId","AdapterVersion","AdapterJson","State","DueUtc")
            VALUES ('20d2e263-4c4a-4ae8-925e-ec516f337c12','legacy',1,'old-version','original-pinned-json','completed',now());
            DELETE FROM "ImporterSchemaVersions" WHERE "Component"='queue';
            ALTER TABLE "ImporterJobs" DROP COLUMN "RefreshEnabled";
            """);
        Assert.False(await jobs.IsReadyAsync(default));
        await jobs.InitializeAsync(PostgresProviderRateLimiter.SchemaSql, default);
        await jobs.InitializeAsync(PostgresProviderRateLimiter.SchemaSql, default);
        Assert.True(await jobs.IsReadyAsync(default));
        var job = (await jobs.GetJobAsync(Guid.Parse("20d2e263-4c4a-4ae8-925e-ec516f337c12"), default))!;
        Assert.Equal("original-pinned-json", job.AdapterJson);
        Assert.Equal("old-version", job.AdapterVersion);
        Assert.True(job.RefreshEnabled);
        Assert.Equal("completed", job.State);
    }

    [PostgresFact]
    public async Task Unknown_queue_version_is_not_migrated_or_reported_ready()
    {
        await using var state = await RecoveryTestState.CreateAsync();
        var jobs = new ImportJobStore(state.Source, new());
        await jobs.InitializeAsync(PostgresProviderRateLimiter.SchemaSql, default);
        await state.SqlAsync("UPDATE \"ImporterSchemaVersions\" SET \"Version\"=999 WHERE \"Component\"='queue'");
        Assert.False(await jobs.IsReadyAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.InitializeAsync(PostgresProviderRateLimiter.SchemaSql, default));
        Assert.Equal(999L, await state.ScalarAsync("SELECT \"Version\"::bigint FROM \"ImporterSchemaVersions\" WHERE \"Component\"='queue'"));
    }

    [PostgresFact]
    public async Task Missing_required_column_is_detected_even_when_tables_and_version_are_present()
    {
        await using var state = await RecoveryTestState.CreateAsync();
        var jobs = new ImportJobStore(state.Source, new());
        await jobs.InitializeAsync(PostgresProviderRateLimiter.SchemaSql, default);
        await state.SqlAsync("ALTER TABLE \"ImporterJobs\" RENAME COLUMN \"Stage\" TO \"ObsoleteStage\"");
        Assert.False(await jobs.IsReadyAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.InitializeAsync(PostgresProviderRateLimiter.SchemaSql, default));
    }
}

internal sealed class RecoveryTestState : IAsyncDisposable
{
    private readonly string adminConnection;
    public string Schema { get; } = "recovery_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; }
    public NpgsqlDataSource Source { get; }

    private RecoveryTestState(string connection)
    {
        adminConnection = connection;
        var builder = new NpgsqlConnectionStringBuilder(connection);
        if (builder.Database is null || !builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recovery tests require an isolated test database.");
        builder.SearchPath = Schema;
        builder.ApplicationName = Schema;
        builder.Timeout = 2;
        builder.CommandTimeout = 5;
        ConnectionString = builder.ConnectionString;
        Source = NpgsqlDataSource.Create(ConnectionString);
    }

    public static async Task<RecoveryTestState> CreateAsync()
    {
        var result = new RecoveryTestState(Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")!);
        await using var admin = new NpgsqlConnection(result.adminConnection);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand("CREATE SCHEMA \"" + result.Schema + "\"", admin);
        await create.ExecuteNonQueryAsync();
        return result;
    }

    public async Task SqlAsync(string sql)
    {
        await using var command = Source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var command = Source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand("DROP SCHEMA \"" + Schema + "\" CASCADE", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
