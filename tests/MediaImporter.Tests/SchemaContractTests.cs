using System.Text.Json;
using MediaImporter;
using MediaImporter.Database;
using MediaImporter.Images;

/// <summary>Prevents published configuration schemas from silently drifting from the actual JSON DTOs.</summary>
public sealed class SchemaContractTests
{
    [Fact]
    public void Adapter_schema_describes_every_configuration_property_and_no_unknown_properties()
    {
        using var schema = Load("adapter.schema.json");
        Matches<AdapterDefinition>(schema.RootElement);
        var properties = schema.RootElement.GetProperty("properties");
        Matches<RateLimitDefinition>(properties.GetProperty("rateLimit"));
        Matches<DestinationDefinition>(properties.GetProperty("destinations"));
        Matches<FileDestinationDefinition>(properties.GetProperty("destinations").GetProperty("properties").GetProperty("files"));
        Matches<MappingDefinition>(properties.GetProperty("mapping"));
        var definitions = schema.RootElement.GetProperty("$defs");
        Matches<DiscoveryDefinition>(definitions.GetProperty("discovery"));
        Matches<PaginationDefinition>(definitions.GetProperty("pagination"));
        Matches<RequestDefinition>(definitions.GetProperty("request"));
        Matches<RequestSupplement>(definitions.GetProperty("supplement"));
        Matches<FieldMapping>(definitions.GetProperty("field"));
        Matches<AssetMapping>(definitions.GetProperty("assetMapping"));
        Matches<AssetSourceDefinition>(definitions.GetProperty("assetSource"));
        Matches<AssetPolicy>(definitions.GetProperty("policy"));
        Matches<ImageEncodingProfile>(definitions.GetProperty("resolvedProfile"));
        var fields = schema.RootElement.GetProperty("properties").GetProperty("mapping").GetProperty("properties").GetProperty("fields");
        Assert.False(fields.GetProperty("additionalProperties").GetBoolean());
        Assert.True(fields.GetProperty("properties").TryGetProperty("title", out _));
        Assert.False(fields.GetProperty("properties").TryGetProperty("titel", out _));
        Assert.Contains(fields.GetProperty("required").EnumerateArray(), value => value.GetString() == "title");
    }

    [Fact]
    public void Encoder_and_database_schemas_describe_the_recursive_runtime_configuration_contracts()
    {
        using var encoder = Load("encoder.schema.json");
        Matches<EncoderModuleDefinition>(encoder.RootElement);
        Matches<EncoderProfileDefinition>(encoder.RootElement.GetProperty("$defs").GetProperty("profile"));
        Matches<EncoderQualityRange>(encoder.RootElement.GetProperty("$defs").GetProperty("qualityRange"));
        using var database = Load("database.schema.json");
        Matches<DatabaseDestination>(database.RootElement);
        var definitions = database.RootElement.GetProperty("$defs");
        Matches<DatabaseTableMapping>(definitions.GetProperty("table"));
        Matches<DatabaseValue>(definitions.GetProperty("value"));
        Matches<DatabaseLookup>(definitions.GetProperty("lookup"));
        Matches<DatabaseCondition>(definitions.GetProperty("condition"));
    }

    [Fact]
    public void All_published_schema_references_resolve_within_the_distributed_schema_set()
    {
        var names = new[] { "adapter.schema.json", "encoder.schema.json", "database.schema.json" };
        var schemas = names.ToDictionary(name => name, Load, StringComparer.Ordinal);
        try
        {
            foreach (var (name, document) in schemas)
            {
                Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
                Walk(document.RootElement, name);
            }
            void Walk(JsonElement value, string current)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    if (value.TryGetProperty("$ref", out var reference))
                    {
                        var parts = reference.GetString()!.Split('#', 2);
                        var target = schemas[parts[0].Length == 0 ? current : parts[0]].RootElement;
                        if (parts.Length == 2)
                            foreach (var segment in parts[1].Split('/').Skip(1))
                                target = target.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
                        Assert.Equal(JsonValueKind.Object, target.ValueKind);
                    }
                    foreach (var property in value.EnumerateObject()) Walk(property.Value, current);
                }
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var item in value.EnumerateArray()) Walk(item, current);
            }
        }
        finally { foreach (var document in schemas.Values) document.Dispose(); }
    }

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", name)));
    private static void Matches<T>(JsonElement definition)
    {
        Assert.False(definition.GetProperty("additionalProperties").GetBoolean());
        var expected = typeof(T).GetProperties().Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Order(StringComparer.Ordinal);
        var actual = definition.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }
}
