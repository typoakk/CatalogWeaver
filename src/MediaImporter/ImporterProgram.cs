using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaImporter.Images;
using Npgsql;
using MediaImporter.Database;

namespace MediaImporter;

public sealed class ImporterProgram
{
    public static async Task Main(string[] args)
    {
        if (args.Any(value => value is "--help" or "-h"))
        {
            Console.WriteLine("""
                CatalogWeaver — standalone media import service

                CatalogWeaver [--urls http://127.0.0.1:8770]  Start the management API and workers
                CatalogWeaver --migrate                      Initialize the tool's own database
                CatalogWeaver --bootstrap                    Initialize persistent service secrets (no database required)
                CatalogWeaver --inspect-db NAME              Print a named PostgreSQL connection's schema as JSON

                Configuration (environment variables or appsettings.json):
                  ConnectionStrings__ImporterDb   Private service-state PostgreSQL connection
                  ConnectionStrings__NAME         Optional existing target database connection
                  Importer__ApiKey                Management key (at least 32 characters)
                  Importer__ApiKeyFile            Optional file containing the management key
                  Importer__DatabasePasswordFile  Optional password file for the service-state connection
                  Importer__ConfigurationPath     Persistent settings directory used by --bootstrap
                  Importer__AdaptersPath          Adapter package directory
                  Importer__EncodersPath          Hot-loaded image encoder module directory
                  Importer__MediaRoot             Downloaded media root

                See README.md and docs/database-mapping.md. No graphical interface is included.
                """);
            return;
        }
        var migrate = args.Contains("--migrate", StringComparer.Ordinal);
        var bootstrap = args.Contains("--bootstrap", StringComparer.Ordinal);
        var inspectIndex = Array.IndexOf(args, "--inspect-db");
        var inspectName = inspectIndex < 0 ? null : inspectIndex + 1 < args.Length
            ? args[inspectIndex + 1] : throw new ArgumentException("Supply a configured connection name after --inspect-db.");
        var builder = WebApplication.CreateBuilder(args.Where((value, index) => value is not ("--migrate" or "--bootstrap")
            && index != inspectIndex && (inspectIndex < 0 || index != inspectIndex + 1)).ToArray());
        var configuration = builder.Configuration;
        if (bootstrap)
        {
            await ServiceBootstrap.InitializeAsync(configuration["Importer:ConfigurationPath"] ?? "/data/config",
                configuration["Bootstrap:DatabasePassword"], configuration["Bootstrap:ApiKey"]);
            Console.WriteLine("Service secrets are ready; existing values were preserved.");
            return;
        }
        if (inspectName is not null)
        {
            var schema = await new PostgresDatabaseWriter(configuration).InspectAsync(inspectName, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(schema, AdapterRegistry.Json));
            return;
        }
        ServiceBootstrap.ApplySecretFiles(configuration);
        var connection = configuration.GetConnectionString("ImporterDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:ImporterDb for the importer state database.");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:ImporterDb must not be empty.");
        var sourceAdapters = Path.Combine(builder.Environment.ContentRootPath, "adapters");
        configuration["Importer:AdaptersPath"] = Path.GetFullPath(configuration["Importer:AdaptersPath"]
            ?? (Directory.Exists(sourceAdapters) ? sourceAdapters : Path.Combine(AppContext.BaseDirectory, "adapters")), builder.Environment.ContentRootPath);
        var sourceEncoders = Path.Combine(builder.Environment.ContentRootPath, "encoders");
        configuration["Importer:EncodersPath"] = Path.GetFullPath(configuration["Importer:EncodersPath"]
            ?? (Directory.Exists(sourceEncoders) ? sourceEncoders : Path.Combine(AppContext.BaseDirectory, "encoders")), builder.Environment.ContentRootPath);
        configuration["Importer:MediaRoot"] = Path.GetFullPath(configuration["Importer:MediaRoot"]
            ?? Path.Combine("data", "media"), builder.Environment.ContentRootPath);
        var options = configuration.GetSection("Importer").Get<ImporterOptions>() ?? new();
        if (options.PersistenceMode != "Standalone")
            throw new InvalidOperationException("Application-specific persistence modes have been replaced by destinations.database JSON profiles.");
        if (!migrate) options.Validate();

        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        builder.Services.AddSingleton<PostgresDatabaseWriter>();
        builder.Services.AddSingleton<ImportJobStore>();
        builder.Services.AddSingleton<IProviderRateLimiter, PostgresProviderRateLimiter>();
        builder.Services.AddHttpClient<AdapterTransport>(client => client.Timeout = TimeSpan.FromSeconds(45))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        builder.Services.AddTransient<AdapterRuntime>();
        builder.Services.AddSingleton<EncoderRegistry>();
        builder.Services.AddHostedService(services => services.GetRequiredService<EncoderRegistry>());
        builder.Services.AddSingleton<AdapterRegistry>();
        builder.Services.AddHostedService(services => services.GetRequiredService<AdapterRegistry>());
        builder.Services.AddSingleton(configuration.GetSection("Importer").Get<ImageStorageOptions>() ?? new());
        builder.Services.AddSingleton<IImporterImageCodec, ImporterImageCodec>();
        builder.Services.AddSingleton<ImagePipeline>();
        builder.Services.AddHostedService<ImageHousekeepingService>();
        builder.Services.AddScoped<StandalonePersistence>();
        builder.Services.AddScoped<IImportPersistence>(services => services.GetRequiredService<StandalonePersistence>());
        builder.Services.AddHostedService<CatalogHousekeepingService>();
        builder.Services.AddScoped<ImportOrchestrator>();
        builder.Services.AddHostedService<ImporterWorker>();

        await using var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<ImportJobStore>();
            if (migrate)
            {
                await StandaloneSchema.InitializeAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), CancellationToken.None);
                await jobs.InitializeAsync(PostgresProviderRateLimiter.SchemaSql, CancellationToken.None);
                app.Logger.LogInformation("Importer database schema is ready.");
                return;
            }
            if (!await jobs.IsReadyAsync(CancellationToken.None)
                || !await StandaloneSchema.IsReadyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), CancellationToken.None))
                throw new InvalidOperationException("Apply importer migrations with --migrate before starting the service.");
        }
        await app.Services.GetRequiredService<EncoderRegistry>().ReloadAsync();
        await app.Services.GetRequiredService<AdapterRegistry>().ReloadAsync();
        MapEndpoints(app, options);
        await app.RunAsync();
    }

    public static void MapEndpoints(WebApplication app, ImporterOptions options)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1/importer"))
            {
                var header = context.Request.Headers.Authorization.ToString();
                var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(options.ApiKey)))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                    return;
                }
            }
            try { await next(context); }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidDataException
                or KeyNotFoundException or InvalidOperationException)
            {
                context.Response.StatusCode = error is KeyNotFoundException ? 404 : 400;
                await context.Response.WriteAsJsonAsync(new { error = error.GetType().Name });
            }
        });

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (NpgsqlDataSource source, ImportJobStore jobs, CancellationToken ct) =>
        {
            try
            {
                await using var connection = await source.OpenConnectionAsync(ct);
                var ready = await jobs.IsReadyAsync(ct) && await StandaloneSchema.IsReadyAsync(source, ct);
                return ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
            }
            catch (Exception error) when (error is NpgsqlException or IOException) { return Results.StatusCode(503); }
        });

        var api = app.MapGroup("/api/v1/importer");
        api.MapGet("/items", async (int? limit, StandalonePersistence persistence, CancellationToken ct) =>
            Results.Ok(await persistence.ListAsync(limit ?? 30, ct)));
        api.MapGet("/items/{mediaId}", async (string mediaId, StandalonePersistence persistence, CancellationToken ct) =>
        {
            var result = await persistence.ReadAsync(mediaId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        api.MapGet("/database/{connectionName}/schema", async (string connectionName, PostgresDatabaseWriter writer, CancellationToken ct) =>
            Results.Ok(await writer.InspectAsync(connectionName, ct)));
        api.MapPost("/database/validate", async (DatabaseDestination destination, PostgresDatabaseWriter writer, CancellationToken ct) =>
        {
            var errors = await writer.ValidateAsync(destination, ct);
            return Results.Ok(new { valid = errors.Length == 0, errors });
        });
        api.MapPost("/database/plan", async (DatabasePlanRequest request, PostgresDatabaseWriter writer, CancellationToken ct) =>
        {
            if (request.Destination is null || request.Item is null || request.Assets is null || request.Assets.Length > 10000)
                return Results.BadRequest(new { error = "supply_destination_item_and_assets" });
            return Results.Ok(await writer.PlanAsync(request.Destination, request.Item, request.Assets,
                request.MediaId ?? request.Item.Provider + "-" + request.Item.ExternalId, ct,
                new DatabaseExecutionContext(request.AdapterVersion, request.Item.AssetStatus, request.Item.Warnings)));
        });
        api.MapGet("/status", async (AdapterRegistry registry, EncoderRegistry encoders, ImportJobStore jobs, CancellationToken ct) =>
            Results.Ok(new { adapters = registry.List(), encoders = encoders.List(), queue = await jobs.SummaryAsync(ct),
                discovery = (await jobs.ListDiscoveryAsync(ct)).Select(DiscoveryView) }));
        api.MapGet("/encoders", (EncoderRegistry encoders) => Results.Ok(encoders.List()));
        api.MapGet("/image-profiles", (EncoderRegistry encoders) => Results.Ok(new { original = "original", profiles = encoders.Profiles() }));
        api.MapGet("/encoders/{id}", (string id, EncoderRegistry encoders) => Results.Content(encoders.GetJson(id), "application/json"));
        api.MapPost("/encoders/reload", async (EncoderRegistry encoders, AdapterRegistry registry, CancellationToken ct) =>
        {
            await encoders.ReloadAsync(ct);
            await registry.ReloadAsync(ct);
            return Results.Ok(encoders.List());
        });
        api.MapPost("/encoders/{id}/validate", (string id, JsonElement definition, EncoderRegistry encoders) =>
        {
            var errors = encoders.ValidateJson(definition.GetRawText(), id);
            return Results.Ok(new { valid = errors.Length == 0, errors });
        });
        api.MapPut("/encoders/{id}", async (string id, JsonElement definition, EncoderRegistry encoders, AdapterRegistry registry, CancellationToken ct) =>
        {
            var errors = encoders.ValidateJson(definition.GetRawText(), id);
            if (errors.Length > 0) return Results.BadRequest(new { errors });
            await encoders.SaveAsync(id, definition.GetRawText(), ct);
            await registry.ReloadAsync(ct);
            return Results.Ok(encoders.List().Single(value => value.Id == id));
        });
        api.MapGet("/adapters", (AdapterRegistry registry) => Results.Ok(registry.List()));
        api.MapPost("/adapters/reload", async (AdapterRegistry registry, CancellationToken ct) =>
        {
            await registry.ReloadAsync(ct);
            return Results.Ok(registry.List());
        });
        api.MapGet("/adapters/{provider}", (string provider, AdapterRegistry registry) =>
            Results.Content(registry.Get(provider).Json, "application/json"));
        api.MapPut("/adapters/{provider}", async (string provider, JsonElement definition, AdapterRegistry registry, CancellationToken ct) =>
        {
            var errors = registry.ValidateJson(definition.GetRawText(), provider);
            if (errors.Length != 0) return Results.BadRequest(new { errors });
            var snapshot = await registry.SaveAsync(provider, definition.GetRawText(), ct);
            return Results.Ok(new { snapshot.Provider, snapshot.Version });
        });
        api.MapPost("/adapters/{provider}/validate", (string provider, JsonElement definition, AdapterRegistry registry) =>
        {
            var errors = registry.ValidateJson(definition.GetRawText(), provider);
            return Results.Ok(new { valid = errors.Length == 0, errors });
        });
        api.MapPost("/adapters/{provider}/enabled", async (string provider, EnabledRequest request, AdapterRegistry registry, CancellationToken ct) =>
        {
            var current = registry.Get(provider);
            var json = JsonSerializer.Serialize(current.Definition with { Enabled = request.Enabled }, AdapterRegistry.Json);
            var snapshot = await registry.SaveAsync(provider, json, ct);
            return Results.Ok(new { snapshot.Provider, snapshot.Version, snapshot.Definition.Enabled });
        });
        api.MapPost("/adapters/{provider}/preview", async (string provider, PreviewRequest request,
            AdapterRegistry registry, ImportOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (request.ExternalId is < 1 or > int.MaxValue) return Results.BadRequest(new { error = "invalid_external_id" });
            var snapshot = registry.Get(provider);
            try
            {
                return Results.Ok(await orchestrator.ExecuteAsync(snapshot, request.ExternalId, true,
                    request.ProcessImages, null, ct, planDatabase: request.PlanDatabase));
            }
            catch (AdapterReadException error)
            {
                return Results.Ok(new ImportResult { Mode = "preview", Provider = provider,
                    ExternalId = request.ExternalId, AdapterVersion = snapshot.Version,
                    Status = error.Retryable ? "retry" : "failed", Error = error.Code });
            }
        });
        api.MapPost("/adapters/{provider}/discovery/preview", async (string provider, DiscoveryPreviewRequest request,
            AdapterRegistry registry, AdapterRuntime runtime, CancellationToken ct) =>
        {
            var snapshot = registry.Get(provider);
            try
            {
                return Results.Ok(new ImportResult { Mode = "preview", Provider = provider,
                    AdapterVersion = snapshot.Version, Discovery = await runtime.DiscoverAsync(snapshot, request.Cursor, ct) });
            }
            catch (AdapterReadException error)
            {
                return Results.Ok(new ImportResult { Mode = "preview", Provider = provider,
                    AdapterVersion = snapshot.Version, Status = error.Retryable ? "retry" : "failed", Error = error.Code });
            }
        });
        api.MapPost("/adapters/{provider}/jobs", async (string provider, EnqueueRequest request,
            AdapterRegistry registry, ImportJobStore jobs, CancellationToken ct) =>
        {
            var snapshot = RequireEnabled(registry, provider);
            if (request.Ids is null || request.Ids.Length is < 1 or > 1000 || request.Ids.Any(id => id is < 1 or > int.MaxValue))
                return Results.BadRequest(new { error = "supply_1_to_1000_valid_external_ids" });
            var count = await jobs.EnqueueAsync(snapshot, request.Ids.Select(id => new DiscoveryEntry(id)).ToArray(), true, ct);
            return Results.Accepted(value: new { provider, enqueued = count });
        });
        api.MapPost("/adapters/{provider}/discovery", async (string provider, DiscoveryRequest request,
            AdapterRegistry registry, ImportJobStore jobs, CancellationToken ct) =>
            Results.Accepted(value: DiscoveryView(await jobs.StartDiscoveryAsync(RequireEnabled(registry, provider),
                request.MaximumItems, request.Restart, ct))));
        api.MapGet("/jobs", async (string? provider, string? state, int? limit, ImportJobStore jobs, CancellationToken ct) =>
            Results.Ok((await jobs.ListJobsAsync(provider, state, limit ?? 30, ct)).Select(JobView)));
        api.MapGet("/jobs/{id:guid}", async (Guid id, ImportJobStore jobs, CancellationToken ct) =>
            await jobs.GetJobAsync(id, ct) is { } job ? Results.Ok(JobView(job)) : Results.NotFound());
        api.MapPost("/jobs/{id:guid}/cancel", async (Guid id, ImportJobStore jobs, CancellationToken ct) =>
            await jobs.CancelJobAsync(id, ct) ? Results.Ok(new { status = "cancelled" }) : Results.NotFound());
        api.MapPost("/jobs/{id:guid}/retry", async (Guid id, AdapterRegistry registry, ImportJobStore jobs, CancellationToken ct) =>
        {
            var job = await jobs.GetJobAsync(id, ct);
            if (job is null) return Results.NotFound();
            var count = await jobs.EnqueueAsync(RequireEnabled(registry, job.Provider), [new(job.ExternalId)], true, ct);
            return count == 1 ? Results.Accepted(value: new { job.Id, status = "pending" }) : Results.Conflict(new { error = "job_is_running" });
        });
        api.MapGet("/discovery", async (ImportJobStore jobs, CancellationToken ct) =>
            Results.Ok((await jobs.ListDiscoveryAsync(ct)).Select(DiscoveryView)));
    }

    private static AdapterSnapshot RequireEnabled(AdapterRegistry registry, string provider)
    {
        var snapshot = registry.Get(provider);
        if (!snapshot.Definition.Enabled) throw new InvalidOperationException("Enable the adapter before starting imports.");
        return snapshot;
    }

    private static object JobView(ImportJob job) => new
    {
        job.Id, job.Provider, job.ExternalId, job.AdapterVersion, job.State, job.Stage, job.Attempts,
        job.DueUtc, job.LeaseUntilUtc, job.CreatedUtc, job.UpdatedUtc, job.FirstCompletedUtc,
        result = job.ResultJson is null ? null : JsonSerializer.Deserialize<ImportResult>(job.ResultJson, ImportJobStore.Json)
    };

    private static object DiscoveryView(DiscoveryRun run) => new
    {
        run.Id, run.Provider, run.AdapterVersion, run.Cursor, run.MaximumItems, run.Discovered, run.Pages,
        run.State, run.Attempts, run.DueUtc, run.LeaseUntilUtc, run.Error, run.UpdatedUtc,
        pendingItems = run.PendingItems?.Length ?? 0
    };
}

public sealed record EnabledRequest(bool Enabled);
public sealed record PreviewRequest(long ExternalId, bool ProcessImages = false, bool PlanDatabase = false);
public sealed record DiscoveryPreviewRequest(string? Cursor = null);
public sealed record EnqueueRequest(long[] Ids);
public sealed record DiscoveryRequest(int? MaximumItems = null, bool Restart = false);
public sealed record DatabasePlanRequest(DatabaseDestination Destination, AdapterItem Item,
    AssetResult[] Assets, string? MediaId = null, string AdapterVersion = "");
