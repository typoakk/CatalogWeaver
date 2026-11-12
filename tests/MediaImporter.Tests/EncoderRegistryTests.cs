using System.Text.Json;
using System.Text.Json.Nodes;
using ImageMagick;
using MediaImporter.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaImporter.Tests;

public sealed class EncoderRegistryTests
{
    [Fact]
    public void Bundled_modules_preserve_old_avif_names_and_offer_other_formats()
    {
        using var fixture = new Fixture();
        foreach (var id in new[] { "avif", "webp", "jpeg", "png" })
            Assert.Contains(fixture.Encoders.List(), module => module.Id == id);
        Assert.All(fixture.Encoders.List(), module => Assert.Empty(module.Errors));
        Assert.Equal(1, fixture.Encoders.Resolve("avif1").Quality);
        Assert.Equal(100, fixture.Encoders.Resolve("avif100").Quality);
        Assert.Equal("image/webp", fixture.Encoders.Resolve("webp80").ContentType);
        Assert.Equal("true", fixture.Encoders.Resolve("webp-lossless").Defines["webp:lossless"]);
        Assert.Equal("image/jpeg", fixture.Encoders.Resolve("jpeg85").ContentType);
        Assert.Equal("image/png", fixture.Encoders.Resolve("png-lossless").ContentType);
    }

