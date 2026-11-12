using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MediaImporter.Images;
using Npgsql;

namespace MediaImporter.Tests;

public sealed class ServiceApiTests
{
    [Fact]
    public async Task Management_requires_the_private_key_while_liveness_remains_available()
    {
        await using var fixture = await Fixture.CreateAsync();
        var key = fixture.Client.DefaultRequestHeaders.Authorization!.Parameter;
        fixture.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/v1/importer/adapters")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/health/live")).StatusCode);
        fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", key);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/v1/importer/adapters")).StatusCode);
    }

    [Fact]
    public async Task Preview_uses_the_same_adapter_contract_without_writing_catalog_data()
    {
        await using var fixture = await Fixture.CreateAsync();
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/importer/adapters/source/preview", new { externalId = 41 });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ImportResult>();
        Assert.Equal("preview", result!.Mode);
        Assert.Equal("completed", result.Status);
        Assert.Null(result.MediaId);
        Assert.Equal("Item 41", result.Item!.Title);
        Assert.Empty(Directory.EnumerateFiles(fixture.MediaRoot, "*", SearchOption.AllDirectories));
        Assert.False(fixture.Registry.Get("source").Definition.Enabled);
    }

    [Fact]
    public async Task A_corrupt_image_preserves_card_diagnostics_and_marks_only_the_asset_for_retry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = fixture.Registry.Get("source").Definition;
        definition = definition with
        {
            AssetPolicies = new() { ["poster"] = new() { Storage = "local", ImageProfile = "avif80", MaxItems = 1 } },
            Mapping = definition.Mapping with { Assets = new()
            {
                ["poster"] = new() { Request = "item", UrlTemplates = ["https://example.org/broken.jpg"] }
            } }
        };
        await fixture.Registry.SaveAsync("source", JsonSerializer.Serialize(definition, AdapterRegistry.Json), default);
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/importer/adapters/source/preview",
            new { externalId = 41, processImages = true });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ImportResult>();
        Assert.Equal("Item 41", result!.Item!.Title);
        Assert.Equal("partial", result.Status);
        Assert.Equal("retry", Assert.Single(result.Assets).State);
    }

    [Fact]
    public async Task Database_plan_without_image_processing_marks_the_file_unresolved_without_downloading()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        await fixture.EnablePosterAsync();
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/importer/adapters/source/preview",
            new { externalId = 41, planDatabase = true });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ImportResult>();
        Assert.NotNull(result!.Plan);
        Assert.Equal("source-41", result.MediaId);
        Assert.Equal(1, fixture.PreviewSink.Calls);
        Assert.Equal("planned", Assert.Single(fixture.PreviewSink.Assets).State);
        Assert.Null(fixture.PreviewSink.Assets[0].Hash);
        Assert.Equal(0, fixture.Handler.ImageRequests);
        Assert.Empty(Directory.EnumerateFiles(fixture.MediaRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Image_checked_plan_has_the_prospective_hash_path_but_does_not_publish_it()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        await fixture.EnablePosterAsync();
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/importer/adapters/source/preview",
            new { externalId = 41, processImages = true, planDatabase = true });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ImportResult>();
        Assert.NotNull(result!.Plan);
        var prospective = Assert.Single(fixture.PreviewSink.Assets);
        Assert.Equal("stored", prospective.State);
        Assert.Equal("source/" + prospective.Hash, prospective.RelativePath);
        Assert.NotNull(prospective.Hash);
        Assert.Equal("image/png", prospective.ContentType);
        Assert.Equal(1, fixture.Handler.ImageRequests);
        Assert.False(File.Exists(Path.Combine(fixture.MediaRoot, prospective.RelativePath!)));
        Assert.Empty(Directory.EnumerateFiles(fixture.MediaRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Discovery_preview_returns_the_common_result_and_diagnostic_errors()
    {
        await using var fixture = await Fixture.CreateAsync();
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/importer/adapters/source/discovery/preview", new { cursor = (string?)null });
        var result = await response.Content.ReadFromJsonAsync<ImportResult>();
        Assert.Equal("preview", result!.Mode);
        Assert.Equal(41, Assert.Single(result.Discovery!.Items).ExternalId);

        fixture.Handler.Fail = true;
        response = await fixture.Client.PostAsJsonAsync("/api/v1/importer/adapters/source/discovery/preview", new { cursor = (string?)null });
        result = await response.Content.ReadFromJsonAsync<ImportResult>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("retry", result!.Status);
        Assert.Equal("http_503", result.Error);
    }

    [Fact]
    public async Task Enabling_persists_a_new_revision_and_invalid_configuration_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Registry.Get("source");
        var enabled = await fixture.Client.PostAsJsonAsync("/api/v1/importer/adapters/source/enabled", new { enabled = true });
        enabled.EnsureSuccessStatusCode();
        Assert.True(fixture.Registry.Get("source").Definition.Enabled);
        Assert.NotEqual(original.Version, fixture.Registry.Get("source").Version);
        var invalid = JsonNode.Parse(original.Json)!.AsObject();
        invalid["requests"] = null;
        var check = await fixture.Client.PostAsJsonAsync("/api/v1/importer/adapters/source/validate", invalid);
        using var validation = JsonDocument.Parse(await check.Content.ReadAsStringAsync());
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());
        Assert.True(fixture.Registry.Get("source").Definition.Enabled);

        var malformed = await fixture.Client.PostAsJsonAsync("/api/v1/importer/adapters/source/jobs", new { ids = (long[]?)null });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task Encoder_api_installs_a_profile_that_an_adapter_can_use_for_real_preview()
    {
        await using var fixture = await Fixture.CreateAsync();
        var module = JsonNode.Parse("""
            {"id":"custom","displayName":"Custom WebP","format":"WebP","profiles":[{"name":"custom-webp","quality":71}]}
            """)!;
        (await fixture.Client.PutAsJsonAsync("/api/v1/importer/encoders/custom", module)).EnsureSuccessStatusCode();
        using var profiles = JsonDocument.Parse(await fixture.Client.GetStringAsync("/api/v1/importer/image-profiles"));
        Assert.Contains(profiles.RootElement.GetProperty("profiles").EnumerateArray(),
            profile => profile.GetProperty("name").GetString() == "custom-webp");
        await fixture.EnablePosterAsync();
        var adapter = JsonNode.Parse(fixture.Registry.Get("source").Json)!;
        adapter["assetPolicies"]!["poster"]!["imageProfile"] = "custom-webp";
        (await fixture.Client.PutAsJsonAsync("/api/v1/importer/adapters/source", adapter)).EnsureSuccessStatusCode();
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/importer/adapters/source/preview", new { externalId = 41, processImages = true });
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ImportResult>())!;
        var asset = Assert.Single(result.Assets);
        Assert.Equal("stored", asset.State);
        Assert.Equal("custom-webp", asset.ActualProfile);
        Assert.Equal("image/webp", asset.ContentType);
        Assert.Empty(Directory.EnumerateFiles(fixture.MediaRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Encoder_api_rejects_invalid_modules_and_unknown_adapter_profiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var invalid = new { id = "custom", displayName = "Invalid", format = "MissingFormat", profiles = new[] { new { name = "custom", quality = 80 } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PutAsJsonAsync("/api/v1/importer/encoders/custom", invalid)).StatusCode);
        using var validation = JsonDocument.Parse(await (await fixture.Client.PostAsJsonAsync("/api/v1/importer/encoders/custom/validate", invalid)).Content.ReadAsStringAsync());
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());
        await fixture.EnablePosterAsync();
        var adapter = JsonNode.Parse(fixture.Registry.Get("source").Json)!;
        adapter["assetPolicies"]!["poster"]!["imageProfile"] = "missing-profile";
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PutAsJsonAsync("/api/v1/importer/adapters/source", adapter)).StatusCode);
        Assert.Equal("original", fixture.Registry.Get("source").Definition.AssetPolicies["poster"].ImageProfile);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "catalogweaver-api-" + Guid.NewGuid().ToString("N"));
        private WebApplication app = null!;
        public string MediaRoot => Path.Combine(root, "media");
        public HttpClient Client { get; private set; } = null!;
        public AdapterRegistry Registry => app.Services.GetRequiredService<AdapterRegistry>();
        public ResponseHandler Handler { get; } = new();
        public PlanningSink PreviewSink { get; } = new();
        private bool usePlanningSink;

        public static async Task<Fixture> CreateAsync(bool planning = false)
        {
            var fixture = new Fixture { usePlanningSink = planning };
            await fixture.StartAsync();
            return fixture;
        }

        private async Task StartAsync()
        {
            var encodersPath = Path.Combine(root, "encoders");
            foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "encoders"), "encoder.json", SearchOption.AllDirectories))
            {
                var target = Path.Combine(encodersPath, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "encoders"), file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Importer:AdaptersPath"] = Path.Combine(root, "adapters"),
                ["Importer:EncodersPath"] = encodersPath,
                ["Importer:MediaRoot"] = MediaRoot
            });
            var options = new ImporterOptions { MediaRoot = MediaRoot, ApiKey = "TestOnlyImporterApiKeyAtLeast32Characters" };
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(_ => NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused_test"));
            builder.Services.AddSingleton<ImportJobStore>();
            builder.Services.AddSingleton<EncoderRegistry>();
            builder.Services.AddSingleton<AdapterRegistry>();
            builder.Services.AddSingleton<IProviderRateLimiter, NoWaitLimiter>();
            builder.Services.AddSingleton(new HttpClient(Handler));
            builder.Services.AddTransient<AdapterTransport>();
            builder.Services.AddTransient<AdapterRuntime>();
            builder.Services.AddSingleton(new ImageStorageOptions { MediaRoot = MediaRoot, MinimumFreeBytes = 0 });
            builder.Services.AddSingleton<IImporterImageCodec, ImporterImageCodec>();
            builder.Services.AddSingleton<ImagePipeline>();
            builder.Services.AddScoped<StandalonePersistence>();
            if (usePlanningSink) builder.Services.AddSingleton<IImportPersistence>(PreviewSink);
            else builder.Services.AddScoped<IImportPersistence>(services => services.GetRequiredService<StandalonePersistence>());
            builder.Services.AddSingleton<MediaImporter.Database.PostgresDatabaseWriter>();
            builder.Services.AddScoped<ImportOrchestrator>();
            app = builder.Build();
            ImporterProgram.MapEndpoints(app, options);
            var definition = new AdapterDefinition
            {
                Provider = "source", DisplayName = "Source", Kind = "game", BaseUrl = "https://example.org",
                SourceUrlTemplate = "https://example.org/items/{{externalId}}",
                Destinations = new() { Files = new() { Directory = Path.Combine(MediaRoot, "source") } },
                Requests = new()
                {
                    ["discover"] = new() { Path = "/ids", ResponseKind = "array" },
                    ["item"] = new() { Path = "/items/{{externalId}}", Required = true, ResponseKind = "object" }
                },
                Mapping = new() { Fields = new()
                {
                    ["title"] = new() { Path = "$.title" }, ["externalId"] = new() { Path = "$.id" }
                } }
            };
            await Registry.SaveAsync("source", JsonSerializer.Serialize(definition, AdapterRegistry.Json), default);
            await app.StartAsync();
            Client = app.GetTestClient();
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        public Task EnablePosterAsync()
        {
            var definition = Registry.Get("source").Definition;
            definition = definition with
            {
                AssetPolicies = new() { ["poster"] = new() { Storage = "local", ImageProfile = "original", MaxItems = 1 } },
                Mapping = definition.Mapping with { Assets = new()
                {
                    ["poster"] = new() { Request = "item", UrlTemplates = ["https://example.org/valid.png"] }
                } }
            };
            return Registry.SaveAsync("source", JsonSerializer.Serialize(definition, AdapterRegistry.Json), default);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public int ImageRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (request.RequestUri!.AbsolutePath.EndsWith(".png", StringComparison.Ordinal))
            {
                ImageRequests++;
                using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 1, 1);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(image.ToByteArray(ImageMagick.MagickFormat.Png))
                });
            }
            if (request.RequestUri!.AbsolutePath.EndsWith(".jpg", StringComparison.Ordinal))
            {
                ImageRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0xff, 0xd8, 0xff, 0x00, 0x01])
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create<object>(
                request.RequestUri.AbsolutePath == "/ids" ? new[] { new { id = 41 } } : new { id = 41, title = "Item 41" }) });
        }
    }

    private sealed class NoWaitLimiter : IProviderRateLimiter
    {
        public Task<IProviderRateLease> AcquireAsync(string provider, RateLimitDefinition limits, CancellationToken ct)
            => Task.FromResult<IProviderRateLease>(new Lease());
        private sealed class Lease : IProviderRateLease
        {
            public Task DeferAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class PlanningSink : IImportPersistence
    {
        public int Calls { get; private set; }
        public AssetResult[] Assets { get; private set; } = [];
        public Task<ImportPreviewPlan> PlanAsync(AdapterItem imported, IReadOnlyList<AssetResult> results,
            string adapterVersion, CancellationToken ct, MediaImporter.Database.DatabaseDestination? destination = null)
        {
            Calls++; Assets = results.ToArray();
            return Task.FromResult(new ImportPreviewPlan { MediaId = imported.Provider + "-" + imported.ExternalId, Assets = Assets });
        }
        public Task<PersistenceResult> SaveAsync(AdapterItem item, IReadOnlyList<AssetResult> results, string version,
            CancellationToken ct, ImportJob? job = null, MediaImporter.Database.DatabaseDestination? destination = null)
            => throw new InvalidOperationException("Preview attempted a write.");
        public Task CleanupAsync(PersistenceResult result, CancellationToken ct) => throw new InvalidOperationException("Preview attempted cleanup.");
        public Task<int> SweepOrphansAsync(CancellationToken ct) => throw new InvalidOperationException("Preview attempted sweeping.");
    }
}
