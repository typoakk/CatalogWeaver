using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaImporter;
using MediaImporter.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class ConfigurationReliabilityTests
{
    [Theory]
    [InlineData("field", "$.mapping.fields.titel")]
    [InlineData("shape", "$.requests.item.responseKind")]
    [InlineData("variable", "$.requests.item.path")]
    [InlineData("body", "$.requests.poster.body.query")]
    [InlineData("staging", "$.destinations.files.directory")]
    [InlineData("nested-staging", "$.destinations.files.directory")]
    [InlineData("title", "$.mapping.fields.title")]
    [InlineData("discovery-scope", "$.requests.discover.path")]
    [InlineData("secret", "$.sourceUrlTemplate")]
    public void Draft_semantics_are_rejected_at_the_configuration_field(string change, string expectedPath)
    {
        using var fixture = new Fixture();
        var draft = JsonNode.Parse(fixture.AdapterJson)!;
        switch (change)
        {
            case "field": draft["mapping"]!["fields"]!["titel"] = new JsonObject { ["path"] = "$.name" }; break;
            case "shape": draft["requests"]!["item"]!["responseKind"] = "objetc"; break;
            case "variable": draft["requests"]!["item"]!["path"] = "/items/{{notDeclared}}"; break;
            case "body": draft["requests"]!["poster"]!["body"]!["query"] = "{{notDeclared}}"; break;
            case "staging": draft["destinations"]!["files"]!["directory"] = ".staging"; break;
            case "nested-staging": draft["destinations"]!["files"]!["directory"] = ".staging/published"; break;
            case "title": draft["mapping"]!["fields"]!.AsObject().Remove("title"); break;
            case "discovery-scope": draft["requests"]!["discover"]!["path"] = "/list/{{externalId}}"; break;
            case "secret": draft["sourceUrlTemplate"] = "https://example.org/{{secret:PrivateKey}}"; break;
        }
        Assert.Contains(fixture.Adapters.ValidateJson(draft.ToJsonString()), error => error.Contains(expectedPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Known_variables_and_request_secrets_are_allowed_without_requiring_the_secret_during_validation()
    {
        using var fixture = new Fixture();
        var draft = JsonNode.Parse(fixture.AdapterJson)!;
        draft["variables"] = new JsonObject { ["source:locale"] = "en" };
        draft["requests"]!["item"]!["headers"] = new JsonObject { ["Authorization"] = "Bearer {{secret:Private:Key}}" };
        draft["requests"]!["item"]!["path"] = "/items/{{externalId}}/{{source:locale|upper}}";
        Assert.Empty(fixture.Adapters.ValidateJson(draft.ToJsonString()));
    }

    [Fact]
    public void A_request_reused_by_discovery_and_item_must_be_valid_in_both_variable_scopes()
    {
        using var fixture = new Fixture();
        var draft = JsonNode.Parse(fixture.AdapterJson)!;
        draft["mapping"]!["fields"]!["title"]!["request"] = "discover";
        Assert.Contains(fixture.Adapters.ValidateJson(draft.ToJsonString()), error => error.Contains("$.requests.discover.query.page", StringComparison.Ordinal));
    }

    [Fact]
    public void New_draft_semantics_do_not_invalidate_an_integrity_checked_older_queued_revision()
    {
        using var fixture = new Fixture();
        var old = JsonNode.Parse(fixture.Adapters.FromJson(fixture.AdapterJson).Json)!;
        old["mapping"]!["fields"]!["titel"] = new JsonObject { ["request"] = "item", ["path"] = "$.name" };
        var json = old.ToJsonString();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        Assert.NotEmpty(fixture.Adapters.ValidateJson(json));
        var restored = fixture.Adapters.FromPinnedJson(json, "shikimori", hash);
        Assert.Equal(json, restored.Json);
        Assert.Equal(hash, restored.Version);
    }

    [Fact]
    public async Task Concurrent_adapter_clients_cannot_overwrite_a_newer_revision_or_restore_it_through_enabled()
    {
        using var fixture = new Fixture();
        var first = await fixture.Adapters.SaveAsync("shikimori", fixture.AdapterJson, default);
        var a = JsonNode.Parse(first.Json)!; a["displayName"] = "Client A";
        var b = JsonNode.Parse(first.Json)!; b["displayName"] = "Client B";
        var attempts = await Task.WhenAll(Attempt(a), Attempt(b));
        Assert.Single(attempts, value => value);
        Assert.Single(attempts, value => !value);
        var current = fixture.Adapters.Get("shikimori");
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => fixture.Adapters.SetEnabledAsync("shikimori", true, default, first.Version));
        var enabled = await fixture.Adapters.SetEnabledAsync("shikimori", true, default, current.Version);
        Assert.True(enabled.Definition.Enabled);
        Assert.Equal(current.Definition.DisplayName, enabled.Definition.DisplayName);
        var stored = fixture.Adapters.FromJson(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "adapters", "shikimori", "adapter.json")));
        Assert.Equal(enabled.Version, stored.Version);

        async Task<bool> Attempt(JsonNode draft)
        {
            try { await fixture.Adapters.SaveAsync("shikimori", draft.ToJsonString(), default, first.Version); return true; }
            catch (ConfigurationConflictException) { return false; }
        }
    }

    [Fact]
    public async Task An_external_valid_file_edit_also_invalidates_a_clients_expected_revision()
    {
        using var fixture = new Fixture();
        var saved = await fixture.Adapters.SaveAsync("shikimori", fixture.AdapterJson, default);
        var edited = JsonNode.Parse(saved.Json)!; edited["displayName"] = "External edit";
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "adapters", "shikimori", "adapter.json"), edited.ToJsonString());
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => fixture.Adapters.SaveAsync("shikimori", saved.Json, default, saved.Version));
    }

    [Fact]
    public async Task Concurrent_encoder_clients_use_the_file_revision_before_publishing_the_catalog()
    {
        using var fixture = new Fixture();
        var original = new EncoderModuleDefinition { Id = "custom", DisplayName = "Custom", Format = "Png", Profiles = [new() { Name = "custom-png" }] };
        var json = JsonSerializer.Serialize(original, AdapterRegistry.Json);
        await fixture.Encoders.SaveAsync("custom", json, default);
        var version = fixture.Encoders.GetVersion("custom");
        var attempts = await Task.WhenAll(Attempt("A"), Attempt("B"));
        Assert.Single(attempts, value => value);
        Assert.Single(attempts, value => !value);
        Assert.NotEqual(version, fixture.Encoders.GetVersion("custom"));
        Assert.Empty(fixture.Encoders.List().Single(value => value.Id == "custom").Errors);

        async Task<bool> Attempt(string displayName)
        {
            try
            {
                await fixture.Encoders.SaveAsync("custom", JsonSerializer.Serialize(original with { DisplayName = displayName }, AdapterRegistry.Json), default, version);
                return true;
            }
            catch (ConfigurationConflictException) { return false; }
        }
    }

    [Fact]
    public async Task Create_only_precondition_cannot_replace_an_existing_configuration()
    {
        using var fixture = new Fixture();
        await fixture.Adapters.SaveAsync("shikimori", fixture.AdapterJson, default, "");
        var changed = JsonNode.Parse(fixture.AdapterJson)!;
        changed["displayName"] = "Must not replace";
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => fixture.Adapters.SaveAsync("shikimori", changed.ToJsonString(), default, ""));
        Assert.Equal("Shikimori", fixture.Adapters.Get("shikimori").Definition.DisplayName);
    }

    [Fact]
    public async Task Concurrent_create_only_clients_have_one_winner_for_adapters_and_encoders()
    {
        using var fixture = new Fixture();
        var adapterAttempts = await Task.WhenAll(CreateAdapter("A"), CreateAdapter("B"));
        Assert.Single(adapterAttempts, value => value);
        Assert.Single(adapterAttempts, value => !value);
        var encoderAttempts = await Task.WhenAll(CreateEncoder("A"), CreateEncoder("B"));
        Assert.Single(encoderAttempts, value => value);
        Assert.Single(encoderAttempts, value => !value);

        async Task<bool> CreateAdapter(string displayName)
        {
            var draft = JsonNode.Parse(fixture.AdapterJson)!; draft["displayName"] = displayName;
            try { await fixture.Adapters.SaveAsync("shikimori", draft.ToJsonString(), default, ""); return true; }
            catch (ConfigurationConflictException) { return false; }
        }
        async Task<bool> CreateEncoder(string displayName)
        {
            var module = new EncoderModuleDefinition { Id = "custom", DisplayName = displayName, Format = "Png", Profiles = [new() { Name = "custom-png" }] };
            try { await fixture.Encoders.SaveAsync("custom", JsonSerializer.Serialize(module, AdapterRegistry.Json), default, ""); return true; }
            catch (ConfigurationConflictException) { return false; }
        }
    }

    [Fact]
    public async Task If_match_wildcard_requires_an_existing_configuration()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => fixture.Adapters.SaveAsync("shikimori", fixture.AdapterJson, default, "*"));
        await fixture.Adapters.SaveAsync("shikimori", fixture.AdapterJson, default);
        await fixture.Adapters.SaveAsync("shikimori", fixture.AdapterJson, default, "*");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "adapters", "shikimori", "adapter.json"), "{");
        await fixture.Adapters.SaveAsync("shikimori", fixture.AdapterJson, default, "*");
        Assert.Empty(fixture.Adapters.ValidateJson(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "adapters", "shikimori", "adapter.json"))));
    }

    [Theory]
    [InlineData("$['*']", "literal-star")]
    [InlineData("$['#0']", "literal-index")]
    [InlineData("$.#0", "literal-index")]
    [InlineData("$['a]b']", "closing-bracket")]
    [InlineData("$['']", "empty-name")]
    [InlineData("$['quo\\'te']", "quoted-name")]
    public void Quoted_jsonpath_members_are_literal_property_names(string path, string expected)
    {
        var document = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["*"] = "literal-star", ["#0"] = "literal-index", ["a]b"] = "closing-bracket", [""] = "empty-name", ["quo'te"] = "quoted-name"
        });
        var selected = Assert.Single(AdapterJsonPath.Select(document, path));
        Assert.Equal(expected, selected.GetString());
    }

    [Fact]
    public void Index_and_wildcard_tokens_keep_their_collection_semantics()
    {
        using var document = JsonDocument.Parse("{\"array\":[10,20],\"object\":{\"a\":1,\"b\":2}}");
        Assert.Equal(20, Assert.Single(AdapterJsonPath.Select(document.RootElement, "$.array[1]")).GetInt32());
        Assert.Equal(2, AdapterJsonPath.Select(document.RootElement, "$.array[*]").Count);
        Assert.Equal(2, AdapterJsonPath.Select(document.RootElement, "$.object.*").Count);
        Assert.Empty(AdapterJsonPath.Select(document.RootElement, "$.array[9]"));
        Assert.Throws<InvalidDataException>(() => AdapterJsonPath.Validate("$[2147483648]"));
        Assert.Throws<InvalidDataException>(() => AdapterJsonPath.Validate("$[-1]"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "catalogweaver-config-" + Guid.NewGuid().ToString("N"));
        public AdapterRegistry Adapters { get; }
        public EncoderRegistry Encoders { get; }
        public string AdapterJson => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "adapters", "shikimori", "adapter.json"));
        public Fixture()
        {
            var encoders = Path.Combine(Root, "encoders");
            foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "encoders"), "encoder.json", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(encoders, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "encoders"), file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Importer:AdaptersPath"] = Path.Combine(Root, "adapters"), ["Importer:EncodersPath"] = encoders, ["Importer:MediaRoot"] = Path.Combine(Root, "media")
            }).Build();
            Encoders = new(config, NullLogger<EncoderRegistry>.Instance);
            Adapters = new(config, NullLogger<AdapterRegistry>.Instance, Encoders);
        }
        public void Dispose()
        {
            Adapters.Dispose(); Encoders.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
