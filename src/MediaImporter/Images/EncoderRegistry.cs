using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaImporter.Images;

/// <summary>Loads declarative encoder modules; a scan publishes one coherent catalog.</summary>
public sealed class EncoderRegistry : BackgroundService
{
    private sealed record Module(EncoderModuleDefinition Definition, string Json, string Version, ImageEncodingProfile[] Profiles);
    private sealed record Catalog(Dictionary<string, Module> Modules, Dictionary<string, ImageEncodingProfile> Profiles,
        EncoderRegistration[] Registrations);
    private Catalog catalog = new([], [], []);
    private readonly SemaphoreSlim reload = new(1, 1);
    private readonly ILogger<EncoderRegistry> logger;
    public string Root { get; }

    public EncoderRegistry(IConfiguration configuration, ILogger<EncoderRegistry> logger)
    {
        this.logger = logger;
        Root = Path.GetFullPath(configuration["Importer:EncodersPath"] ?? DefaultRoot);
        ReloadAsync().GetAwaiter().GetResult();
    }

    internal static EncoderRegistry Bundled() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Importer:EncodersPath"] = DefaultRoot }).Build(), NullLogger<EncoderRegistry>.Instance);

    private static string DefaultRoot => Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "encoders"))
        ? Path.Combine(Directory.GetCurrentDirectory(), "encoders") : Path.Combine(AppContext.BaseDirectory, "encoders");

    public EncoderRegistration[] List() => Volatile.Read(ref catalog).Registrations;
    public ImageEncodingProfile[] Profiles() => Volatile.Read(ref catalog).Profiles.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    public ImageEncodingProfile Resolve(string name) => Volatile.Read(ref catalog).Profiles.TryGetValue(name, out var profile)
        ? profile : throw new InvalidDataException("Unknown image profile. Install a valid encoder module or select original.");
    public string GetJson(string id) => Volatile.Read(ref catalog).Modules.TryGetValue(id, out var module)
        ? module.Json : throw new KeyNotFoundException("Encoder module is unavailable or invalid.");

    public string[] ValidateJson(string json, string? expectedId = null)
    {
        try
        {
            var module = Parse(json, expectedId);
            var others = Volatile.Read(ref catalog).Modules.Where(pair => pair.Key != module.Definition.Id)
                .SelectMany(pair => pair.Value.Profiles).Select(profile => profile.Name).ToHashSet(StringComparer.Ordinal);
            if (module.Profiles.Any(profile => others.Contains(profile.Name)))
                return ["Image profile names must be unique across modules."];
            return [];
        }
        catch (Exception error) when (IsModuleFailure(error)) { return [Message(error)]; }
    }

    public async Task SaveAsync(string id, string json, CancellationToken ct)
    {
        await reload.WaitAsync(ct);
        try
        {
            var errors = ValidateJson(json, id);
            if (errors.Length > 0) throw new InvalidDataException(string.Join(" ", errors));
            var module = Parse(json, id);
            Directory.CreateDirectory(Root);
            var folder = Path.Combine(Root, id);
            if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Encoder module directories cannot be symbolic links.");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "encoder.json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, module.Json, new UTF8Encoding(false), ct);
                File.Move(temporary, path, true);
            }
            finally { File.Delete(temporary); }
        }
        finally { reload.Release(); }
        await ReloadAsync(ct);
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await reload.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Root);
            var previous = Volatile.Read(ref catalog);
            var modules = new Dictionary<string, Module>(StringComparer.Ordinal);
            var registrations = new Dictionary<string, EncoderRegistration>(StringComparer.Ordinal);
            foreach (var folder in Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var id = Path.GetFileName(folder);
                if (!AdapterRegistry.ValidProvider(id)) continue;
                Module? module = null;
                string[] errors = [];
                try
                {
                    var info = new DirectoryInfo(folder);
                    var resolved = info.ResolveLinkTarget(true)?.FullName ?? info.FullName;
                    var path = Path.Combine(folder, "encoder.json");
                    if (!AdapterRegistry.Inside(Root, resolved) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Encoder modules must use regular files inside EncodersPath.");
                    if (new FileInfo(path).Length > 128 * 1024)
                        throw new InvalidDataException("Encoder module exceeds the 128 KiB limit.");
                    module = Parse(await File.ReadAllTextAsync(path, ct), id);
                }
                catch (Exception error) when (IsModuleFailure(error))
                {
                    errors = [Message(error)];
                    previous.Modules.TryGetValue(id, out module);
                }
                if (module is not null) modules[id] = module;
                registrations[id] = new(id, module?.Definition.DisplayName ?? id, module?.Version,
                    module?.Profiles.Select(value => value.Name).ToArray() ?? [], errors, DateTime.UtcNow);
            }

            var duplicates = modules.Values.SelectMany(module => module.Profiles)
                .GroupBy(profile => profile.Name, StringComparer.Ordinal).Where(group => group.Count() > 1).ToArray();
            if (duplicates.Length > 0)
            {
                foreach (var id in duplicates.SelectMany(group => group.Select(profile => profile.Module)).Distinct())
                    registrations[id] = registrations[id] with { Errors = ["Image profile names must be unique across modules; previous catalog remains active."] };
                // No partially applied module set, including collisions with retained good revisions.
                foreach (var id in registrations.Keys.ToArray())
                {
                    previous.Modules.TryGetValue(id, out var active);
                    registrations[id] = registrations[id] with { Version = active?.Version,
                        Profiles = active?.Profiles.Select(profile => profile.Name).ToArray() ?? [] };
                }
                Volatile.Write(ref catalog, previous with { Registrations = registrations.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray() });
                return;
            }
            Volatile.Write(ref catalog, new(modules,
                modules.Values.SelectMany(module => module.Profiles).ToDictionary(profile => profile.Name, StringComparer.Ordinal),
                registrations.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray()));
        }
        finally { reload.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ReloadAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Encoder scan failed ({ErrorType})", error.GetType().Name); }
        }
    }

    private static Module Parse(string json, string? expectedId)
    {
        if (Encoding.UTF8.GetByteCount(json) > 128 * 1024) throw new InvalidDataException("Encoder module exceeds the 128 KiB limit.");
        var definition = JsonSerializer.Deserialize<EncoderModuleDefinition>(json, AdapterRegistry.Json)
            ?? throw new InvalidDataException("Encoder module is empty.");
        if (definition.ConfigVersion != 1 || !AdapterRegistry.ValidProvider(definition.Id)
            || expectedId is not null && definition.Id != expectedId || string.IsNullOrWhiteSpace(definition.DisplayName)
            || definition.DisplayName.Length > 120 || definition.Driver != "magick" || definition.Profiles is null)
            throw new InvalidDataException("Invalid encoder module identity, version or driver.");
        ImageEncodingProfile.ValidateDefines(definition.Defines);
        var contentType = ImageEncodingProfile.ContentTypeFor(definition.Format);
        var profiles = new Dictionary<string, ImageEncodingProfile>(StringComparer.Ordinal);
        if (definition.QualityRange is { } range)
        {
            if (!ImageEncodingProfile.ValidName(range.Prefix) || range.Prefix.Length > 60
                || range.Minimum < 1 || range.Maximum > 100 || range.Minimum > range.Maximum)
                throw new InvalidDataException("Encoder quality range must be within 1 through 100.");
            for (var quality = range.Minimum; quality <= range.Maximum; quality++)
                AddProfile(new() { Name = range.Prefix + quality.ToString(System.Globalization.CultureInfo.InvariantCulture), Quality = quality });
        }
        if (definition.Profiles.Length > 100) throw new InvalidDataException("Too many encoder profiles.");
        foreach (var profile in definition.Profiles) AddProfile(profile);
        if (profiles.Count == 0) throw new InvalidDataException("Encoder module must declare at least one profile.");
        var canonical = JsonSerializer.Serialize(definition, AdapterRegistry.Json);
        return new(definition, canonical, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), profiles.Values.ToArray());

        void AddProfile(EncoderProfileDefinition profile)
        {
            if (profile is null || !ImageEncodingProfile.ValidName(profile.Name) || profile.Name == "original"
                || profile.Quality is < 1 or > 100 || profiles.ContainsKey(profile.Name))
                throw new InvalidDataException("Invalid or duplicate image profile name or quality.");
            ImageEncodingProfile.ValidateDefines(profile.Defines);
            var defines = new Dictionary<string, string>(definition.Defines, StringComparer.Ordinal);
            foreach (var (key, value) in profile.Defines) defines[key] = value;
            ImageEncodingProfile.ValidateDefines(defines);
            var resolved = new ImageEncodingProfile(profile.Name, definition.Id, definition.Driver,
                definition.Format, contentType, profile.Quality, defines, "", definition.RejectAlpha);
            profiles.Add(profile.Name, resolved with { Version = ImageEncodingProfile.Hash(resolved) });
        }
    }

    private static bool IsModuleFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or JsonException or InvalidDataException or ArgumentException or ImageMagick.MagickException
        or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException;
    private static string Message(Exception error) => error is JsonException
        ? "Invalid encoder JSON or an unknown configuration field." : error is InvalidDataException ? error.Message : error.GetType().Name;
}
