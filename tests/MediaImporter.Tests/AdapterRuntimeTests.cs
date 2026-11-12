using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MediaImporter;

public sealed class AdapterRuntimeTests
{
    [Fact]
    public async Task Shikimori_maps_all_screenshots_preserves_titles_and_distinguishes_failed_roles()
    {
        var images = Enumerable.Range(1, 10).Select(index => new { original = $"/screenshots/{index}.jpg" }).ToArray();
        using var fixture = new RuntimeFixture("shikimori", request => request.RequestUri!.AbsolutePath switch
        {
            "/api/animes/52991" => Json("""{"id":52991,"name":"[Oshi no Ko]","russian":"","genres":[{"russian":"Драма"}],"score":"8.9"}"""),
            "/api/graphql" => Json("""{"data":{"animes":[{"id":"52991","poster":null}]}}"""),
            "/api/animes/52991/screenshots" => Json(JsonSerializer.Serialize(images)),
            "/api/animes/52991/videos" => new(HttpStatusCode.ServiceUnavailable),
            _ => throw new InvalidOperationException()
        });

        var item = await fixture.Runtime.ReadItemAsync(fixture.Snapshot, 52991, CancellationToken.None);

        Assert.Equal("[Oshi no Ko]", item.Title);
        Assert.Equal("complete", item.AssetStatus["poster"]);
        Assert.DoesNotContain(item.Assets, asset => asset.Role == "poster");
        Assert.Equal("retry", item.AssetStatus["video"]);
        Assert.Equal(10, item.Assets.Count(asset => asset.Role == "screenshot"));
        Assert.Equal("https://shikimori.io/screenshots/1.jpg", item.Assets[0].SourceUrl);
        Assert.Equal(4, fixture.Limiter.Requests);
    }

    [Fact]
    public async Task Invalid_graphql_poster_is_retry_while_explicit_null_is_complete()
    {
        using var fixture = new RuntimeFixture("shikimori", request => request.RequestUri!.AbsolutePath switch
        {
            "/api/animes/52991" => Json("""{"id":52991,"name":"Frieren"}"""),
            "/api/graphql" => Json("""{"data":{"animes":[]}}"""),
            _ => Json("[]")
        });
        var item = await fixture.Runtime.ReadItemAsync(fixture.Snapshot, 52991, CancellationToken.None);
        Assert.Equal("retry", item.AssetStatus["poster"]);
        Assert.Contains(item.Warnings, warning => warning.StartsWith("request:poster:"));
    }

    [Fact]
    public async Task Wrong_shape_of_screenshot_response_cannot_clear_saved_screenshots()
    {
        using var fixture = new RuntimeFixture("shikimori", request => request.RequestUri!.AbsolutePath switch
        {
            "/api/animes/52991" => Json("""{"id":52991,"name":"Frieren"}"""),
            "/api/graphql" => Json("""{"data":{"animes":[{"id":"52991","poster":null}]}}"""),
            "/api/animes/52991/screenshots" => Json("{}"),
            _ => Json("[]")
        });
        var item = await fixture.Runtime.ReadItemAsync(fixture.Snapshot, 52991, CancellationToken.None);
        Assert.Equal("retry", item.AssetStatus["screenshot"]);
        Assert.Equal("complete", item.AssetStatus["video"]);
    }

