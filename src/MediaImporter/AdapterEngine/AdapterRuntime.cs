using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MediaImporter;

public sealed class AdapterRuntime(AdapterTransport transport)
{
    private static readonly HashSet<string> SecretFields = new(["key", "api_key", "apikey", "access_token", "refresh_token", "authorization", "password", "secret"], StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Html = new(@"<[^>]*>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task<DiscoveryPage> DiscoverAsync(AdapterSnapshot snapshot, string? cursor, CancellationToken ct)
    {
        var definition = snapshot.Definition;
        var discovery = definition.Discovery;
        var pagination = discovery.Pagination;
        var variables = Variables(definition);
        variables["pageSize"] = pagination.PageSize.ToString(CultureInfo.InvariantCulture);
        if (pagination.Type == "page")
        {
            if (!long.TryParse(cursor ?? pagination.StartPage.ToString(CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < pagination.StartPage)
                throw new AdapterReadException("invalid_cursor", false);
            variables["page"] = page.ToString(CultureInfo.InvariantCulture);
        }
        else variables["cursor"] = cursor ?? "0";
        var request = definition.Requests[discovery.Request];
        var root = await ReadRequestAsync(snapshot, request, variables, [], ct);
        var nodes = AdapterJsonPath.Select(root, discovery.ItemsPath)
            .SelectMany(value => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value]);
        var items = new List<DiscoveryEntry>();
        foreach (var node in nodes)
        {
            var id = ReadPositiveId(First(node, discovery.ExternalIdPath));
            if (id is null) throw new AdapterReadException("invalid_discovery_identity", true);
            var name = discovery.NamePath is null ? null : Plain(AdapterJsonPath.Text(First(node, discovery.NamePath)), 1024);
            var modified = discovery.ModifiedPath is null ? null : ReadLong(First(node, discovery.ModifiedPath));
            items.Add(new(id.Value, name, modified));
        }
        if (items.Count > pagination.PageSize || items.Select(x => x.ExternalId).Distinct().Count() != items.Count)
            throw new AdapterReadException("invalid_discovery_page", true);
        if (pagination.CursorFromLastId && (!long.TryParse(cursor ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out var previous)
            || items.Any(x => x.ExternalId <= previous) || !items.Select(x => x.ExternalId).SequenceEqual(items.Select(x => x.ExternalId).Order())))
            throw new AdapterReadException("invalid_discovery_order", true);
        var hasMore = pagination.HasMorePath is null ? items.Count > 0 : First(root, pagination.HasMorePath).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Undefined when pagination.HasMoreWhenMissing == "fullPage" => items.Count == pagination.PageSize,
            _ => throw new AdapterReadException("invalid_discovery_continuation", true)
        };
        if (hasMore && items.Count == 0) throw new AdapterReadException("empty_continuation_page", true);
        if (pagination.StopOnEmpty && items.Count == 0) hasMore = false;
        string? next = null;
        if (hasMore)
        {
            next = pagination.Type == "page"
                ? (long.Parse(variables["page"], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture)
                : pagination.CursorFromLastId ? items[^1].ExternalId.ToString(CultureInfo.InvariantCulture)
                : AdapterJsonPath.Text(First(root, pagination.NextCursorPath ?? throw new AdapterReadException("missing_cursor_path", false)));
            if (pagination.CursorFromLastId && pagination.NextCursorPath is { } declaredPath)
            {
                var declared = First(root, declaredPath);
                if (declared.ValueKind != JsonValueKind.Undefined && AdapterJsonPath.Text(declared) != next)
                    throw new AdapterReadException("inconsistent_discovery_cursor", true);
            }
            if (string.IsNullOrWhiteSpace(next) || next == cursor || next == "0" && pagination.Type == "cursor")
                throw new AdapterReadException("nonadvancing_cursor", true);
        }
        return new(items.ToArray(), next);
    }

    public async Task<AdapterItem> ReadItemAsync(AdapterSnapshot snapshot, long externalId, CancellationToken ct)
    {
        if (externalId is < 1 or > int.MaxValue) throw new AdapterReadException("invalid_external_id", false);
        var definition = snapshot.Definition;
        var variables = Variables(definition);
        variables["externalId"] = externalId.ToString(CultureInfo.InvariantCulture);
        var responses = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var needed = definition.Mapping.Fields.Values.Select(x => x.Request)
            .Concat(definition.Mapping.Assets.Where(x => definition.AssetPolicies.ContainsKey(x.Key))
                .SelectMany(x => Sources(x.Value).Select(source => source.Request))).Distinct().ToHashSet(StringComparer.Ordinal);
        foreach (var (name, request) in definition.Requests.Where(x => needed.Contains(x.Key)))
        {
            try { responses[name] = await ReadRequestAsync(snapshot, request, variables, warnings, ct); }
            catch (AdapterReadException error) when (!request.Required)
            {
                failed.Add(name);
                warnings.Add("request:" + name + ":" + error.Code);
            }
        }
        var fields = definition.Mapping.Fields.ToDictionary(x => x.Key,
            x => MapField(x.Value, responses, variables), StringComparer.Ordinal);
        string Text(string field, int limit = 6000) => fields.TryGetValue(field, out var values) ? Plain(values.FirstOrDefault() ?? "", limit) : "";
        string[] Strings(string field) => fields.GetValueOrDefault(field, []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        int Number(string field) => int.TryParse(Text(field), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        var title = Text("title", 250);
        if (title.Length == 0) throw new AdapterReadException("missing_title", true);
        if (fields.TryGetValue("externalId", out var identity)
            && (!long.TryParse(identity.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var mappedId) || mappedId != externalId))
            throw new AdapterReadException("identity_mismatch", true);

        var assets = new List<AdapterAsset>();
        var assetStatus = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (role, mapping) in definition.Mapping.Assets)
        {
            if (!definition.AssetPolicies.TryGetValue(role, out var policy)) continue;
            var sources = Sources(mapping);
            assetStatus[role] = sources.Any(x => failed.Contains(x.Request)) ? "retry" : "complete";
            var roleAssets = new List<AdapterAsset>();
            foreach (var source in sources)
            {
                if (!responses.TryGetValue(source.Request, out var response)) continue;
                try { roleAssets.AddRange(MapAssets(snapshot, role, source, policy, response, variables)); }
                catch (AdapterReadException error) { assetStatus[role] = "retry"; warnings.Add("assets:" + role + ":" + error.Code); }
            }
            assets.AddRange(roleAssets.DistinctBy(x => x.SourceUrl).Take(policy.MaxItems ?? int.MaxValue));
        }
        var sourceUrl = transport.Expand(definition.SourceUrlTemplate, variables);
        if (!SafeUrl(sourceUrl)) throw new AdapterReadException("invalid_source_url", false);
        var metadata = new JsonObject();
        foreach (var (name, response) in responses) metadata[name] = Sanitize(response);
        metadata["version"] = 1;
        metadata["text_format"] = "plain";
        var country = Text("country", 2);
        var language = Text("language", 32);
        metadata["country"] = country;
        metadata["language"] = language;
        var metadataText = metadata.ToJsonString();
        if (System.Text.Encoding.UTF8.GetByteCount(metadataText) > 1024 * 1024) throw new AdapterReadException("metadata_too_large", true);
        using var metadataDocument = JsonDocument.Parse(metadataText);
        _ = double.TryParse(Text("rating"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rating);
        var episodes = Number("episodes");
        return new()
        {
            Provider = definition.Provider, ExternalId = externalId, Kind = definition.Kind, Title = title,
            OriginalTitle = Text("originalTitle", 250) is { Length: > 0 } original ? original : title,
            AlternativeTitles = Strings("alternativeTitles").Select(x => Plain(x, 250)).Distinct().Take(30).ToArray(),
            Description = Text("description"), Genres = Strings("genres"), Tags = Strings("tags"),
            Creator = fields.TryGetValue("creator", out var creators) ? Plain(string.Join(", ", creators), 250) : "",
            Year = Number("year"), Rating = double.IsFinite(rating) ? Math.Clamp(rating, 0, 10) : 0,
            Episodes = episodes > 0 ? episodes : null, ReleaseStatus = Text("releaseStatus", 32),
            SourceUrl = sourceUrl, Assets = assets.ToArray(), AssetStatus = assetStatus,
            Metadata = metadataDocument.RootElement.Clone(), Country = country, Language = language, Warnings = warnings.ToArray()
        };
    }

    private async Task<JsonElement> ReadRequestAsync(AdapterSnapshot snapshot, RequestDefinition request,
        Dictionary<string, string> variables, List<string> warnings, CancellationToken ct)
    {
        var alternative = request.AlternativeVariables.FirstOrDefault();
        var values = alternative.Value ?? [""];
        foreach (var value in values)
        {
            if (alternative.Key is not null) variables[alternative.Key] = value;
            var raw = await transport.ReadJsonAsync(snapshot, request, variables, ct);
            if (request.GraphQl && raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("errors", out var errors)
                && errors.ValueKind != JsonValueKind.Null && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 0))
                throw new AdapterReadException("graphql_error", true);
            if (request.AvailabilityPath is { } availability)
            {
                var available = First(raw, transport.Expand(availability, variables));
                if (available.ValueKind == JsonValueKind.False) continue;
                if (available.ValueKind != JsonValueKind.True) throw new AdapterReadException("invalid_availability", true);
            }
            var roots = request.ResponseRootPath is { } rootPath ? AdapterJsonPath.Select(raw, transport.Expand(rootPath, variables)) : [raw];
            if (roots.Count == 0 && request.AllowEmptyRoot) return JsonSerializer.SerializeToElement<object?>(null);
            if (roots.Count != 1) throw new AdapterReadException("invalid_response_root", true);
            var root = roots[0];
            if (request.ResponseKind is "array" && root.ValueKind != JsonValueKind.Array
                || request.ResponseKind is "object" && root.ValueKind != JsonValueKind.Object)
                throw new AdapterReadException("invalid_response_shape", true);
            foreach (var requiredPath in request.RequiredPaths)
                if (AdapterJsonPath.Select(root, transport.Expand(requiredPath, variables)).Count == 0) throw new AdapterReadException("missing_response_field", true);
            var identityPaths = request.IdentityPaths.Length > 0 ? request.IdentityPaths : request.IdentityPath is { } identityPath ? [identityPath] : Array.Empty<string>();
            if (identityPaths.Length > 0 && variables.TryGetValue("externalId", out var expected))
            {
                var actual = identityPaths.Select(path => First(root, transport.Expand(path, variables))).FirstOrDefault(value => !Empty(value));
                if (AdapterJsonPath.Text(actual) != expected) throw new AdapterReadException("identity_mismatch", true);
            }
            if (request.SuccessPath is { } successPath)
            {
                var success = First(root, successPath);
                if (success.ValueKind != JsonValueKind.Undefined && !request.SuccessValues.Contains(AdapterJsonPath.Text(success)))
                    throw new AdapterUnavailableException();
            }
            if (request.FilterPath is { } filterPath)
            {
                var actual = First(root, filterPath);
                if (actual.ValueKind == JsonValueKind.Undefined) throw new AdapterReadException("missing_filter_field", true);
                if (AdapterJsonPath.Text(actual) != request.FilterValue) throw new AdapterExcludedException();
            }
            if (request.Supplement is { } supplement && (supplement.WhenMissingPaths.Any(path => Empty(First(root, path)))
                || supplement.WhenMissingGroups.Any(group => group.All(path => Empty(First(root, path))))))
            {
                try
                {
                    var alternateVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                    foreach (var pair in supplement.Variables) alternateVariables[pair.Key] = pair.Value;
                    var other = await ReadRequestAsync(snapshot, request with { Supplement = null, AlternativeVariables = [] }, alternateVariables, warnings, ct);
                    foreach (var (name, path) in supplement.CaptureVariables) variables[name] = AdapterJsonPath.Text(First(other, path));
                    var merged = JsonNode.Parse(root.GetRawText())!.AsObject();
                    var filled = false;
                    foreach (var path in supplement.FillPaths)
                    {
                        var candidate = First(other, path);
                        if (!Empty(First(root, path)) || Empty(candidate)) continue;
                        if (!Regex.IsMatch(path, @"^\$\.[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)) throw new AdapterReadException("unsupported_supplement_path", false);
                        merged[path[2..]] = JsonNode.Parse(candidate.GetRawText()); filled = true;
                    }
                    if (filled) variables["resultLanguage"] = "mixed";
                    using var document = JsonDocument.Parse(merged.ToJsonString()); root = document.RootElement.Clone();
                }
                catch (AdapterReadException error) { warnings.Add("supplement:" + error.Code); }
            }
            return root.Clone();
        }
        throw new AdapterUnavailableException();
    }

    private string[] MapField(FieldMapping mapping, Dictionary<string, JsonElement> responses, Dictionary<string, string> variables)
    {
        if (mapping.Constant is { } constant) return [transport.Expand(constant, variables)];
        if (!responses.TryGetValue(mapping.Request, out var root)) return [];
        var items = mapping.ItemsPath is { } itemsPath ? AdapterJsonPath.Select(root, itemsPath) : [root];
        var paths = mapping.Paths.Length > 0 ? mapping.Paths : [mapping.Path ?? "$"];
        var result = new List<string>();
        foreach (var item in items)
            foreach (var path in paths)
            {
                var found = AdapterJsonPath.Select(item, transport.Expand(path, variables)).SelectMany(value => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value])
                    .Select(AdapterJsonPath.Text).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                if (found.Length == 0) continue;
                result.AddRange(found.Select(value => Transform(value, mapping.Transform)));
                if (mapping.Selection == "firstNonEmpty") break;
            }
        return result.Where(x => x.Length > 0).ToArray();
    }

    private IEnumerable<AdapterAsset> MapAssets(AdapterSnapshot snapshot, string role, AssetSourceDefinition source,
        AssetPolicy policy, JsonElement response, Dictionary<string, string> variables)
    {
        var paths = source.ItemsPaths.Length > 0 ? source.ItemsPaths : [source.ItemsPath ?? "$"];
        var items = paths.SelectMany(path => AdapterJsonPath.Select(response, path));
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.Null) continue;
            var urls = new List<string>();
            foreach (var template in source.UrlTemplates) urls.Add(transport.Expand(template, variables));
            foreach (var path in source.UrlPaths.Length > 0 ? source.UrlPaths : source.UrlPath is { } single ? [single] : Array.Empty<string>())
                urls.AddRange(AdapterJsonPath.Select(item, path).Select(AdapterJsonPath.Text));
            if (source.RecursiveMediaUrls) urls.AddRange(MediaUrls(item));
            if (source.FormatPath is { } formatPath)
            {
                var format = AdapterJsonPath.Text(First(item, formatPath));
                var filenames = source.FilePaths.SelectMany(path => AdapterJsonPath.Select(item, path)).Select(AdapterJsonPath.Text).Where(x => x.Length > 0).ToArray();
                if (filenames.Length > 0)
                {
                    var prefix = source.RequiredFormatPrefix is null ? null : transport.Expand(source.RequiredFormatPrefix, variables);
                    var filenamePrefix = source.RequiredFilenamePrefix is null ? null : transport.Expand(source.RequiredFilenamePrefix, variables);
                    if (format.Length > 2048 || !format.Contains(source.FormatToken, StringComparison.Ordinal)
                        || prefix is not null && !format.StartsWith(prefix, StringComparison.Ordinal)) throw new AdapterReadException("invalid_asset_format", true);
                    foreach (var filename in filenames)
                    {
                        if (filename.Length > 2048 || filename.Split('/').Any(x => x is "." or "..")
                            || !Regex.IsMatch(filename, @"^[A-Za-z0-9_./-]+$", RegexOptions.CultureInvariant)
                            || filenamePrefix is not null && !filename.StartsWith(filenamePrefix, StringComparison.Ordinal)) throw new AdapterReadException("invalid_asset_filename", true);
                        urls.Add((source.UrlBase ?? "") + format.Replace(source.FormatToken, filename, StringComparison.Ordinal));
                    }
                }
            }
            var normalized = urls.Where(x => x.Length > 0).Select(url => NormalizeAssetUrl(snapshot, source, url)).Distinct().ToArray();
            if (source.RequireUrl && normalized.Length == 0) throw new AdapterReadException("missing_asset_url", true);
            if (normalized.Length == 0) continue;
            var title = source.TitlePath is null ? null : Plain(AdapterJsonPath.Text(First(item, source.TitlePath)), 250);
            foreach (var url in source.EmitAllUrls ? normalized : normalized.Take(1))
                yield return new() { Role = role, SourceUrl = url, Storage = policy.Storage, ImageProfile = policy.ImageProfile,
                    Title = title, Alternatives = source.EmitAllUrls ? [] : normalized.Skip(1).ToArray() };
        }
    }

    private string NormalizeAssetUrl(AdapterSnapshot snapshot, AssetSourceDefinition source, string value)
    {
        if (source.ResolveRelativeUrls && value.StartsWith('/') && !value.StartsWith("//")) value = new Uri(new Uri(snapshot.Definition.BaseUrl), value).AbsoluteUri;
        if (source.UpgradeHttp && Uri.TryCreate(value, UriKind.Absolute, out var old) && old.Scheme == "http") value = new UriBuilder(old) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri;
        if (!SafeUrl(value) || new Uri(value).AbsolutePath.StartsWith("/assets/globals/missing_", StringComparison.OrdinalIgnoreCase))
            throw new AdapterReadException("invalid_asset_url", true);
        return value;
    }

    private static IEnumerable<string> MediaUrls(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().Where(x => x.Name is not ("thumbnail" or "thumbnail_url")).SelectMany(x => MediaUrls(x.Value));
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().SelectMany(MediaUrls);
        if (value.ValueKind != JsonValueKind.String || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)) return [];
        return new[] { ".mp4", ".webm", ".m3u8", ".mpd", ".mov" }.Any(extension => uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ? [value.GetString()!] : [];
    }

    private static Dictionary<string, string> Variables(AdapterDefinition definition) => new(definition.Variables, StringComparer.Ordinal);
    private static AssetSourceDefinition[] Sources(AssetMapping mapping) => mapping.Sources.Length > 0 ? mapping.Sources : [mapping];
    private static JsonElement First(JsonElement root, string path) => AdapterJsonPath.Select(root, path).FirstOrDefault();
    private static bool Empty(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || string.IsNullOrWhiteSpace(AdapterJsonPath.Text(value));
    private static long? ReadPositiveId(JsonElement value) => ReadLong(value) is > 0 and <= int.MaxValue and var number ? number : null;
    private static long? ReadLong(JsonElement value) => long.TryParse(AdapterJsonPath.Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static bool SafeUrl(string value) => value.Length is > 0 and <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static string Transform(string value, string? transform) => transform switch
    {
        "plainText" => Plain(Regex.Replace(value, @"\[/?[^\]\r\n]+\]", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))),
        "year" => Regex.Match(value, @"\b(?:19|20)\d{2}\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Value,
        "number" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number.ToString(CultureInfo.InvariantCulture) : "",
        "releaseStatus" => value is "true" ? "announced" : value is "false" ? "released" : value,
        _ => Plain(value)
    };

    private static string Plain(string value, int maximum = 6000)
    {
        var text = Whitespace.Replace(WebUtility.HtmlDecode(Html.Replace(value, " ")), " ").Trim();
        return text.Length > maximum ? text[..maximum] : text;
    }

    private static JsonNode? Sanitize(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var field in value.EnumerateObject()) if (!SecretFields.Contains(field.Name)) result[field.Name] = Sanitize(field.Value);
            return result;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray(); foreach (var item in value.EnumerateArray()) result.Add(Sanitize(item)); return result;
        }
        return value.ValueKind == JsonValueKind.String ? JsonValue.Create(Plain(value.GetString()!, 100_000)) : JsonNode.Parse(value.GetRawText());
    }
}
