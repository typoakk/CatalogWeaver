using System.Text.Json;
using System.Text.Json.Nodes;

namespace MediaImporter.Monitoring;

public static class MonitoringEndpoints
{
    public static void AddMonitoring(this IServiceCollection services)
    {
        services.AddSingleton<MonitoringAccess>();
        services.AddSingleton<MonitoringTelemetry>();
        services.AddSingleton<MonitoringStore>();
        services.AddSingleton<MonitoringCollector>();
        services.AddHostedService(s => s.GetRequiredService<MonitoringCollector>());
    }
    public static void MapMonitoring(this WebApplication app)
    {
        var store = app.Services.GetRequiredService<MonitoringStore>();
        var access = app.Services.GetRequiredService<MonitoringAccess>();
        var collector = app.Services.GetRequiredService<MonitoringCollector>();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-CatalogWeaver-Role"] = "monitoring-server";
            if (context.Request.Path.StartsWithSegments("/api/v1/monitor"))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (!access.CanRead(context)) { context.Response.StatusCode = 401; return; }
            }
            await next(context);
        });
        object ServerInfo() => new { service = "CatalogWeaver", role = "monitoring-server", monitoringApi = "/api/v1/monitor", message = "Open the standalone web client on your PC to connect this server." };
        app.MapGet("/", () => Results.Ok(ServerInfo()));
        app.MapGet("/monitor/", () => Results.Ok(ServerInfo()));
        app.MapGet("/api/v1/importer/monitoring-key", (HttpContext context) => access.CanManage(context)
            ? Results.Ok(new { key = access.ReadKey }) : Results.Unauthorized());
        app.MapGet("/api/v1/monitor/snapshot", async (CancellationToken ct) =>
        {
            var snapshot = await store.LatestAsync(ct) ?? await store.CollectAsync(ct);
            snapshot["collectorError"] = collector.Error;
            return Results.Json(snapshot);
        });
        async Task<JsonNode> History(int hours, CancellationToken ct)
        {
            if (hours is < 1 or > 720) throw new ArgumentException("Invalid history window.");
            var bucket = Math.Max(60, hours * 3600 / 500);
            // Bound transfer size; gaps remain gaps and actual measured values are retained.
            return await store.QueryAsync($"""
                SELECT coalesce(jsonb_agg("Data" ORDER BY "Utc"),'[]')::text FROM (
                    SELECT DISTINCT ON (floor(extract(epoch FROM "Utc")/{bucket})) "Utc","Data" FROM "MonitorSnapshots"
                    WHERE "Utc">now()-make_interval(hours=>{hours}) ORDER BY floor(extract(epoch FROM "Utc")/{bucket}),"Utc" DESC
                ) t
                """, ct);
        }
        async Task<JsonNode> JobList(string? provider, string? state, string? cursor, CancellationToken ct)
        {
            var page = await app.Services.GetRequiredService<ImportJobStore>().ListJobsPageAsync(string.IsNullOrEmpty(provider)?null:provider,
                string.IsNullOrEmpty(state)?null:state, 100, string.IsNullOrEmpty(cursor)?null:cursor, ct);
            return JsonSerializer.SerializeToNode(new { items = page.Items.Select(ViewJob), page.NextCursor }, MonitoringStore.Json)!;
        }
        async Task<JsonNode> Job(Guid id, CancellationToken ct)
        {
            var job = await app.Services.GetRequiredService<ImportJobStore>().GetJobAsync(id, ct) ?? throw new KeyNotFoundException();
            return JsonSerializer.SerializeToNode(ViewJob(job), MonitoringStore.Json)!;
        }
        app.MapGet("/api/v1/monitor/history", async (int? hours, CancellationToken ct) => Results.Json(await History(hours ?? 1, ct)));
        app.MapGet("/api/v1/monitor/jobs", async (string? provider, string? state, string? cursor, CancellationToken ct) => Results.Json(await JobList(provider, state, cursor, ct)));
        app.MapGet("/api/v1/monitor/jobs/{id:guid}", async (Guid id, CancellationToken ct) => Results.Json(await Job(id, ct)));
    }
    private static ImportJobResponse ViewJob(ImportJob job) => new(job.Id, job.Provider, job.ExternalId, job.AdapterVersion,
        job.State, job.Stage, job.Attempts, job.DueUtc, job.LeaseUntilUtc, job.CreatedUtc, job.UpdatedUtc, job.FirstCompletedUtc,
        job.RefreshEnabled, job.ResultJson is null ? null : JsonSerializer.Deserialize<ImportResult>(job.ResultJson, MonitoringStore.Json));
}
