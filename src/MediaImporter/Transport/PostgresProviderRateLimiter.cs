using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace MediaImporter;

public interface IProviderRateLease : IAsyncDisposable
{
    Task DeferAsync(TimeSpan delay, CancellationToken ct);
}

public interface IProviderRateLimiter
{
    Task<IProviderRateLease> AcquireAsync(string provider, RateLimitDefinition limits, CancellationToken ct);
}

/// <summary>One serialized dispatch lane per provider, shared by all service processes.</summary>
public sealed class PostgresProviderRateLimiter(NpgsqlDataSource dataSource) : IProviderRateLimiter
{
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS "ImporterRateStates" (
            "Provider" varchar(24) PRIMARY KEY,
            "Starts" jsonb NOT NULL DEFAULT '[]'::jsonb,
            "BlockedUntilUtc" timestamptz NULL,
            "LastStartedUtc" timestamptz NULL
        );
        """;

    public async Task<IProviderRateLease> AcquireAsync(string provider, RateLimitDefinition limits, CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        var locked = false;
        try
        {
            while (!locked)
            {
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@provider, 1298051))", connection);
                command.Parameters.AddWithValue("provider", provider);
                locked = (bool)(await command.ExecuteScalarAsync(ct))!;
                if (!locked) await Task.Delay(100, ct);
            }
            var lease = new Lease(connection, provider);
            await lease.WaitAsync(limits, ct);
            return lease;
        }
        catch
        {
            try
            {
                if (locked)
                {
                    await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@provider, 1298051))", connection);
                    command.Parameters.AddWithValue("provider", provider);
                    await command.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            finally { await connection.DisposeAsync(); }
            throw;
        }
    }

    private sealed class Lease(NpgsqlConnection connection, string provider) : IProviderRateLease
    {
        public async Task WaitAsync(RateLimitDefinition limits, CancellationToken ct)
        {
            while (true)
            {
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using (var create = new NpgsqlCommand("INSERT INTO \"ImporterRateStates\" (\"Provider\") VALUES (@provider) ON CONFLICT DO NOTHING", connection, transaction))
                {
                    create.Parameters.AddWithValue("provider", provider);
                    await create.ExecuteNonQueryAsync(ct);
                }
                double now, blocked, last;
                double[] starts;
                await using (var read = new NpgsqlCommand("""
                    SELECT extract(epoch FROM clock_timestamp())::double precision,
                        "Starts"::text,
                        coalesce(extract(epoch FROM "BlockedUntilUtc")::double precision, 0),
                        coalesce(extract(epoch FROM "LastStartedUtc")::double precision, 0)
                    FROM "ImporterRateStates" WHERE "Provider" = @provider FOR UPDATE
                    """, connection, transaction))
                {
                    read.Parameters.AddWithValue("provider", provider);
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    await reader.ReadAsync(ct);
                    now = reader.GetDouble(0);
                    starts = JsonSerializer.Deserialize<double[]>(reader.GetString(1))!;
                    blocked = reader.GetDouble(2);
                    last = reader.GetDouble(3);
                }
                starts = starts.Where(x => x > now - 60.001).Order().ToArray();
                var wait = Math.Max(0, Math.Max(blocked - now, last + 1 / limits.RequestsPerSecond - now));
                if (starts.Length >= limits.RequestsPerMinute)
                    wait = Math.Max(wait, starts[starts.Length - limits.RequestsPerMinute] + 60.001 - now);
                if (wait <= 0)
                {
                    await using var save = new NpgsqlCommand("""
                        UPDATE "ImporterRateStates" SET "Starts" = @starts,
                            "LastStartedUtc" = to_timestamp(@now) WHERE "Provider" = @provider
                        """, connection, transaction);
                    save.Parameters.AddWithValue("provider", provider);
                    save.Parameters.AddWithValue("starts", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(starts.Append(now)));
                    save.Parameters.AddWithValue("now", now);
                    await save.ExecuteNonQueryAsync(ct);
                }
                await transaction.CommitAsync(ct);
                if (wait <= 0) return;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 30)), ct);
            }
        }

        public async Task DeferAsync(TimeSpan delay, CancellationToken ct)
        {
            if (delay <= TimeSpan.Zero) return;
            await using var command = new NpgsqlCommand("""
                UPDATE "ImporterRateStates" SET "BlockedUntilUtc" = greatest(
                    coalesce("BlockedUntilUtc", '-infinity'::timestamptz),
                    clock_timestamp() + @seconds * interval '1 second') WHERE "Provider" = @provider
                """, connection);
            command.Parameters.AddWithValue("provider", provider);
            command.Parameters.AddWithValue("seconds", Math.Min(delay.TotalSeconds, 7 * 24 * 3600));
            await command.ExecuteNonQueryAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@provider, 1298051))", connection);
                command.Parameters.AddWithValue("provider", provider);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally { await connection.DisposeAsync(); }
        }
    }
}
