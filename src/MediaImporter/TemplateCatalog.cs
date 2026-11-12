using System.Text.Json;
using System.Text.Json.Nodes;
using MediaImporter.Images;

namespace MediaImporter;

public sealed record TemplateComparison(string Id, string? CurrentVersion, JsonElement Shipped, string[] ChangedPaths);

/// <summary>Shipped templates are embedded so persistent working volumes never hide release updates.</summary>
public static class TemplateCatalog
{
    public static TemplateComparison Adapter(string id, AdapterRegistry registry)
    {
        var shipped = Read(id, "adapter.json");
        var active = registry.TryGet(id, out var current) ? current : null;
        JsonNode Canonical(string json)
        {
            var definition = JsonSerializer.Deserialize<AdapterDefinition>(json, AdapterRegistry.Json)!;
            definition = definition with { AssetPolicies = definition.AssetPolicies.ToDictionary(pair => pair.Key,
                pair => pair.Value with { ResolvedImageProfile = null }) };
            return JsonSerializer.SerializeToNode(definition, AdapterRegistry.Json)!;
        }
        return new(id, active?.Version, JsonSerializer.Deserialize<JsonElement>(shipped),
            active is null ? ["$"] : Differences(Canonical(active.Json), Canonical(shipped)));
    }

    public static TemplateComparison Encoder(string id, EncoderRegistry registry)
    {
        var shipped = Read(id, "encoder.json");
        var active = registry.List().SingleOrDefault(value => value.Id == id);
        JsonNode Canonical(string json) => JsonSerializer.SerializeToNode(
            JsonSerializer.Deserialize<EncoderModuleDefinition>(json, AdapterRegistry.Json), AdapterRegistry.Json)!;
        return new(id, active?.Version, JsonSerializer.Deserialize<JsonElement>(shipped), active?.Version is null
            ? ["$"] : Differences(Canonical(registry.GetJson(id)), Canonical(shipped)));
    }

    private static string Read(string id, string file)
    {
        if (!AdapterRegistry.ValidProvider(id)) throw new ArgumentException("Invalid template ID.");
        var assembly = typeof(TemplateCatalog).Assembly;
        var name = assembly.GetManifestResourceNames().SingleOrDefault(name => name.Replace('\\', '/') == "CatalogWeaver.Templates." + id + "/" + file)
            ?? throw new KeyNotFoundException("This release does not ship that template.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] Differences(JsonNode? current, JsonNode? shipped)
    {
        var paths = new List<string>();
        void Compare(JsonNode? left, JsonNode? right, string path)
        {
            if (JsonNode.DeepEquals(left, right)) return;
            if (left is JsonObject a && right is JsonObject b)
            {
                foreach (var key in a.Select(pair => pair.Key).Union(b.Select(pair => pair.Key)).Order(StringComparer.Ordinal))
                    Compare(a[key], b[key], path + "." + key);
            }
            else paths.Add(path);
        }
        Compare(current, shipped, "$");
        return paths.ToArray();
    }
}