    [Fact]
    public async Task Discovery_page_and_empty_final_page_have_resumable_cursors()
    {
        using var fixture = new RuntimeFixture("shikimori", request => request.RequestUri!.Query.Contains("page=1", StringComparison.Ordinal)
            ? Json("""[{"id":1,"name":"One"},{"id":2,"name":"Two"}]""") : Json("[]"));
        var first = await fixture.Runtime.DiscoverAsync(fixture.Snapshot, null, CancellationToken.None);
        var last = await fixture.Runtime.DiscoverAsync(fixture.Snapshot, first.NextCursor, CancellationToken.None);
        Assert.Equal(new long[] { 1, 2 }, first.Items.Select(item => item.ExternalId));
        Assert.Equal("2", first.NextCursor);
        Assert.Empty(last.Items);
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task Max_items_is_applied_after_url_normalization_and_deduplication()
    {
        using var fixture = new RuntimeFixture("shikimori", request => request.RequestUri!.AbsolutePath switch
        {
            "/api/animes/52991" => Json("""{"id":52991,"name":"Frieren"}"""),
            "/api/graphql" => Json("""{"data":{"animes":[{"id":"52991","poster":null}]}}"""),
            "/api/animes/52991/screenshots" => Json("""[{"original":"/one.jpg"},{"original":"https://shikimori.io/one.jpg"},{"original":"/two.jpg"},{"original":"/three.jpg"}]"""),
            _ => Json("[]")
        });
        var policies = new Dictionary<string, AssetPolicy>(fixture.Snapshot.Definition.AssetPolicies)
        { ["screenshot"] = new() { Storage = "link", MaxItems = 2 } };
        var snapshot = fixture.Snapshot with { Definition = fixture.Snapshot.Definition with { AssetPolicies = policies } };
        var item = await fixture.Runtime.ReadItemAsync(snapshot, 52991, CancellationToken.None);
        Assert.Equal(new[] { "https://shikimori.io/one.jpg", "https://shikimori.io/two.jpg" }, item.Assets.Select(asset => asset.SourceUrl));
    }

    [Fact]
    public async Task Steam_country_fallback_only_follows_explicit_unavailability()
    {
        var countries = new List<string>();
        using var fixture = new RuntimeFixture("steam", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/appdetails")
            {
                countries.Add(request.RequestUri.Query);
                if (request.RequestUri.Query.Contains("cc=us", StringComparison.Ordinal)) return Json("""{"620":{"success":false}}""");
                return Json("""{"620":{"success":true,"data":{"steam_appid":620,"type":"game","name":"Portal 2","short_description":"Short","detailed_description":"Long","about_the_game":"About"}}}""");
            }
            return Json("""{"response":{"store_items":[]}}""");
        });
        var item = await fixture.Runtime.ReadItemAsync(fixture.Snapshot, 620, CancellationToken.None);
        Assert.Equal(2, countries.Count);
        Assert.Equal("jp", item.Country);
        Assert.Equal("Portal 2", item.Title);
    }

