using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using MediaImporter.Images;

namespace MediaImporter.Monitoring;

public sealed class MonitoringStore(NpgsqlDataSource source, AdapterRegistry adapters, EncoderRegistry encoders,
    ImportJobStore jobs, ExecutionDiagnostics diagnostics, ImporterOptions options,
    MonitoringTelemetry telemetry, IConfiguration configuration)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Stopwatch uptime = Stopwatch.StartNew();
    private TimeSpan previousCpu;
    private double previousCpuTime;
    private readonly object resourceGate = new();

    public async Task<JsonNode> QueryAsync(string sql, CancellationToken ct)
    {
        await using var command = source.CreateCommand(sql);
        return JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!)!;
    }
    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT to_regclass('\"MonitorCurrent\"') IS NOT NULL");
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }
    public async Task SaveHttpAsync(List<HttpMeasurement> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;
        await using var command = source.CreateCommand("""
            WITH inserted AS (
                INSERT INTO "MonitorHttp" SELECT * FROM jsonb_to_recordset(@data) AS x(
                    "Id" uuid, "Utc" timestamptz, "Provider" text, "Category" text, "Host" text,
                    "Status" integer, "Error" text, "Received" bigint, "Sent" bigint, "DurationMs" double precision)
                ON CONFLICT DO NOTHING RETURNING *
            )
            INSERT INTO "MonitorHttpTotals"
                SELECT "Provider", "Category", count(*), count(*) FILTER (WHERE "Error" IS NOT NULL OR "Status">=400),
                    sum("Received"), sum("Sent") FROM inserted GROUP BY 1,2
            ON CONFLICT ("Provider", "Category") DO UPDATE SET
                "Requests"="MonitorHttpTotals"."Requests"+EXCLUDED."Requests",
                "Errors"="MonitorHttpTotals"."Errors"+EXCLUDED."Errors",
                "Received"="MonitorHttpTotals"."Received"+EXCLUDED."Received",
                "Sent"="MonitorHttpTotals"."Sent"+EXCLUDED."Sent"
            """);
        command.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(entries));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<JsonObject> CollectAsync(CancellationToken ct)
    {
        var identity = await QueryAsync("SELECT jsonb_build_object('serverId',\"ServerId\",'sinceUtc',\"SinceUtc\")::text FROM \"MonitorIdentity\"", ct);
        var now = DateTime.UtcNow;
        var since = identity["sinceUtc"]!.GetValue<DateTime>().ToUniversalTime();
        var queue = await jobs.SummaryAsync(ct);
        var rates = await QueryAsync("""
            SELECT coalesce(jsonb_agg(to_jsonb(t)),'[]')::text FROM (
                SELECT "Provider" AS provider, "Kind" AS kind, w.minutes,
                    count(*) FILTER (WHERE "Utc">now()-make_interval(mins=>w.minutes)) AS count
                FROM "MonitorCompletions" CROSS JOIN (VALUES (1),(5),(15)) AS w(minutes)
                WHERE "Utc">now()-interval '15 minutes' GROUP BY 1,2,3
            ) t
            """, ct);
        var itemCounts = await QueryAsync("""
            SELECT coalesce(jsonb_agg(to_jsonb(t)),'[]')::text FROM (
                SELECT "Provider" AS provider, count(*) AS cards,
                    count(*) FILTER (WHERE EXISTS (SELECT 1 FROM jsonb_each_text(coalesce("MetadataJson"->'fieldStatus','{}')) f WHERE f.value='retry')) AS "incompleteFields",
                    count(*) FILTER (WHERE EXISTS (SELECT 1 FROM jsonb_each_text("AssetStatusJson") f WHERE f.value='retry')) AS "assetRetryCards",
                    max("UpdatedUtc") AS "lastSavedUtc" FROM "ImporterItems" GROUP BY 1
            ) t
            """, ct);
        var assets = await QueryAsync("""
            SELECT coalesce(jsonb_agg(to_jsonb(t)),'[]')::text FROM (
                SELECT "Provider" AS provider, "AssetJson"->>'state' AS state, "AssetJson"->>'role' AS role,
                    "AssetJson"->>'requestedProfile' AS "requestedProfile", "AssetJson"->>'actualProfile' AS "actualProfile", count(*) AS count
                FROM "ImporterAssets" GROUP BY 1,2,3,4,5
            ) t
            """, ct);
        var files = await QueryAsync("""
            SELECT coalesce(jsonb_agg(to_jsonb(t)),'[]')::text FROM (
                SELECT a."Provider" AS provider, count(*) AS files, coalesce(sum(f."Bytes"),0) AS bytes
                FROM (SELECT DISTINCT "Provider", "Hash", "RelativePath" FROM "ImporterAssets" WHERE "Hash" IS NOT NULL) a
                JOIN "ImporterFiles" f USING ("Hash","RelativePath") GROUP BY 1
            ) t
            """, ct);
        var waiting = await QueryAsync("""
            SELECT coalesce(jsonb_agg(to_jsonb(t)),'[]')::text FROM (
                SELECT "Provider" AS provider, min("CreatedUtc") FILTER (WHERE "FirstCompletedUtc" IS NULL AND "State" IN ('pending','retry','partial')) AS "oldestQueuedUtc",
                    count(*) FILTER (WHERE "DueUtc"<=now() AND "RefreshEnabled" AND "State" IN ('pending','retry','partial','completed')) AS "dueNow",
                    count(*) FILTER (WHERE "State"='completed' AND "RefreshEnabled" AND "DueUtc"<=now()) AS "refreshDue"
                    ,min("DueUtc") FILTER (WHERE "DueUtc"<=now() AND "RefreshEnabled" AND "State" IN ('pending','retry','partial','completed')) AS "eligibleSinceUtc"
                FROM "ImporterJobs" GROUP BY 1
            ) t
            """, ct);
        var gates = await QueryAsync("""
            SELECT coalesce(jsonb_agg(to_jsonb(t)),'[]')::text FROM (
                SELECT "Provider" AS provider, "BlockedUntilUtc" AS "blockedUntilUtc", "LastStartedUtc" AS "lastStartedUtc",
                    (SELECT count(*) FROM jsonb_array_elements_text("Starts") s WHERE s::double precision>extract(epoch FROM now())-60) AS "reservedMinute"
                FROM "ImporterRateStates"
            ) t
            """, ct);
        var totals = await QueryAsync("""
            SELECT coalesce(jsonb_agg(to_jsonb(t)),'[]')::text FROM (
                SELECT "Provider" AS provider,"Category" AS category,"Requests" AS requests,"Errors" AS errors,
                    "Received" AS received,"Sent" AS sent FROM "MonitorHttpTotals"
            ) t
            """, ct);
        var http = await QueryAsync("""
            SELECT coalesce(jsonb_agg(to_jsonb(t)),'[]')::text FROM (
                SELECT "Provider" AS provider, count(*) AS requests,
                    count(*) FILTER (WHERE "Error" IS NOT NULL OR "Status">=400) AS errors,
                    percentile_cont(0.5) WITHIN GROUP (ORDER BY "DurationMs") AS "p50Ms",
                    percentile_cont(0.95) WITHIN GROUP (ORDER BY "DurationMs") AS "p95Ms",
                    sum("Received") AS received, sum("Sent") AS sent FROM "MonitorHttp"
                WHERE "Utc">now()-interval '15 minutes' GROUP BY 1
            ) t
            """, ct);
        var problems = await QueryAsync("""
            SELECT coalesce(jsonb_agg(to_jsonb(t)),'[]')::text FROM (
                SELECT "Utc" AS utc,"Provider" AS provider,"Category" AS category,"Host" AS host,"Status" AS status,"Error" AS error
                FROM "MonitorHttp" WHERE "Error" IS NOT NULL OR "Status">=400 ORDER BY "Utc" DESC LIMIT 50
            ) t
            """, ct);
        var registrations = adapters.List();
        var providerNames = registrations.Select(x => x.Provider).Union(queue.Select(x => x.Provider)).Distinct();
        JsonArray Matching(JsonNode items, string provider) => new(items.AsArray().Where(x => x?["provider"]?.GetValue<string>() == provider).Select(x => x!.DeepClone()).ToArray());
        var summaries = new JsonArray();
        foreach (var provider in providerNames)
        {
            var registration = registrations.FirstOrDefault(x => x.Provider == provider);
            AdapterSnapshot? adapter = null;
            try { adapter = adapters.Get(provider); } catch (KeyNotFoundException) { }
            var windows = new JsonArray();
            foreach (var minutes in new[] { 1, 5, 15 })
            {
                var seconds = Math.Min(minutes * 60, Math.Max(1, (now - since).TotalSeconds));
                long Count(string kind) => rates.AsArray().Where(x => x!["provider"]!.GetValue<string>() == provider
                    && x["kind"]!.GetValue<string>() == kind && x["minutes"]!.GetValue<int>() == minutes).Sum(x => x!["count"]!.GetValue<long>());
                windows.Add(JsonSerializer.SerializeToNode(new { minutes, observedSeconds = seconds, fullWindow = seconds >= minutes * 60,
                    imports = Count("import"), refreshes = Count("refresh"), importsPerMinute = Count("import") * 60 / seconds,
                    refreshesPerMinute = Count("refresh") * 60 / seconds }, Json));
            }
            var states = queue.Where(x => x.Provider == provider).ToDictionary(x => x.State, x => x.Count);
            summaries.Add(JsonSerializer.SerializeToNode(new { provider, name = registration?.DisplayName ?? provider,
                enabled = registration?.Enabled ?? false, version = registration?.Version, errors = registration?.Errors ?? ["adapter_missing"],
                checkedUtc = registration?.CheckedUtc, kind = adapter?.Definition.Kind, refreshHours = adapter?.Definition.RefreshHours,
                rateLimit = adapter?.Definition.RateLimit, destination = adapter?.Definition.Destinations.Database?.Connection,
                states, rates = windows, items = Matching(itemCounts, provider), assets = Matching(assets, provider), files = Matching(files, provider),
                waiting = Matching(waiting, provider), limiter = Matching(gates, provider), traffic = Matching(totals, provider), http = Matching(http, provider) }, Json));
        }
        var storage = await QueryAsync("""
            SELECT jsonb_build_object('databaseBytes',pg_database_size(current_database()),
                'files',(SELECT count(*) FROM "ImporterFiles"),'mediaBytes',(SELECT coalesce(sum("Bytes"),0) FROM "ImporterFiles"),
                'publications',(SELECT count(*) FROM "ImporterPublications"),
                'exportReservations',(SELECT count(*) FROM "ImporterExportedFiles"))::text
            """, ct);
        return new JsonObject
        {
            ["schemaVersion"] = 1, ["serverId"] = identity["serverId"]!.DeepClone(),
            ["name"] = configuration["Monitoring:Name"] ?? "CatalogWeaver", ["sinceUtc"] = identity["sinceUtc"]!.DeepClone(),
            ["updatedUtc"] = now, ["version"] = typeof(MonitoringStore).Assembly.GetName().Version?.ToString(),
            ["workersEnabled"] = options.WorkersEnabled, ["healthy"] = diagnostics.IsHealthy(), ["adapters"] = summaries,
            ["loops"] = JsonSerializer.SerializeToNode(diagnostics.Snapshot(), Json),
            ["discovery"] = JsonSerializer.SerializeToNode((await jobs.ListDiscoveryAsync(ct)).Select(x => new {
                x.Id,x.Provider,x.State,x.Pages,x.Discovered,x.MaximumItems,x.Attempts,x.DueUtc,x.UpdatedUtc,x.Error, pendingItems = x.PendingItems?.Length ?? 0 }), Json),
            ["encoders"] = JsonSerializer.SerializeToNode(encoders.List(), Json), ["storage"] = storage,
            ["resources"] = Resources(), ["httpErrors"] = problems, ["activeRequests"] = JsonSerializer.SerializeToNode(telemetry.Active, Json),
            ["droppedTelemetry"] = telemetry.Dropped
        };
    }
    private JsonNode Resources()
    {
        using var process = Process.GetCurrentProcess();
        double cpu;
        lock (resourceGate)
        {
            var elapsed = uptime.Elapsed.TotalMilliseconds;
            cpu = previousCpuTime > 0 ? Math.Max(0, (process.TotalProcessorTime - previousCpu).TotalMilliseconds / Math.Max(1, elapsed - previousCpuTime) * 100) : 0;
            previousCpu = process.TotalProcessorTime; previousCpuTime = elapsed;
        }
        long? free = null, total = null;
        try { var drive = new DriveInfo(Path.GetPathRoot(options.MediaRoot)!); free = drive.AvailableFreeSpace; total = drive.TotalSize; } catch (IOException) { }
        // On Linux the root of the mounted media directory may be a different filesystem.
        if (OperatingSystem.IsLinux())
        {
            var drive = DriveInfo.GetDrives().Where(d => options.MediaRoot.StartsWith(d.Name, StringComparison.Ordinal))
                .OrderByDescending(d => d.Name.Length).FirstOrDefault();
            try { if (drive is not null) { free = drive.AvailableFreeSpace; total = drive.TotalSize; } } catch (IOException) { }
        }
        return JsonSerializer.SerializeToNode(new { cpuPercent = cpu, memoryBytes = process.WorkingSet64,
            uptimeSeconds = uptime.Elapsed.TotalSeconds, diskFreeBytes = free, diskTotalBytes = total }, Json)!;
    }
    public async Task<JsonObject?> LatestAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT \"Data\"::text FROM \"MonitorCurrent\" WHERE \"Singleton\"");
        return await command.ExecuteScalarAsync(ct) is string data ? JsonNode.Parse(data)!.AsObject() : null;
    }
    public async Task SaveSnapshotAsync(JsonObject data, CancellationToken ct)
    {
        var history = new JsonObject { ["updatedUtc"] = data["updatedUtc"]!.DeepClone(),
            ["adapters"] = new JsonArray(data["adapters"]!.AsArray().Select(a => (JsonNode)new JsonObject {
                ["provider"] = a!["provider"]!.DeepClone(), ["rates"] = a["rates"]!.DeepClone(), ["states"] = a["states"]!.DeepClone()
            }).ToArray()) };
        await using var command = source.CreateCommand("""
            WITH current AS (INSERT INTO "MonitorCurrent" VALUES (true,@data)
                ON CONFLICT ("Singleton") DO UPDATE SET "Data"=EXCLUDED."Data")
            INSERT INTO "MonitorSnapshots" VALUES (date_trunc('minute',@utc::timestamptz),@history) ON CONFLICT DO NOTHING
            """);
        command.Parameters.AddWithValue("utc", data["updatedUtc"]!.GetValue<DateTime>());
        command.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, data.ToJsonString());
        command.Parameters.AddWithValue("history", NpgsqlDbType.Jsonb, history.ToJsonString());
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task CleanupAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            DELETE FROM "MonitorSnapshots" WHERE "Utc"<now()-interval '30 days';
            DELETE FROM "MonitorCompletions" WHERE "Utc"<now()-interval '30 days';
            DELETE FROM "MonitorHttp" WHERE "Utc"<now()-interval '7 days';
            """);
        await command.ExecuteNonQueryAsync(ct);
    }
}
