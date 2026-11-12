using System.Data;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace MediaImporter;

public sealed class ImportJobStore(NpgsqlDataSource dataSource, ImporterOptions options)
{
    private const string JobColumns = """
        "Id", "Provider", "ExternalId", "AdapterVersion", "AdapterJson"::text,
        "State", "Stage", "Attempts", "DueUtc", "LeaseUntilUtc", "LeaseToken",
        "ResultJson"::text, "CreatedUtc", "UpdatedUtc", "FirstCompletedUtc", "RefreshEnabled"
        """;
    private const string DiscoveryColumns = """
        "Id", "Provider", "AdapterVersion", "AdapterJson"::text, "Cursor", "MaximumItems",
        "Discovered", "Pages", "State", "Attempts", "DueUtc", "LeaseUntilUtc", "LeaseToken", "Error", "UpdatedUtc",
        "PendingItems"::text, "PendingCursor"
        """;
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS "ImporterJobs" (
            "Id" uuid PRIMARY KEY, "Provider" varchar(24) NOT NULL, "ExternalId" bigint NOT NULL,
            "AdapterVersion" varchar(64) NOT NULL, "AdapterJson" text NOT NULL,
            "State" varchar(24) NOT NULL, "Stage" varchar(64) NOT NULL DEFAULT 'queued',
            "Attempts" integer NOT NULL DEFAULT 0, "DueUtc" timestamptz NOT NULL,
            "LeaseUntilUtc" timestamptz, "LeaseToken" uuid, "ResultJson" jsonb,
            "SourceModified" bigint, "CreatedUtc" timestamptz NOT NULL DEFAULT now(),
            "UpdatedUtc" timestamptz NOT NULL DEFAULT now(), "FirstCompletedUtc" timestamptz,
            "RefreshEnabled" boolean NOT NULL DEFAULT true,
            UNIQUE ("Provider", "ExternalId"), CHECK ("ExternalId" BETWEEN 1 AND 2147483647)
        );
        CREATE INDEX IF NOT EXISTS "IX_ImporterJobs_Due" ON "ImporterJobs" ("State", "DueUtc");
        CREATE INDEX IF NOT EXISTS "IX_ImporterJobs_Created" ON "ImporterJobs" ("CreatedUtc" DESC, "Id" DESC);
        CREATE TABLE IF NOT EXISTS "ImporterDiscoveryRuns" (
            "Id" uuid PRIMARY KEY, "Provider" varchar(24) NOT NULL UNIQUE,
            "AdapterVersion" varchar(64) NOT NULL, "AdapterJson" text NOT NULL,
            "Cursor" text, "MaximumItems" integer, "Discovered" integer NOT NULL DEFAULT 0,
            "Pages" integer NOT NULL DEFAULT 0, "State" varchar(24) NOT NULL,
            "Attempts" integer NOT NULL DEFAULT 0, "DueUtc" timestamptz NOT NULL,
            "LeaseUntilUtc" timestamptz, "LeaseToken" uuid, "Error" varchar(96),
            "UpdatedUtc" timestamptz NOT NULL DEFAULT now(),
            "PendingItems" jsonb NOT NULL DEFAULT '[]'::jsonb, "PendingCursor" text
        );
        ALTER TABLE "ImporterDiscoveryRuns" ADD COLUMN IF NOT EXISTS "PendingItems" jsonb NOT NULL DEFAULT '[]'::jsonb;
        ALTER TABLE "ImporterDiscoveryRuns" ADD COLUMN IF NOT EXISTS "PendingCursor" text;
        ALTER TABLE "ImporterJobs" ADD COLUMN IF NOT EXISTS "RefreshEnabled" boolean NOT NULL DEFAULT true;
        """;

    public async Task InitializeAsync(string rateSchemaSql, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var prepare = new NpgsqlCommand("SELECT pg_advisory_xact_lock(741857209);" + QueueSchema.VersionsSql, connection, transaction))
            await prepare.ExecuteNonQueryAsync(ct);
        var current = await QueueSchema.ReadVersionAsync(connection, transaction, ct);
        if (current is not null && current != QueueSchema.Version)
            throw new InvalidOperationException("Unsupported queue database schema version; use a compatible release or restore its backup.");
        await using (var command = new NpgsqlCommand(SchemaSql + rateSchemaSql, connection, transaction))
            await command.ExecuteNonQueryAsync(ct);
        if (!await QueueSchema.HasColumnsAsync(connection, transaction, ct))
            throw new InvalidOperationException("Queue database columns are incompatible; restore or repair the service schema before starting workers.");
        await using (var version = new NpgsqlCommand("INSERT INTO \"ImporterSchemaVersions\" (\"Component\",\"Version\") VALUES ('queue',@version) ON CONFLICT (\"Component\") DO NOTHING", connection, transaction))
        {
            version.Parameters.AddWithValue("version", QueueSchema.Version);
            await version.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await QueueSchema.HasColumnsAsync(connection, null, ct)
            && await QueueSchema.ReadVersionAsync(connection, null, ct) == QueueSchema.Version;
    }

    public async Task<int> EnqueueAsync(AdapterSnapshot adapter, IReadOnlyList<DiscoveryEntry> entries,
        bool refresh, CancellationToken ct, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null)
    {
        var owned = connection is null;
        connection ??= await dataSource.OpenConnectionAsync(ct);
        try
        {
            var count = 0;
            foreach (var entry in entries.DistinctBy(x => x.ExternalId))
            {
                if (entry.ExternalId is < 1 or > int.MaxValue)
                    throw new ArgumentException("External IDs must be positive 32-bit integers.");
                await using var command = new NpgsqlCommand("""
                    INSERT INTO "ImporterJobs" ("Id", "Provider", "ExternalId", "AdapterVersion", "AdapterJson",
                        "State", "DueUtc", "SourceModified")
                    VALUES (@id, @provider, @externalId, @version, @json, 'pending', now(), @modified)
                    ON CONFLICT ("Provider", "ExternalId") DO UPDATE SET
                        "AdapterVersion" = EXCLUDED."AdapterVersion", "AdapterJson" = EXCLUDED."AdapterJson",
                        "State" = 'pending', "Stage" = 'queued', "Attempts" = 0, "DueUtc" = now(),
                        "RefreshEnabled" = CASE WHEN @refresh THEN true ELSE "ImporterJobs"."RefreshEnabled" END,
                        "LeaseUntilUtc" = NULL, "LeaseToken" = NULL, "UpdatedUtc" = now(),
                        "SourceModified" = COALESCE(EXCLUDED."SourceModified", "ImporterJobs"."SourceModified")
                    WHERE ("ImporterJobs"."LeaseUntilUtc" IS NULL OR "ImporterJobs"."LeaseUntilUtc" < now())
                      AND (@refresh OR ("ImporterJobs"."RefreshEnabled" AND EXCLUDED."SourceModified" > COALESCE("ImporterJobs"."SourceModified", 0)))
                    """, connection, transaction);
                command.Parameters.AddWithValue("id", Guid.NewGuid());
                AddSnapshot(command, adapter);
                command.Parameters.AddWithValue("externalId", entry.ExternalId);
                command.Parameters.AddWithValue("refresh", refresh);
                command.Parameters.AddWithValue("modified", NpgsqlDbType.Bigint, (object?)entry.LastModified ?? DBNull.Value);
                count += await command.ExecuteNonQueryAsync(ct);
            }
            return count;
        }
        finally { if (owned) await connection.DisposeAsync(); }
    }

    public async Task<ImportJob?> ClaimJobAsync(string[] providers, CancellationToken ct)
    {
        if (providers.Length == 0) return null;
        await using var command = dataSource.CreateCommand($"""
            WITH next AS (
                SELECT "Id" FROM "ImporterJobs"
                WHERE "Provider" = ANY(@providers) AND "State" IN ('pending','retry','partial','importing','completed')
                    AND ("State" <> 'completed' OR "RefreshEnabled")
                    AND "Attempts" < @maximum AND "DueUtc" <= now()
                    AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc" < now())
                ORDER BY ("FirstCompletedUtc" IS NOT NULL), "DueUtc", "CreatedUtc"
                FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE "ImporterJobs" SET "State" = 'importing', "Stage" = 'metadata', "UpdatedUtc" = now(),
                "LeaseUntilUtc" = now() + @lease * interval '1 second', "LeaseToken" = @token
            WHERE "Id" IN (SELECT "Id" FROM next) RETURNING {JobColumns}
            """);
        command.Parameters.AddWithValue("providers", providers);
        command.Parameters.AddWithValue("maximum", options.MaximumAttempts);
        command.Parameters.AddWithValue("lease", options.LeaseSeconds);
        command.Parameters.AddWithValue("token", Guid.NewGuid());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJob(reader) : null;
    }

    public async Task<bool> RenewJobAsync(ImportJob job, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE "ImporterJobs" SET "LeaseUntilUtc" = now() + @lease * interval '1 second', "UpdatedUtc" = now()
            WHERE "Id" = @id AND "LeaseToken" = @token AND "State" = 'importing'
            """);
        AddJobLease(command, job);
        command.Parameters.AddWithValue("lease", options.LeaseSeconds);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task FinishJobAsync(ImportJob job, ImportResult result, bool terminal, CancellationToken ct, int? refreshHours = null)
    {
        var completed = result.Status == "completed";
        var attempt = completed ? 0 : job.Attempts + 1;
        var state = result.Status;
        if (!completed && (terminal || attempt >= options.MaximumAttempts)) state = "failed";
        var due = completed ? DateTime.UtcNow.AddHours(refreshHours ?? options.RefreshHours) : RetryAt(attempt);
        result = result with { Status = state };
        await using var command = dataSource.CreateCommand("""
            UPDATE "ImporterJobs" SET "State" = @state, "Stage" = @stage, "Attempts" = @attempt,
                "DueUtc" = @due, "ResultJson" = @result, "LeaseUntilUtc" = NULL, "LeaseToken" = NULL,
                "FirstCompletedUtc" = CASE WHEN @saved THEN COALESCE("FirstCompletedUtc",now()) ELSE "FirstCompletedUtc" END,
                "UpdatedUtc" = now()
            WHERE "Id" = @id AND "LeaseToken" = @token AND "State" = 'importing'
            """);
        AddJobLease(command, job);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("stage", completed ? "complete" : state);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("due", due);
        command.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(result, Json));
        command.Parameters.AddWithValue("saved", result.MediaId is not null);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ReleaseJobAsync(ImportJob job, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE "ImporterJobs" SET "State"='pending', "Stage"='queued', "DueUtc"=now()+interval '5 seconds',
                "LeaseUntilUtc"=NULL, "LeaseToken"=NULL, "UpdatedUtc"=now()
            WHERE "Id"=@id AND "LeaseToken"=@token AND "State"='importing'
            """);
        AddJobLease(command, job);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetStageAsync(ImportJob job, string stage, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE "ImporterJobs" SET "Stage"=@stage, "UpdatedUtc"=now()
            WHERE "Id"=@id AND "LeaseToken"=@token AND "State"='importing'
            """);
        AddJobLease(command, job);
        command.Parameters.AddWithValue("stage", stage);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new OperationCanceledException("The importer job lease is no longer current.");
    }

    public async Task<IReadOnlyList<ImportJob>> ListJobsAsync(string? provider, string? state, int limit, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT {JobColumns} FROM "ImporterJobs"
            WHERE (@provider IS NULL OR "Provider"=@provider) AND (@state IS NULL OR "State"=@state)
            ORDER BY "UpdatedUtc" DESC, "Id" LIMIT @limit
            """);
        command.Parameters.AddWithValue("provider", NpgsqlDbType.Text, (object?)provider ?? DBNull.Value);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Text, (object?)state ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<ImportJob>();
        while (await reader.ReadAsync(ct)) items.Add(ReadJob(reader));
        return items;
    }

    public async Task<ImportJob?> GetJobAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"SELECT {JobColumns} FROM \"ImporterJobs\" WHERE \"Id\"=@id");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJob(reader) : null;
    }

    public async Task<JobPage> ListJobsPageAsync(string? provider, string? state, int limit, string? cursor, CancellationToken ct)
    {
        var size = Math.Clamp(limit, 1, 100);
        var before = ParseCursor(cursor, provider, state);
        await using var command = dataSource.CreateCommand($"""
            SELECT {JobColumns} FROM "ImporterJobs"
            WHERE (@provider IS NULL OR "Provider"=@provider) AND (@state IS NULL OR "State"=@state)
                AND (NOT @hasCursor OR ("CreatedUtc","Id") < (@before,@id))
            ORDER BY "CreatedUtc" DESC,"Id" DESC LIMIT @limit
            """);
        command.Parameters.AddWithValue("provider", NpgsqlDbType.Text, (object?)provider ?? DBNull.Value);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Text, (object?)state ?? DBNull.Value);
        command.Parameters.AddWithValue("hasCursor", before is not null);
        command.Parameters.AddWithValue("before", before?.CreatedUtc ?? DateTime.UnixEpoch);
        command.Parameters.AddWithValue("id", before?.Id ?? Guid.Empty);
        command.Parameters.AddWithValue("limit", size + 1);
        var items = new List<ImportJob>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) items.Add(ReadJob(reader));
        var more = items.Count > size;
        if (more) items.RemoveAt(size);
        var last = items.LastOrDefault();
        return new(items.ToArray(), more && last is not null ? EncodeCursor(new(last.CreatedUtc, last.Id, provider, state)) : null);
    }

    private sealed record JobCursor(DateTime CreatedUtc, Guid Id, string? Provider, string? State);

    private static string EncodeCursor(JobCursor value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JobCursor? ParseCursor(string? value, string? provider, string? state)
    {
        if (value is null) return null;
        try
        {
            if (value.Length is < 1 or > 2048) throw new FormatException();
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var result = JsonSerializer.Deserialize<JobCursor>(Convert.FromBase64String(encoded), Json);
            if (result is null || result.Id == Guid.Empty || result.CreatedUtc.Kind != DateTimeKind.Utc
                || result.Provider != provider || result.State != state) throw new FormatException();
            return result;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        { throw new ArgumentException("Invalid jobs cursor or filters differ from the first page.", nameof(value)); }
    }

    public async Task<bool> CancelJobAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE "ImporterJobs" SET "State"=CASE WHEN "State"='completed' THEN 'completed' ELSE 'cancelled' END,
                "Stage"=CASE WHEN "State"='completed' THEN "Stage" ELSE 'cancelled' END, "RefreshEnabled"=false,
                "LeaseUntilUtc"=NULL,"LeaseToken"=NULL,"UpdatedUtc"=now()
            WHERE "Id"=@id AND "State" IN ('pending','retry','partial','importing','completed','cancelled')
            """);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> SetRefreshEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE "ImporterJobs" SET "RefreshEnabled"=@enabled,
                "DueUtc"=CASE WHEN @enabled AND "State"='completed' THEN LEAST("DueUtc",now()) ELSE "DueUtc" END,
                "UpdatedUtc"=now() WHERE "Id"=@id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("enabled", enabled);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<QueueSummary>> SummaryAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT "Provider", "State", count(*) FROM "ImporterJobs" GROUP BY 1,2 ORDER BY 1,2
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<QueueSummary>();
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return items;
    }

    public async Task<DiscoveryRun> StartDiscoveryAsync(AdapterSnapshot adapter, int? maximumItems, bool restart, CancellationToken ct)
    {
        if (maximumItems is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        await using var command = dataSource.CreateCommand($"""
            INSERT INTO "ImporterDiscoveryRuns" ("Id","Provider","AdapterVersion","AdapterJson","MaximumItems","State","DueUtc")
            VALUES (@id,@provider,@version,@json,@maximum,'pending',now())
            ON CONFLICT ("Provider") DO UPDATE SET
                "AdapterVersion"=CASE WHEN @restart THEN EXCLUDED."AdapterVersion" ELSE "ImporterDiscoveryRuns"."AdapterVersion" END,
                "AdapterJson"=CASE WHEN @restart THEN EXCLUDED."AdapterJson" ELSE "ImporterDiscoveryRuns"."AdapterJson" END,
                "Cursor"=CASE WHEN @restart THEN NULL ELSE "ImporterDiscoveryRuns"."Cursor" END,
                "Discovered"=CASE WHEN @restart THEN 0 ELSE "ImporterDiscoveryRuns"."Discovered" END,
                "Pages"=CASE WHEN @restart THEN 0 ELSE "ImporterDiscoveryRuns"."Pages" END,
                "PendingItems"=CASE WHEN @restart THEN '[]'::jsonb ELSE "ImporterDiscoveryRuns"."PendingItems" END,
                "PendingCursor"=CASE WHEN @restart THEN NULL ELSE "ImporterDiscoveryRuns"."PendingCursor" END,
                "MaximumItems"=EXCLUDED."MaximumItems",
                "State"=CASE WHEN NOT @restart AND "ImporterDiscoveryRuns"."State"='completed'
                    AND "ImporterDiscoveryRuns"."Cursor" IS NULL
                    AND jsonb_array_length("ImporterDiscoveryRuns"."PendingItems")=0
                    THEN 'completed' ELSE 'pending' END,
                "Attempts"=0,"DueUtc"=now(),"Error"=NULL,"UpdatedUtc"=now()
            WHERE "ImporterDiscoveryRuns"."LeaseUntilUtc" IS NULL OR "ImporterDiscoveryRuns"."LeaseUntilUtc" < now()
            RETURNING {DiscoveryColumns}
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        AddSnapshot(command, adapter);
        command.Parameters.AddWithValue("maximum", NpgsqlDbType.Integer, (object?)maximumItems ?? DBNull.Value);
        command.Parameters.AddWithValue("restart", restart);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Discovery is already running.");
        return ReadDiscovery(reader);
    }

    public async Task<DiscoveryRun?> ClaimDiscoveryAsync(string[] providers, CancellationToken ct)
    {
        if (providers.Length == 0) return null;
        await using var command = dataSource.CreateCommand($"""
            WITH next AS (
                SELECT "Id" FROM "ImporterDiscoveryRuns"
                WHERE "Provider"=ANY(@providers) AND "State" IN ('pending','retry','discovering')
                    AND "Attempts" < @maximum AND "DueUtc"<=now()
                    AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc"<now())
                ORDER BY "DueUtc" FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE "ImporterDiscoveryRuns" SET "State"='discovering', "LeaseToken"=@token,
                "LeaseUntilUtc"=now()+@lease*interval '1 second',"UpdatedUtc"=now()
            WHERE "Id" IN (SELECT "Id" FROM next) RETURNING {DiscoveryColumns}
            """);
        command.Parameters.AddWithValue("providers", providers);
        command.Parameters.AddWithValue("maximum", options.MaximumAttempts);
        command.Parameters.AddWithValue("token", Guid.NewGuid());
        command.Parameters.AddWithValue("lease", options.LeaseSeconds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadDiscovery(reader) : null;
    }

    public async Task CommitDiscoveryPageAsync(DiscoveryRun run, AdapterSnapshot snapshot, DiscoveryPage page, CancellationToken ct)
    {
        var remaining = run.MaximumItems is { } maximum ? Math.Max(0, maximum - run.Discovered) : page.Items.Length;
        var entries = page.Items.Take(remaining).ToArray();
        var pending = page.Items.Skip(entries.Length).ToArray();
        var complete = page.NextCursor is null && pending.Length == 0
            || run.MaximumItems is { } max && run.Discovered + entries.Length >= max;
        if (!complete && (page.Items.Length == 0 || page.NextCursor == run.Cursor))
            throw new InvalidDataException("Discovery cursor made no progress.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var guard = new NpgsqlCommand("""
            SELECT 1 FROM "ImporterDiscoveryRuns" WHERE "Id"=@id AND "LeaseToken"=@token
                AND "State"='discovering' FOR UPDATE
            """, connection, transaction);
        guard.Parameters.AddWithValue("id", run.Id);
        guard.Parameters.AddWithValue("token", run.LeaseToken!.Value);
        if (await guard.ExecuteScalarAsync(ct) is null) throw new OperationCanceledException("Discovery lease was superseded.");
        await EnqueueAsync(snapshot, entries, false, ct, connection, transaction);
        await using var update = new NpgsqlCommand("""
            UPDATE "ImporterDiscoveryRuns" SET "Cursor"=@cursor,"Discovered"="Discovered"+@count,
                "Pages"="Pages"+@pageCount,"State"=@state,"Attempts"=0,"Error"=NULL,"DueUtc"=now(),
                "PendingItems"=@pending,"PendingCursor"=@pendingCursor,
                "LeaseToken"=NULL,"LeaseUntilUtc"=NULL,"UpdatedUtc"=now()
            WHERE "Id"=@id AND "LeaseToken"=@token
            """, connection, transaction);
        update.Parameters.AddWithValue("cursor", NpgsqlDbType.Text, (object?)(pending.Length == 0 ? page.NextCursor : run.Cursor) ?? DBNull.Value);
        update.Parameters.AddWithValue("pending", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(pending, Json));
        update.Parameters.AddWithValue("pendingCursor", NpgsqlDbType.Text, pending.Length == 0 ? DBNull.Value : (object?)page.NextCursor ?? DBNull.Value);
        update.Parameters.AddWithValue("count", entries.Length);
        update.Parameters.AddWithValue("pageCount", run.PendingItems is { Length: > 0 } ? 0 : 1);
        update.Parameters.AddWithValue("state", complete ? "completed" : "pending");
        update.Parameters.AddWithValue("id", run.Id);
        update.Parameters.AddWithValue("token", run.LeaseToken.Value);
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> RenewDiscoveryAsync(DiscoveryRun run, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE "ImporterDiscoveryRuns" SET "LeaseUntilUtc"=now()+@lease*interval '1 second',"UpdatedUtc"=now()
            WHERE "Id"=@id AND "LeaseToken"=@token AND "State"='discovering'
            """);
        command.Parameters.AddWithValue("lease", options.LeaseSeconds);
        command.Parameters.AddWithValue("id", run.Id);
        command.Parameters.AddWithValue("token", run.LeaseToken!.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task ReleaseDiscoveryAsync(DiscoveryRun run, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE "ImporterDiscoveryRuns" SET "State"='pending',"LeaseUntilUtc"=NULL,"LeaseToken"=NULL,"UpdatedUtc"=now()
            WHERE "Id"=@id AND "LeaseToken"=@token AND "State"='discovering'
            """);
        command.Parameters.AddWithValue("id", run.Id);
        command.Parameters.AddWithValue("token", run.LeaseToken!.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task FailDiscoveryAsync(DiscoveryRun run, Exception error, CancellationToken ct)
    {
        var attempts = run.Attempts + 1;
        var failure = ImportErrors.Classify(error);
        await using var command = dataSource.CreateCommand("""
            UPDATE "ImporterDiscoveryRuns" SET "State"=@state,"Attempts"=@attempts,"Error"=@error,
                "DueUtc"=@due,"LeaseToken"=NULL,"LeaseUntilUtc"=NULL,"UpdatedUtc"=now()
            WHERE "Id"=@id AND "LeaseToken"=@token
            """);
        command.Parameters.AddWithValue("state", !failure.Retryable || attempts >= options.MaximumAttempts ? "failed" : "retry");
        command.Parameters.AddWithValue("attempts", attempts);
        command.Parameters.AddWithValue("error", failure.Code);
        command.Parameters.AddWithValue("due", RetryAt(attempts));
        command.Parameters.AddWithValue("id", run.Id);
        command.Parameters.AddWithValue("token", run.LeaseToken!.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DiscoveryRun>> ListDiscoveryAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"SELECT {DiscoveryColumns} FROM \"ImporterDiscoveryRuns\" ORDER BY \"Provider\"");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<DiscoveryRun>();
        while (await reader.ReadAsync(ct)) items.Add(ReadDiscovery(reader));
        return items;
    }

    public static DateTime RetryAt(int attempts) => DateTime.UtcNow.AddSeconds(Math.Min(86400, 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 15))));
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void AddSnapshot(NpgsqlCommand command, AdapterSnapshot snapshot)
    {
        command.Parameters.AddWithValue("provider", snapshot.Provider);
        command.Parameters.AddWithValue("version", snapshot.Version);
        command.Parameters.AddWithValue("json", NpgsqlDbType.Text, snapshot.Json);
    }

    private static void AddJobLease(NpgsqlCommand command, ImportJob job)
    {
        command.Parameters.AddWithValue("id", job.Id);
        command.Parameters.AddWithValue("token", job.LeaseToken!.Value);
    }

    private static ImportJob ReadJob(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetInt64(2),
        r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetDateTime(8),
        Nullable<DateTime>(r, 9), Nullable<Guid>(r, 10), r.IsDBNull(11) ? null : r.GetString(11),
        r.GetDateTime(12), r.GetDateTime(13), Nullable<DateTime>(r, 14)) { RefreshEnabled = r.GetBoolean(15) };

    private static DiscoveryRun ReadDiscovery(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2),
        r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), Nullable<int>(r, 5), r.GetInt32(6),
        r.GetInt32(7), r.GetString(8), r.GetInt32(9), r.GetDateTime(10), Nullable<DateTime>(r, 11),
        Nullable<Guid>(r, 12), r.IsDBNull(13) ? null : r.GetString(13), r.GetDateTime(14),
        JsonSerializer.Deserialize<DiscoveryEntry[]>(r.GetString(15), Json), r.IsDBNull(16) ? null : r.GetString(16));

    private static T? Nullable<T>(NpgsqlDataReader reader, int ordinal) where T : struct
        => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
}
