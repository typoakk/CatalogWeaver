using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediaImporter.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaImporter;

public sealed class AdapterRegistry(IConfiguration configuration, ILogger<AdapterRegistry> logger,
    EncoderRegistry? encoders = null) : BackgroundService
{
    private readonly EncoderRegistry encoderRegistry = encoders ?? new EncoderRegistry(configuration, NullLogger<EncoderRegistry>.Instance);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 48
    };

    private readonly ConcurrentDictionary<string, AdapterSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AdapterRegistration> registrations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim reload = new(1, 1);
    public string Root => Path.GetFullPath(configuration["Importer:AdaptersPath"] ?? "adapters");
    public string MediaRoot => Path.GetFullPath(configuration["Importer:MediaRoot"] ?? "/data/media");

    public AdapterRegistration[] List() => registrations.Values.OrderBy(x => x.Provider, StringComparer.Ordinal).ToArray();
    public AdapterSnapshot Get(string provider) => snapshots.TryGetValue(provider, out var value)
        ? value : throw new KeyNotFoundException("Adapter is unavailable or invalid.");
    public bool TryGet(string provider, out AdapterSnapshot snapshot) => snapshots.TryGetValue(provider, out snapshot!);

    public AdapterSnapshot FromJson(string json, string? expectedProvider = null)
        => Parse(json, expectedProvider, false);

    private AdapterSnapshot Parse(string json, string? expectedProvider, bool pinned)
    {
        if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) throw new InvalidDataException("Adapter configuration is too large.");
        var definition = JsonSerializer.Deserialize<AdapterDefinition>(json, Json) ?? throw new InvalidDataException("Adapter configuration is empty.");
        string[] errors;
        try
        {
            errors = Validate(definition, expectedProvider);
            // Old queued revisions retain the validation rules under which they were accepted.
            // New semantic checks apply to drafts and hot-loaded configuration only.
            if (!pinned) errors = errors.Concat(AdapterSemanticValidation.Validate(definition, MediaRoot)).Distinct().ToArray();
        }
        catch (Exception error) when (error is NullReferenceException or ArgumentNullException)
        { throw new InvalidDataException("Adapter configuration collections and definitions cannot be null."); }
        if (errors.Length > 0) throw new InvalidDataException(string.Join(" ", errors));
        definition = definition with { AssetPolicies = definition.AssetPolicies.ToDictionary(pair => pair.Key, pair =>
        {
            var policy = pair.Value;
            if (policy.Storage != "local" || policy.ImageProfile == "original") return policy with { ResolvedImageProfile = null };
            var profile = pinned && policy.ResolvedImageProfile is not null ? policy.ResolvedImageProfile
                : encoderRegistry.Resolve(policy.ImageProfile!);
            ImageEncodingProfile.Validate(profile);
            if (profile.Name != policy.ImageProfile) throw new InvalidDataException("Captured image profile name does not match its policy.");
            return policy with { ResolvedImageProfile = profile };
        }, StringComparer.Ordinal) };
        var canonical = JsonSerializer.Serialize(definition, Json);
        var version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(definition, version, canonical);
    }

    public string[] ValidateJson(string json, string? expectedProvider = null)
    {
        try { FromJson(json, expectedProvider); return []; }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        { return [error is JsonException invalid ? (invalid.Path ?? "$") + ": invalid JSON, null value or unknown configuration field." : error.Message]; }
    }

    public AdapterSnapshot FromPinnedJson(string json, string provider, string version)
    {
        var storedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (!string.Equals(storedHash, version, StringComparison.Ordinal))
            throw new InvalidDataException("Pinned adapter version does not match its stored JSON.");
        var parsed = Parse(json, provider, true);
        // New optional contract fields can change current canonical serialization. A queued
        // task must keep the exact original JSON and version whose integrity we just checked.
        return new(parsed.Definition, version, json);
    }

    public async Task<AdapterSnapshot> SaveAsync(string provider, string json, CancellationToken ct, string? expectedVersion = null)
    {
        await reload.WaitAsync(ct);
        try
        {
            var snapshot = FromJson(json, provider);
            if (expectedVersion == "*")
            {
                EnsureInsideRoot(Path.Combine(Root, provider));
                ConfigurationConflictException.Check("*", File.Exists(Path.Combine(Root, provider, "adapter.json")) ? "*" : null);
            }
            else if (expectedVersion is not null)
                ConfigurationConflictException.Check(expectedVersion, await CurrentVersionAsync(provider, ct));
            return await SaveLockedAsync(snapshot, ct);
        }
        finally { reload.Release(); }
    }

    public async Task<AdapterSnapshot> SetEnabledAsync(string provider, bool enabled, CancellationToken ct, string? expectedVersion = null)
    {
        if (!ValidProvider(provider)) throw new ArgumentException("Invalid adapter provider.");
        await reload.WaitAsync(ct);
        try
        {
            var current = Get(provider);
            var path = Path.Combine(Root, provider, "adapter.json");
            EnsureInsideRoot(Path.GetDirectoryName(path)!);
            // Read-modify-write shares the same lock as PUT and reload, so enabled cannot
            // restore an older client copy of unrelated adapter settings.
            if (File.Exists(path)) current = FromJson(await File.ReadAllTextAsync(path, ct), provider);
            ConfigurationConflictException.Check(expectedVersion, current.Version);
            var changed = FromJson(JsonSerializer.Serialize(current.Definition with { Enabled = enabled }, Json), provider);
            return await SaveLockedAsync(changed, ct);
        }
        finally { reload.Release(); }
    }

    private async Task<string?> CurrentVersionAsync(string provider, CancellationToken ct)
    {
        var folder = Path.Combine(Root, provider);
        EnsureInsideRoot(folder);
        var path = Path.Combine(folder, "adapter.json");
        if (!File.Exists(path)) return null;
        try { return FromJson(await File.ReadAllTextAsync(path, ct), provider).Version; }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException)
        { throw new ConfigurationConflictException(null); }
    }

    private async Task<AdapterSnapshot> SaveLockedAsync(AdapterSnapshot snapshot, CancellationToken ct)
    {
        var folder = Path.Combine(Root, snapshot.Provider);
        Directory.CreateDirectory(folder);
        EnsureInsideRoot(folder);
        var path = Path.Combine(folder, "adapter.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, snapshot.Json, new UTF8Encoding(false), ct);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Register(snapshot);
        return snapshot;
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await reload.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Root);
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                var provider = Path.GetFileName(folder);
                if (!ValidProvider(provider)) continue;
                present.Add(provider);
                try
                {
                    EnsureInsideRoot(folder);
                    var path = Path.Combine(folder, "adapter.json");
                    if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Adapter configuration is too large.");
                    var json = await File.ReadAllTextAsync(path, ct);
                    Register(FromJson(json, provider));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
                {
                    var previous = snapshots.GetValueOrDefault(provider);
                    registrations[provider] = new(provider, previous?.Definition.DisplayName ?? provider,
                        previous?.Definition.Enabled ?? false, previous?.Version,
                        [error is JsonException invalid ? (invalid.Path ?? "$") + ": invalid adapter JSON or unknown field."
                            : error is InvalidDataException ? error.Message : error.GetType().Name], DateTime.UtcNow);
                }
            }
            foreach (var provider in registrations.Keys.Where(x => !present.Contains(x)))
            {
                snapshots.TryRemove(provider, out _);
                registrations.TryRemove(provider, out _);
            }
        }
        finally { reload.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReloadAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Adapter scan failed ({ErrorType})", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private void Register(AdapterSnapshot snapshot)
    {
        snapshots[snapshot.Provider] = snapshot;
        registrations[snapshot.Provider] = new(snapshot.Provider, snapshot.Definition.DisplayName,
            snapshot.Definition.Enabled, snapshot.Version, [], DateTime.UtcNow);
    }

    private void EnsureInsideRoot(string folder)
    {
        var info = new DirectoryInfo(folder);
        var resolved = info.Exists ? info.ResolveLinkTarget(true)?.FullName ?? info.FullName : info.FullName;
        if (!Inside(Root, resolved)) throw new InvalidDataException("Adapter folder must be inside the configured adapter directory.");
    }

    public static bool ValidProvider(string value) => Regex.IsMatch(value, "^[a-z][a-z0-9-]{0,23}$", RegexOptions.CultureInvariant);

    public static bool Inside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private string[] Validate(AdapterDefinition definition, string? expectedProvider)
    {
        var errors = new List<string>();
        if (!ValidProvider(definition.Provider) || expectedProvider is not null && definition.Provider != expectedProvider)
            errors.Add("Provider must match its folder and contain 1 to 24 lowercase letters, digits or hyphens.");
        if (definition.ConfigVersion != 1) errors.Add("Only configVersion 1 is supported.");
        if (string.IsNullOrWhiteSpace(definition.DisplayName) || definition.DisplayName.Length > 120) errors.Add("displayName is required and limited to 120 characters.");
        if (definition.Kind is not ("anime" or "game" or "film" or "series" or "book" or "manga" or "music")) errors.Add("Unsupported kind.");
        if (definition.RefreshHours is < 1 or > 8784) errors.Add("refreshHours must be between 1 and 8784.");
        if (!Https(definition.BaseUrl)) errors.Add("baseUrl must be an HTTPS origin.");
        if (string.IsNullOrWhiteSpace(definition.SourceUrlTemplate)) errors.Add("sourceUrlTemplate is required.");
        if (definition.AllowedHosts.Any(host => string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) != UriHostNameType.Dns))
            errors.Add("allowedHosts must contain DNS host names.");
        if (definition.RateLimit is null || !double.IsFinite(definition.RateLimit.RequestsPerSecond)
            || definition.RateLimit.RequestsPerSecond is <= 0 or > 1000 || definition.RateLimit.RequestsPerMinute is < 1 or > 60_000)
            errors.Add("Rate limits must be positive and within supported bounds.");
        if (definition.Destinations?.Files is not { Directory.Length: > 0 } destination || !Inside(MediaRoot, Path.IsPathRooted(destination.Directory) ? destination.Directory : Path.Combine(MediaRoot, destination.Directory)))
            errors.Add("destinations.files.directory must be within Importer:MediaRoot.");
        if (definition.Destinations?.Database is { } database)
        {
            errors.AddRange(MediaImporter.Database.PostgresDatabaseWriter.ValidateDefinition(database));
        }
        if (definition.Requests is null || definition.Mapping is null || definition.AssetPolicies is null || definition.Discovery is null)
            return [.. errors, "requests, mapping, discovery and assetPolicies are required."];
        if (definition.Requests.Count is < 1 or > 32 || !definition.Requests.ContainsKey(definition.Discovery.Request)) errors.Add("The discovery request must exist.");
        if (definition.Discovery.Pagination is null || definition.Discovery.Pagination.Type is not ("page" or "cursor")
            || definition.Discovery.Pagination.PageSize is < 1 or > 10_000 || definition.Discovery.Pagination.StartPage < 1)
            errors.Add("Invalid discovery pagination.");
        if (definition.Discovery.Pagination?.HasMoreWhenMissing is not (null or "fullPage")) errors.Add("Unsupported missing-continuation policy.");
        foreach (var (name, request) in definition.Requests)
        {
            if (request is null) { errors.Add("Request definitions cannot be null."); continue; }
            if (request.Method is not ("GET" or "POST")) errors.Add($"Request {name} has an unsupported method.");
            if (request.MaximumResponseBytes is < 1 or > 16 * 1024 * 1024) errors.Add($"Request {name} has an invalid response bound.");
            if (request.BaseUrl is not null && !Https(request.BaseUrl)) errors.Add($"Request {name} has an invalid baseUrl.");
            if (!request.Path.StartsWith('/') || request.Path.StartsWith("//")) errors.Add($"Request {name} path must be relative to an origin.");
            foreach (var header in request.Headers.Keys)
                if (header.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) errors.Add("Host and Cookie headers are unsupported.");
            CheckPath(request.ResponseRootPath, errors); CheckPath(request.AvailabilityPath, errors);
            CheckPath(request.IdentityPath, errors); CheckPath(request.FilterPath, errors);
            CheckPath(request.SuccessPath, errors);
            foreach (var path in request.IdentityPaths) CheckPath(path, errors);
            foreach (var path in request.RequiredPaths) CheckPath(path, errors);
            if (request.Supplement is { } supplement)
                foreach (var path in supplement.WhenMissingPaths.Concat(supplement.WhenMissingGroups.SelectMany(x => x)).Concat(supplement.FillPaths).Concat(supplement.CaptureVariables.Values)) CheckPath(path, errors);
            if (request.AlternativeVariables.Count > 1 || request.AlternativeVariables.Values.Any(x => x is null || x.Length is < 1 or > 16))
                errors.Add("A request supports one alternative variable with up to 16 values.");
        }
        CheckPath(definition.Discovery.ItemsPath, errors); CheckPath(definition.Discovery.ExternalIdPath, errors);
        CheckPath(definition.Discovery.NamePath, errors); CheckPath(definition.Discovery.ModifiedPath, errors);
        CheckPath(definition.Discovery.Pagination?.NextCursorPath, errors); CheckPath(definition.Discovery.Pagination?.HasMorePath, errors);
        foreach (var field in definition.Mapping.Fields.Values)
        {
            if (field is null || !definition.Requests.ContainsKey(field.Request)) { errors.Add("Mapped fields must name an existing request."); continue; }
            CheckPath(field.Path, errors); CheckPath(field.ItemsPath, errors);
            foreach (var path in field.Paths) CheckPath(path, errors);
            if (field.Selection is not (null or "firstNonEmpty" or "all")) errors.Add("Unsupported field selection.");
            if (field.Transform is not (null or "plainText" or "title" or "year" or "number" or "releaseStatus")) errors.Add("Unsupported field transform.");
        }
        foreach (var (role, mapping) in definition.Mapping.Assets)
        {
            if (!Roles.Contains(role) || mapping is null) { errors.Add("Unsupported asset role."); continue; }
            foreach (var source in mapping.Sources.Length > 0 ? mapping.Sources : [mapping])
            {
                if (source is null || !definition.Requests.ContainsKey(source.Request)) { errors.Add("Asset sources must name an existing request."); continue; }
                CheckPath(source.ItemsPath, errors); CheckPath(source.UrlPath, errors); CheckPath(source.TitlePath, errors); CheckPath(source.FormatPath, errors);
                foreach (var path in source.ItemsPaths.Concat(source.UrlPaths).Concat(source.FilePaths)) CheckPath(path, errors);
                if (source.UrlBase is not null && !Https(source.UrlBase)) errors.Add("Asset URL bases must use HTTPS.");
            }
        }
        foreach (var (role, policy) in definition.AssetPolicies)
        {
            if (!Roles.Contains(role) || policy is null) { errors.Add("Unsupported asset policy."); continue; }
            if (policy.Storage is not ("local" or "link") || policy.MaxItems is < 1) errors.Add("Asset policy storage or maxItems is invalid.");
            if (policy.Storage == "local" && !ValidProfile(policy.ImageProfile)) errors.Add("Local images require original or an installed image profile name.");
            if (role == "video" && policy.Storage == "local") errors.Add("Video assets support link storage.");
        }
        return errors.Distinct().ToArray();
    }

    public static readonly string[] Roles = ["poster", "hero", "logo", "header", "background", "screenshot", "video"];

    public static bool ValidProfile(string? profile) => ImageEncodingProfile.ValidName(profile);

    public override void Dispose()
    {
        if (encoders is null) encoderRegistry.Dispose();
        base.Dispose();
    }

    private static bool Https(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static void CheckPath(string? path, List<string> errors)
    {
        if (path is null) return;
        try { AdapterJsonPath.Validate(Regex.Replace(path, @"\{\{[^{}]+\}\}", "0", RegexOptions.CultureInvariant)); }
        catch (InvalidDataException) { errors.Add("Unsupported JSON path in mapping."); }
    }
}
