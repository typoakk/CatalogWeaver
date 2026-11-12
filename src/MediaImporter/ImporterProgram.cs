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
        if (args.Length == 1 && args[0] == "--image-worker")
        {
            Environment.ExitCode = await ProcessImageCodec.RunWorkerAsync();
            return;
        }
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
        builder.Services.AddHttpClient<AdapterTransport>(client => client.Timeout = Timeout.InfiniteTimeSpan)
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
        builder.Services.AddSingleton<IImporterImageCodec, ProcessImageCodec>();
        builder.Services.AddSingleton<PreviewExecutor>();
        builder.Services.AddSingleton<ExecutionDiagnostics>();
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
        var diagnostics = app.Services.GetService<ExecutionDiagnostics>() ?? new ExecutionDiagnostics();
        var previews = app.Services.GetService<PreviewExecutor>();
        if (previews is null)
        {
            previews = new PreviewExecutor(options);
            app.Lifetime.ApplicationStopped.Register(previews.Dispose);
        }
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
            catch (Exception error) when (!context.Response.HasStarted)
            {
                if (error is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return;
                var detail = ImportErrors.Classify(error);
                context.Response.StatusCode = error switch
                {
                    ConfigurationConflictException => 412,
                    PreconditionRequiredException => 428,
                    KeyNotFoundException => 404,
                    ArgumentException or JsonException or InvalidDataException or InvalidOperationException
                        or FormatException or OverflowException => 400,
                    NpgsqlException => detail.Retryable ? 503 : 422,
                    AdapterReadException => detail.Retryable ? 503 : 422,
                    OperationCanceledException or TimeoutException => 504,
                    _ => 500
                };
                app.Logger.LogWarning("API operation failed: {Code}; trace {TraceId}", detail.Code, context.TraceIdentifier);
                await context.Response.WriteAsJsonAsync(new { error = error is PreconditionRequiredException ? "if_match_required"
                    : error is ConfigurationConflictException ? "configuration_conflict" : detail.Code,
                    detail.Retryable, message = detail.SafeMessage, traceId = context.TraceIdentifier,
                    currentVersion = (error as ConfigurationConflictException)?.CurrentVersion });
            }
        });

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (NpgsqlDataSource source, ImportJobStore jobs, CancellationToken ct) =>
        {
            try
            {
                await using var connection = await source.OpenConnectionAsync(ct);
                var ready = diagnostics.IsHealthy() && await jobs.IsReadyAsync(ct) && await StandaloneSchema.IsReadyAsync(source, ct);
                return ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
            }
            catch (Exception error) when (error is NpgsqlException or IOException) { return Results.StatusCode(503); }
        });

        var api = app.MapGroup("/api/v1/importer");
        api.MapGet("/items", async (int? limit, string? cursor, HttpContext context, StandalonePersistence persistence, CancellationToken ct) =>
        {
            var page = await persistence.ListPageAsync(limit ?? 30, cursor, ct);
            if (page.NextCursor is not null) context.Response.Headers["X-Next-Cursor"] = page.NextCursor;
            return Results.Ok(page.Items);
        });
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
            Results.Ok(new { workersEnabled = options.WorkersEnabled, adapters = registry.List(), encoders = encoders.List(), queue = await jobs.SummaryAsync(ct),
                discovery = (await jobs.ListDiscoveryAsync(ct)).Select(DiscoveryView), loops = diagnostics.Snapshot() }));
        api.MapGet("/encoders", (EncoderRegistry encoders) => Results.Ok(encoders.List()));
        api.MapGet("/image-profiles", (EncoderRegistry encoders) => Results.Ok(new { original = "original", profiles = encoders.Profiles() }));
        api.MapGet("/templates/adapters/{id}", (string id, AdapterRegistry registry) => Results.Ok(TemplateCatalog.Adapter(id, registry)));
        api.MapGet("/templates/encoders/{id}", (string id, EncoderRegistry registry) => Results.Ok(TemplateCatalog.Encoder(id, registry)));
        api.MapGet("/encoders/{id}", (string id, HttpContext context, EncoderRegistry encoders) =>
        {
            context.Response.Headers.ETag = '"' + encoders.GetVersion(id) + '"';
            return Results.Content(encoders.GetJson(id), "application/json");
        });
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
        api.MapPut("/encoders/{id}", async (string id, JsonElement definition, HttpContext context, EncoderRegistry encoders, AdapterRegistry registry, CancellationToken ct) =>
        {
            var errors = encoders.ValidateJson(definition.GetRawText(), id);
            if (errors.Length > 0) return Results.BadRequest(new { errors });
            var current = encoders.List().SingleOrDefault(value => value.Id == id)?.Version;
            await encoders.SaveAsync(id, definition.GetRawText(), ct, ExpectedVersion(context, current));
            await registry.ReloadAsync(ct);
            context.Response.Headers.ETag = '"' + encoders.GetVersion(id) + '"';
            return Results.Ok(encoders.List().Single(value => value.Id == id));
        });
        api.MapGet("/adapters", (AdapterRegistry registry) => Results.Ok(registry.List()));
        api.MapPost("/adapters/reload", async (AdapterRegistry registry, CancellationToken ct) =>
        {
            await registry.ReloadAsync(ct);
            return Results.Ok(registry.List());
        });
        api.MapGet("/adapters/{provider}", (string provider, HttpContext context, AdapterRegistry registry) =>
        {
            var snapshot = registry.Get(provider);
            context.Response.Headers.ETag = '"' + snapshot.Version + '"';
            return Results.Content(snapshot.Json, "application/json");
        });
        api.MapPut("/adapters/{provider}", async (string provider, JsonElement definition, HttpContext context, AdapterRegistry registry, CancellationToken ct) =>
        {
            var errors = registry.ValidateJson(definition.GetRawText(), provider);
            if (errors.Length != 0) return Results.BadRequest(new { errors });
            var current = registry.TryGet(provider, out var previous) ? previous.Version : null;
            var snapshot = await registry.SaveAsync(provider, definition.GetRawText(), ct, ExpectedVersion(context, current));
            context.Response.Headers.ETag = '"' + snapshot.Version + '"';
            return Results.Ok(new { snapshot.Provider, snapshot.Version });
        });
        api.MapPost("/adapters/{provider}/validate", (string provider, JsonElement definition, AdapterRegistry registry) =>
        {
            var errors = registry.ValidateJson(definition.GetRawText(), provider);
            return Results.Ok(new { valid = errors.Length == 0, errors });
        });
        api.MapPost("/adapters/{provider}/enabled", async (string provider, EnabledRequest request, HttpContext context, AdapterRegistry registry, CancellationToken ct) =>
        {
            var current = registry.Get(provider);
            var snapshot = await registry.SetEnabledAsync(provider, request.Enabled, ct, ExpectedVersion(context, current.Version));
            context.Response.Headers.ETag = '"' + snapshot.Version + '"';
            return Results.Ok(new { snapshot.Provider, snapshot.Version, snapshot.Definition.Enabled });
        });
        api.MapPost("/adapters/{provider}/preview", async (string provider, PreviewRequest request,
            AdapterRegistry registry, ImportOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (request.ExternalId is < 1 or > int.MaxValue) return Results.BadRequest(new { error = "invalid_external_id" });
            var snapshot = registry.Get(provider);
            try
            {
                return Results.Ok(await previews.ExecuteAsync(token => orchestrator.ExecuteAsync(snapshot, request.ExternalId, true,
                    request.ProcessImages, null, token, planDatabase: request.PlanDatabase), ct));
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
                    AdapterVersion = snapshot.Version, Discovery = await previews.ExecuteAsync(token => runtime.DiscoverAsync(snapshot, request.Cursor, token), ct) });
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
        api.MapGet("/jobs", async (string? provider, string? state, int? limit, string? cursor, HttpContext context, ImportJobStore jobs, CancellationToken ct) =>
        {
            var page = await jobs.ListJobsPageAsync(provider, state, limit ?? 30, cursor, ct);
            if (page.NextCursor is not null) context.Response.Headers["X-Next-Cursor"] = page.NextCursor;
            return Results.Ok(page.Items.Select(JobView));
        });
        api.MapGet("/jobs/{id:guid}", async (Guid id, ImportJobStore jobs, CancellationToken ct) =>
            await jobs.GetJobAsync(id, ct) is { } job ? Results.Ok(JobView(job)) : Results.NotFound());
        api.MapPost("/jobs/{id:guid}/cancel", async (Guid id, ImportJobStore jobs, CancellationToken ct) =>
        {
            if (!await jobs.CancelJobAsync(id, ct)) return Results.NotFound();
            return Results.Ok(JobView((await jobs.GetJobAsync(id, ct))!));
        });
        api.MapPost("/jobs/{id:guid}/refresh", async (Guid id, EnabledRequest request, ImportJobStore jobs, CancellationToken ct) =>
            await jobs.SetRefreshEnabledAsync(id, request.Enabled, ct) ? Results.Ok(new { refreshEnabled = request.Enabled }) : Results.NotFound());
        api.MapPost("/jobs/{id:guid}/retry", async (Guid id, AdapterRegistry registry, ImportJobStore jobs, CancellationToken ct) =>
        {
            var job = await jobs.GetJobAsync(id, ct);
            if (job is null) return Results.NotFound();
            var count = await jobs.EnqueueAsync(RequireEnabled(registry, job.Provider), [new(job.ExternalId)], true, ct);
            return count == 1 ? Results.Accepted(value: new { job.Id, status = "pending" }) : Results.Conflict(new { error = "job_is_running" });
        });
        api.MapGet("/discovery", async (ImportJobStore jobs, CancellationToken ct) =>
            Results.Ok((await jobs.ListDiscoveryAsync(ct)).Select(DiscoveryView)));
        api.MapGet("/files/export-reservations", async (int? limit, string? cursor, StandalonePersistence persistence, CancellationToken ct) =>
            Results.Ok(await persistence.ListExportReservationsAsync(limit ?? 30, cursor, ct)));
        api.MapPost("/files/export-reservations/release", async (ReleaseReservationsRequest request, StandalonePersistence persistence, CancellationToken ct) =>
            Results.Ok(await persistence.ReleaseExportReservationsAsync(request.Files, request.Confirmation, ct)));
        ApiContract.Map(api, app);
    }

    private static AdapterSnapshot RequireEnabled(AdapterRegistry registry, string provider)
    {
        var snapshot = registry.Get(provider);
        if (!snapshot.Definition.Enabled) throw new InvalidOperationException("Enable the adapter before starting imports.");
        return snapshot;
    }

    private static ImportJobResponse JobView(ImportJob job) => new(job.Id, job.Provider, job.ExternalId, job.AdapterVersion,
        job.State, job.Stage, job.Attempts, job.DueUtc, job.LeaseUntilUtc, job.CreatedUtc, job.UpdatedUtc, job.FirstCompletedUtc,
        job.RefreshEnabled, job.ResultJson is null ? null : JsonSerializer.Deserialize<ImportResult>(job.ResultJson, ImportJobStore.Json));

    private static DiscoveryResponse DiscoveryView(DiscoveryRun run) => new(run.Id, run.Provider, run.AdapterVersion, run.Cursor,
        run.MaximumItems, run.Discovered, run.Pages, run.State, run.Attempts, run.DueUtc, run.LeaseUntilUtc, run.Error,
        run.UpdatedUtc, run.PendingItems?.Length ?? 0);

    private static string? ExpectedVersion(HttpContext context, string? current)
    {
        var header = context.Request.Headers.IfMatch.ToString();
        if (header.Length == 0)
        {
            if (current is not null) throw new PreconditionRequiredException();
            return ""; // Atomically create-only; a concurrent first save must never be overwritten.
        }
        if (header == "*") return header;
        if (header.Length != 66 || header[0] != '"' || header[^1] != '"' || !header[1..^1].All(Uri.IsHexDigit))
            throw new ArgumentException("Supply a single strong configuration ETag.");
        return header[1..^1].ToLowerInvariant();
    }
}

public sealed class PreconditionRequiredException : Exception;
public sealed record ReleaseReservationsRequest(ExportReleaseSelection[] Files, string Confirmation);

public sealed record EnabledRequest(bool Enabled);
public sealed record PreviewRequest(long ExternalId, bool ProcessImages = false, bool PlanDatabase = false);
public sealed record DiscoveryPreviewRequest(string? Cursor = null);
public sealed record EnqueueRequest(long[] Ids);
public sealed record DiscoveryRequest(int? MaximumItems = null, bool Restart = false);
public sealed record DatabasePlanRequest(DatabaseDestination Destination, AdapterItem Item,
    AssetResult[] Assets, string? MediaId = null, string AdapterVersion = "");