    [Fact]
    public async Task New_module_is_discovered_without_restart_and_can_be_selected_in_an_adapter()
    {
        using var fixture = new Fixture();
        await fixture.Encoders.StartAsync(default);
        try
        {
            var directory = Path.Combine(fixture.Encoders.Root, "custom");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "encoder.json"), CustomModule().ToJsonString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!fixture.Encoders.Profiles().Any(profile => profile.Name == "custom-webp"))
                await Task.Delay(100, timeout.Token);
            var adapter = JsonNode.Parse(fixture.AdapterJson)!;
            adapter["assetPolicies"]!["poster"]!["imageProfile"] = "custom-webp";
            Assert.Empty(fixture.Adapters.ValidateJson(adapter.ToJsonString()));
            var snapshot = fixture.Adapters.FromJson(adapter.ToJsonString());
            Assert.Equal(71, snapshot.Definition.AssetPolicies["poster"].ResolvedImageProfile!.Quality);
        }
        finally { await fixture.Encoders.StopAsync(default); }
    }

    [Fact]
    public async Task Invalid_replacement_keeps_good_module_and_a_fixed_replacement_gets_a_new_version()
    {
        using var fixture = new Fixture();
        var original = fixture.Encoders.Resolve("avif80");
        var path = Path.Combine(fixture.Encoders.Root, "avif", "encoder.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        await File.WriteAllTextAsync(path, "{");
        await fixture.Encoders.ReloadAsync();
        Assert.Equal(original.Version, fixture.Encoders.Resolve("avif80").Version);
        Assert.NotEmpty(fixture.Encoders.List().Single(module => module.Id == "avif").Errors);
        json["defines"]!["heic:speed"] = "7";
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await fixture.Encoders.ReloadAsync();
        Assert.NotEqual(original.Version, fixture.Encoders.Resolve("avif80").Version);
        Assert.Empty(fixture.Encoders.List().Single(module => module.Id == "avif").Errors);
    }

    [Fact]
    public async Task Queued_revision_keeps_encoding_instructions_after_a_module_changes_or_is_removed()
    {
        using var fixture = new Fixture();
        var pinned = fixture.Adapters.FromJson(fixture.AdapterJson);
        var profile = pinned.Definition.AssetPolicies["poster"].ResolvedImageProfile!;
        var path = Path.Combine(fixture.Encoders.Root, "avif", "encoder.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["defines"]!["heic:speed"] = "7";
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await fixture.Encoders.ReloadAsync();
        Assert.NotEqual(pinned.Version, fixture.Adapters.FromJson(fixture.AdapterJson).Version);
        var restored = fixture.Adapters.FromPinnedJson(pinned.Json, pinned.Provider, pinned.Version);
        Assert.Equal(profile.Version, restored.Definition.AssetPolicies["poster"].ResolvedImageProfile!.Version);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
        await fixture.Encoders.ReloadAsync();
        Assert.Throws<InvalidDataException>(() => fixture.Encoders.Resolve("avif80"));
        restored = fixture.Adapters.FromPinnedJson(pinned.Json, pinned.Provider, pinned.Version);
        Assert.Equal("6", restored.Definition.AssetPolicies["poster"].ResolvedImageProfile!.Defines["heic:speed"]);
    }

    [Fact]
    public void Pre_module_queued_json_still_runs_with_its_original_hash()
    {
        using var fixture = new Fixture();
        var oldJson = fixture.AdapterJson;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(oldJson)));
        var pinned = fixture.Adapters.FromPinnedJson(oldJson, "shikimori", hash);
        Assert.Equal(oldJson, pinned.Json);
        Assert.Equal(hash, pinned.Version);
        Assert.Equal(80, pinned.Definition.AssetPolicies["poster"].ResolvedImageProfile!.Quality);
    }

    [Fact]
    public async Task Duplicate_names_do_not_shadow_an_existing_module()
    {
        using var fixture = new Fixture();
        var custom = CustomModule();
        custom["profiles"]![0]!["name"] = "avif80";
        Assert.NotEmpty(fixture.Encoders.ValidateJson(custom.ToJsonString()));
        var directory = Path.Combine(fixture.Encoders.Root, "custom");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "encoder.json"), custom.ToJsonString());
        await fixture.Encoders.ReloadAsync();
        Assert.Equal("avif", fixture.Encoders.Resolve("avif80").Module);
        Assert.NotEmpty(fixture.Encoders.List().Single(module => module.Id == "custom").Errors);
    }

    [Theory]
    [InlineData("quality", "0")]
    [InlineData("quality", "101")]
    [InlineData("name", "\"original\"")]
    [InlineData("name", "\"../../outside\"")]
    public void Invalid_profile_parameters_are_rejected(string property, string value)
    {
        using var fixture = new Fixture();
        var module = CustomModule();
        module["profiles"]![0]![property] = JsonNode.Parse(value);
        Assert.NotEmpty(fixture.Encoders.ValidateJson(module.ToJsonString()));
    }

    [Fact]
    public async Task A_new_runtime_supported_format_needs_only_a_module_file()
    {
        using var fixture = new Fixture();
        var module = CustomModule();
        module["format"] = "Bmp";
        module["profiles"]![0]!["name"] = "custom-bmp";
        await fixture.Encoders.SaveAsync("custom", module.ToJsonString(), default);
        using var image = new MagickImage(MagickColors.Blue, 8, 5);
        var bytes = image.ToByteArray(MagickFormat.Png24);
        using var pipeline = new ImagePipeline(new() { MediaRoot = Path.Combine(fixture.Root, "media"), MinimumFreeBytes = 0 },
            new ImporterImageCodec(), fixture.Encoders);
        var prepared = await pipeline.PrepareAsync("source", "custom-bmp", _ => Task.FromResult(bytes), default);
        Assert.Equal("custom-bmp", prepared.ActualProfile);
        Assert.StartsWith("image/", prepared.ContentType);
        using var output = new MagickImage(await File.ReadAllBytesAsync(prepared.SelectedPath));
        Assert.Equal(8u, output.Width);
        Assert.Equal(5u, output.Height);
        await pipeline.DiscardAsync(prepared, default);
    }

    private static JsonObject CustomModule() => JsonNode.Parse("""
        {"id":"custom","displayName":"Custom WebP","format":"WebP","profiles":[{"name":"custom-webp","quality":71}]}
        """)!.AsObject();

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "catalogweaver-encoders-" + Guid.NewGuid().ToString("N"));
        public EncoderRegistry Encoders { get; }
        public AdapterRegistry Adapters { get; }
        public string AdapterJson => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "adapters", "shikimori", "adapter.json"));

        public Fixture()
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "encoders"), "encoder.json", SearchOption.AllDirectories))
            {
                var target = Path.Combine(Root, "encoders", Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "encoders"), file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Importer:EncodersPath"] = Path.Combine(Root, "encoders"),
                ["Importer:MediaRoot"] = Path.Combine(Root, "media")
            }).Build();
            Encoders = new(configuration, NullLogger<EncoderRegistry>.Instance);
            Adapters = new(configuration, NullLogger<AdapterRegistry>.Instance, Encoders);
        }

        public void Dispose()
        {
            Adapters.Dispose();
            Encoders.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
