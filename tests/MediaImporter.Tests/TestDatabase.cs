using MediaImporter;
using Npgsql;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEDIA_IMPORTER_TEST_CONNECTION")))
            Skip = "Set MEDIA_IMPORTER_TEST_CONNECTION to an isolated PostgreSQL database.";
    }
}

internal static class TestDatabase
{
    private static readonly SemaphoreSlim QueueInitialization = new(1, 1);

    public static async Task EnsureQueueAsync(NpgsqlDataSource source)
    {
        await QueueInitialization.WaitAsync();
        try
        {
            var jobs = new ImportJobStore(source, new ImporterOptions());
            if (!await jobs.IsReadyAsync(default))
                await jobs.InitializeAsync(PostgresProviderRateLimiter.SchemaSql, default);
        }
        finally { QueueInitialization.Release(); }
    }
}
