using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MediaImporter;

public sealed class AdapterTransport(HttpClient http, IProviderRateLimiter limiter, IConfiguration configuration)
{
    private static readonly Regex Variables = new(@"\{\{([A-Za-z][A-Za-z0-9_:.-]*)(?:\|(upper|lower))?\}\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task<JsonElement> ReadJsonAsync(AdapterSnapshot snapshot, RequestDefinition request,
        IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        var origin = request.BaseUrl ?? snapshot.Definition.BaseUrl;
        var path = Expand(request.Path, variables, true);
        var uri = new Uri(new Uri(origin), path);
        if (request.Query.Count > 0)
        {
            var query = request.Query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(QueryValue(pair.Value, variables)));
            uri = new UriBuilder(uri) { Query = string.Join('&', query) }.Uri;
        }
        ValidateDownloadUri(snapshot, uri);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        message.Headers.UserAgent.ParseAdd("CatalogWeaver/1.0");
        message.Headers.Accept.ParseAdd("application/json");
        foreach (var (name, value) in request.Headers)
            if (!message.Headers.TryAddWithoutValidation(name, Expand(value, variables, true))) throw new AdapterReadException("invalid_header", false);
        if (request.Body is { } body)
            message.Content = new StringContent(ExpandJson(body, variables).ToJsonString(), Encoding.UTF8, "application/json");
        var bytes = await SendAsync(snapshot, message, request.MaximumResponseBytes, ct);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new AdapterReadException("invalid_json", true); }
    }

    public async Task<byte[]> DownloadImageAsync(AdapterSnapshot snapshot, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new AdapterReadException("invalid_image_url", false);
        ValidateDownloadUri(snapshot, uri);
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.UserAgent.ParseAdd("CatalogWeaver/1.0");
        message.Headers.Accept.ParseAdd("image/avif,image/webp,image/png,image/jpeg");
        return await SendAsync(snapshot, message, configuration.GetValue("Importer:MaximumImageBytes", 8 * 1024 * 1024), ct);
    }

    public string Expand(string template, IReadOnlyDictionary<string, string> variables, bool allowSecrets = false)
    {
        var expanded = Variables.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            var value = name.StartsWith("secret:", StringComparison.Ordinal)
                ? allowSecrets ? configuration["Importer:Secrets:" + name[7..]] ?? throw new AdapterReadException("missing_secret", false)
                    : throw new AdapterReadException("secret_reference_outside_request", false)
                : variables.TryGetValue(name, out var variable) ? variable : throw new AdapterReadException("missing_template_variable", false);
            return match.Groups[2].Value switch { "upper" => value.ToUpperInvariant(), "lower" => value.ToLowerInvariant(), _ => value };
        });
        if (expanded.Contains("{{", StringComparison.Ordinal) || expanded.Contains("}}", StringComparison.Ordinal))
            throw new AdapterReadException("invalid_template", false);
        return expanded;
    }

    public void ValidateDownloadUri(AdapterSnapshot snapshot, Uri uri)
    {
        var allowHttp = configuration.GetValue<bool>("Importer:AllowHttpForTests");
        var allowed = snapshot.Definition.AllowedHosts.Length > 0 ? snapshot.Definition.AllowedHosts : [new Uri(snapshot.Definition.BaseUrl).IdnHost];
        if ((uri.Scheme != "https" || !uri.IsDefaultPort) && !(allowHttp && uri.Scheme == "http"))
            throw new AdapterReadException("url_scheme_rejected", false);
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.OriginalString.Length > 8192
            || !allowed.Any(host => uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
            throw new AdapterReadException("url_host_rejected", false);
    }

    private async Task<byte[]> SendAsync(AdapterSnapshot snapshot, HttpRequestMessage message, int maximumBytes, CancellationToken ct)
    {
        await using var lease = await limiter.AcquireAsync(snapshot.Provider, snapshot.Definition.RateLimit, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Importer:RequestTimeoutSeconds", 45), 1, 300)));
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var delay = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
            if (delay is { } advised && advised > TimeSpan.Zero) await lease.DeferAsync(advised, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var transient = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500 || status is 403 or 408 or 425;
                if (transient && delay is null) { delay = TimeSpan.FromSeconds(30); await lease.DeferAsync(delay.Value, ct); }
                throw new AdapterReadException("http_" + status.ToString(CultureInfo.InvariantCulture), transient)
                { HttpStatusCode = status, RetryAfter = delay };
            }
            if (response.Content.Headers.ContentLength > maximumBytes) throw new AdapterReadException("response_too_large", true);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var count = await body.ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                if (output.Length + count > maximumBytes) throw new AdapterReadException("response_too_large", true);
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AdapterReadException("request_timeout", true); }
        catch (HttpRequestException) { throw new AdapterReadException("transport_error", true); }
        catch (IOException) { throw new AdapterReadException("response_read_error", true); }
    }

    private string QueryValue(JsonElement value, IReadOnlyDictionary<string, string> variables)
        => value.ValueKind == JsonValueKind.String ? Expand(value.GetString()!, variables, true) : ExpandJson(value, variables).ToJsonString();

    private JsonNode ExpandJson(JsonElement value, IReadOnlyDictionary<string, string> variables)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var item in value.EnumerateObject()) result[item.Name] = ExpandJson(item.Value, variables);
            return result;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (var item in value.EnumerateArray()) result.Add(ExpandJson(item, variables));
            return result;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var original = value.GetString()!;
            var expanded = Expand(original, variables, true);
            if (original is "{{externalId}}" or "{{page}}" or "{{pageSize}}" or "{{cursor}}"
                && long.TryParse(expanded, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return JsonValue.Create(number)!;
            return JsonValue.Create(expanded)!;
        }
        return JsonNode.Parse(value.GetRawText())!;
    }
}
