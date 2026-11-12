using System.Net;
using System.Text;
using System.Text.Json;
using MediaImporter;

public sealed class AdapterTransportTests
{
    [Fact]
    public async Task Graphql_uses_post_with_a_fresh_json_body()
    {
        string? query = null;
        using var fixture = new AdapterRuntimeTests.RuntimeFixture("shikimori", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/graphql")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                query = document.RootElement.GetProperty("query").GetString();
                return AdapterRuntimeTests.Json("""{"data":{"animes":[{"id":"52991","poster":null}]}}""");
            }
            return request.RequestUri.AbsolutePath == "/api/animes/52991" ? AdapterRuntimeTests.Json("""{"id":52991,"name":"Frieren"}""") : AdapterRuntimeTests.Json("[]");
        });
        await fixture.Runtime.ReadItemAsync(fixture.Snapshot, 52991, CancellationToken.None);
        Assert.Contains("52991", query);
    }

    [Fact]
    public async Task Key_bearing_discovery_url_is_sent_but_http_failure_exposes_only_safe_code()
    {
        using var fixture = new AdapterRuntimeTests.RuntimeFixture("steam", request =>
        {
            Assert.Contains("key=test-secret", request.RequestUri!.Query);
            return new(HttpStatusCode.Forbidden);
        });
        var error = await Assert.ThrowsAsync<AdapterReadException>(() => fixture.Runtime.DiscoverAsync(fixture.Snapshot, null, CancellationToken.None));
        Assert.Equal("http_403", error.Code);
        Assert.True(error.Retryable);
        Assert.DoesNotContain("test-secret", error.ToString());
        Assert.DoesNotContain("https://", error.Message);
    }

    [Fact]
    public async Task Foreign_image_hosts_are_rejected_before_reserving_a_network_slot()
    {
        using var fixture = new AdapterRuntimeTests.RuntimeFixture("shikimori", _ => throw new InvalidOperationException());
        var error = await Assert.ThrowsAsync<AdapterReadException>(() => fixture.Transport.DownloadImageAsync(fixture.Snapshot, "https://example.org/cover.jpg", CancellationToken.None));
        Assert.Equal("url_host_rejected", error.Code);
        Assert.Equal(0, fixture.Limiter.Requests);
    }

    [Fact]
    public async Task Chunked_image_body_is_bounded_without_a_content_length()
    {
        using var fixture = new AdapterRuntimeTests.RuntimeFixture("shikimori", _ => new(HttpStatusCode.OK)
        { Content = new StreamContent(new MemoryStream(new byte[8 * 1024 * 1024 + 1])) });
        var error = await Assert.ThrowsAsync<AdapterReadException>(() => fixture.Transport.DownloadImageAsync(fixture.Snapshot, "https://shikimori.io/cover.jpg", CancellationToken.None));
        Assert.Equal("response_too_large", error.Code);
        Assert.Equal(1, fixture.Limiter.Requests);
    }
}