    [Fact]
    public async Task Steam_upstream_error_does_not_probe_other_countries()
    {
        var calls = 0;
        using var fixture = new RuntimeFixture("steam", _ => { calls++; return new(HttpStatusCode.TooManyRequests); });
        var error = await Assert.ThrowsAsync<AdapterReadException>(() => fixture.Runtime.ReadItemAsync(fixture.Snapshot, 620, CancellationToken.None));
        Assert.Equal(429, error.HttpStatusCode);
        Assert.True(error.Retryable);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Malformed_poster_object_does_not_report_an_empty_complete_role()
    {
        using var fixture = new RuntimeFixture("shikimori", request => request.RequestUri!.AbsolutePath switch
        {
            "/api/animes/52991" => Json("""{"id":52991,"name":"Frieren"}"""),
            "/api/graphql" => Json("""{"data":{"animes":[{"id":"52991","poster":{}}]}}"""),
            _ => Json("[]")
        });
        var item = await fixture.Runtime.ReadItemAsync(fixture.Snapshot, 52991, CancellationToken.None);
        Assert.Equal("retry", item.AssetStatus["poster"]);
    }

    [Fact]
    public async Task Steam_cursor_can_use_last_id_when_optional_cursor_fields_are_absent()
    {
        using var fixture = new RuntimeFixture("steam", _ => Json("""{"response":{"apps":[{"appid":10,"name":"Ten","last_modified":12}]}}"""));
        var discovery = fixture.Snapshot.Definition.Discovery;
        var snapshot = fixture.Snapshot with { Definition = fixture.Snapshot.Definition with
        { Discovery = discovery with { Pagination = discovery.Pagination with { PageSize = 1 } } } };
        var page = await fixture.Runtime.DiscoverAsync(snapshot, null, CancellationToken.None);
        Assert.Equal("10", page.NextCursor);
        Assert.Equal(10, Assert.Single(page.Items).ExternalId);
    }

    [Fact]
    public async Task A_new_provider_uses_the_same_mapping_runtime()
    {
        using var fixture = new RuntimeFixture("shikimori", request => request.RequestUri!.AbsolutePath switch
        {
            "/api/animes/52991" => Json("""{"id":52991,"name":"A new source"}"""),
            "/api/graphql" => Json("""{"data":{"animes":[{"id":"52991","poster":null}]}}"""),
            _ => Json("[]")
        });
        var snapshot = fixture.Snapshot with { Definition = fixture.Snapshot.Definition with { Provider = "third-source" } };
        var item = await fixture.Runtime.ReadItemAsync(snapshot, 52991, CancellationToken.None);
        Assert.Equal("third-source", item.Provider);
        Assert.Equal("A new source", item.Title);
    }

    [Fact]
    public async Task Steam_library_filenames_and_alternates_are_read_from_provider_response()
    {
        using var fixture = new RuntimeFixture("steam", request => request.RequestUri!.AbsolutePath switch
        {
            "/api/appdetails" => Json("""{"620":{"success":true,"data":{"steam_appid":620,"type":"game","name":"Portal 2","short_description":"Short","detailed_description":"Long","about_the_game":"About"}}}"""),
            _ => Json("""{"response":{"store_items":[{"appid":620,"assets":{"asset_url_format":"steam/apps/620/${FILENAME}?t=1","library_capsule_2x":"hash/poster.jpg","library_capsule":"small.jpg","library_hero":"hero.jpg"}}]}}""")
        });
        var item = await fixture.Runtime.ReadItemAsync(fixture.Snapshot, 620, CancellationToken.None);
        var poster = Assert.Single(item.Assets, asset => asset.Role == "poster");
        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/620/hash/poster.jpg?t=1", poster.SourceUrl);
        Assert.EndsWith("/small.jpg?t=1", Assert.Single(poster.Alternatives));
        Assert.Equal("complete", item.AssetStatus["poster"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Optional_description_failure_is_distinct_from_a_successfully_empty_description(bool unavailable)
    {
        using var fixture = new RuntimeFixture("shikimori", request => request.RequestUri!.AbsolutePath == "/description"
            ? unavailable ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json("{\"description\":\"\"}")
            : Json("{\"id\":52991,\"name\":\"Example\"}"));
        var definition = fixture.Snapshot.Definition;
        var requests = new Dictionary<string, RequestDefinition>(definition.Requests)
        { ["description"] = new() { Path = "/description", ResponseKind = "object" } };
        var fields = new Dictionary<string, FieldMapping>
        {
            ["title"] = new() { Request = "item", Path = "$.name" },
            ["description"] = new() { Request = "description", Path = "$.description" }
        };
        var snapshot = fixture.Snapshot with { Definition = definition with
        { Requests = requests, Mapping = new() { Fields = fields }, AssetPolicies = [] } };
        var item = await fixture.Runtime.ReadItemAsync(snapshot, 52991, default);
        Assert.Equal("", item.Description);
        Assert.Equal(unavailable ? "retry" : "complete", item.FieldStatus["description"]);
        Assert.Equal("complete", item.FieldStatus["title"]);
        Assert.Equal(unavailable, item.Warnings.Any(value => value.StartsWith("request:description:", StringComparison.Ordinal)));
    }

    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal sealed class RuntimeFixture : IDisposable
    {
        public AdapterSnapshot Snapshot { get; }
        public AdapterRuntime Runtime { get; }
        public AdapterTransport Transport { get; }
        public FakeLimiter Limiter { get; } = new();
        private readonly HttpClient client;

        public RuntimeFixture(string provider, Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Importer:MediaRoot"] = "/data/media", ["Importer:Secrets:SteamApiKey"] = "test-secret" }).Build();
            var registry = new AdapterRegistry(configuration, NullLogger<AdapterRegistry>.Instance);
            var path = Path.Combine(AppContext.BaseDirectory, "adapters", provider, "adapter.json");
            Snapshot = registry.FromJson(File.ReadAllText(path));
            client = new HttpClient(new FakeHandler(respond));
            Transport = new AdapterTransport(client, Limiter, configuration);
            Runtime = new AdapterRuntime(Transport);
        }

        public void Dispose() => client.Dispose();
    }

    internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    internal sealed class FakeLimiter : IProviderRateLimiter
    {
        public int Requests { get; private set; }
        public Task<IProviderRateLease> AcquireAsync(string provider, RateLimitDefinition limits, CancellationToken ct)
        { Requests++; return Task.FromResult<IProviderRateLease>(new FakeLease()); }

        private sealed class FakeLease : IProviderRateLease
        {
            public Task DeferAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
