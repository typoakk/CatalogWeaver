using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MediaImporter;

public sealed class AdapterRegistryTests
{
    [Fact]
    public void Older_pinned_adapter_keeps_its_original_revision_when_optional_contract_fields_are_added()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Importer:MediaRoot"] = Path.Combine(Path.GetTempPath(), "standalone-media-root") }).Build();
        using var registry = new AdapterRegistry(configuration, NullLogger<AdapterRegistry>.Instance);
        var current = registry.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "adapters", "shikimori", "adapter.json")));
        Assert.Equal(current.Version, registry.FromPinnedJson(current.Json, "shikimori", current.Version).Version);
        var old = JsonNode.Parse(current.Json)!;
        old["destinations"]!.AsObject().Remove("database");
        var originalJson = old.ToJsonString(AdapterRegistry.Json);
        var originalVersion = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(originalJson)));
        var pinned = registry.FromPinnedJson(originalJson, "shikimori", originalVersion);
        Assert.Equal(originalJson, pinned.Json);
        Assert.Equal(originalVersion, pinned.Version);
        Assert.Null(pinned.Definition.Destinations.Database);
        Assert.NotEqual(originalVersion, registry.FromJson(originalJson, "shikimori").Version);
        Assert.Throws<InvalidDataException>(() => registry.FromPinnedJson(originalJson.Replace("Shikimori", "Changed source"), "shikimori", originalVersion));
    }

    [Fact]
    public void Shipped_adapters_use_portable_destinations_inside_the_selected_media_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "standalone-media-root");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Importer:MediaRoot"] = root }).Build();
        using var registry = new AdapterRegistry(configuration, NullLogger<AdapterRegistry>.Instance);
        foreach (var provider in new[] { "shikimori", "steam" })
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "adapters", provider, "adapter.json"));
            var snapshot = registry.FromJson(json, provider);
            Assert.Equal(provider, snapshot.Definition.Destinations.Files.Directory);
            Assert.False(snapshot.Definition.Enabled);
        }
    }

    [Fact]
    public async Task Reload_keeps_last_valid_revision_and_reports_an_invalid_replacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "catalogweaver-adapters-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = Path.Combine(directory, "adapters");
            var providerDirectory = Path.Combine(root, "shikimori");
            Directory.CreateDirectory(providerDirectory);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Importer:AdaptersPath"] = root, ["Importer:MediaRoot"] = "/data/media" }).Build();
            using var registry = new AdapterRegistry(configuration, NullLogger<AdapterRegistry>.Instance);
            var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "adapters", "shikimori", "adapter.json"));
            var configPath = Path.Combine(providerDirectory, "adapter.json");
            await File.WriteAllTextAsync(configPath, original);
            await registry.ReloadAsync();
            var pinned = registry.Get("shikimori");

            await File.WriteAllTextAsync(configPath, "{");
            await registry.ReloadAsync();
            Assert.Equal(pinned.Version, registry.Get("shikimori").Version);
            Assert.NotEmpty(Assert.Single(registry.List()).Errors);

            var updated = JsonNode.Parse(original)!.AsObject();
            updated["displayName"] = "Edited Shikimori";
            await registry.SaveAsync("shikimori", updated.ToJsonString(), CancellationToken.None);
            Assert.NotEqual(pinned.Version, registry.Get("shikimori").Version);
            Assert.Equal("Shikimori", pinned.Definition.DisplayName);
            Assert.Equal("Edited Shikimori", registry.Get("shikimori").Definition.DisplayName);
            Assert.Empty(Assert.Single(registry.List()).Errors);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Invalid_policy_and_escaping_destination_are_rejected_before_activation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Importer:MediaRoot"] = "/data/media" }).Build();
        using var registry = new AdapterRegistry(configuration, NullLogger<AdapterRegistry>.Instance);
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "adapters", "shikimori", "adapter.json")))!;
        config["assetPolicies"]!["poster"]!["maxItems"] = 0;
        config["destinations"]!["files"]!["directory"] = "/data/media/../outside";
        var errors = registry.ValidateJson(config.ToJsonString());
        Assert.Contains(errors, value => value.Contains("maxItems", StringComparison.Ordinal));
        Assert.Contains(errors, value => value.Contains("MediaRoot", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("$..poster")]
    [InlineData("$.items[?(@.x)]")]
    [InlineData("$[invalid]")]
    public void Unsupported_jsonpath_is_explicitly_rejected(string path)
        => Assert.Throws<InvalidDataException>(() => AdapterJsonPath.Validate(path));
}
