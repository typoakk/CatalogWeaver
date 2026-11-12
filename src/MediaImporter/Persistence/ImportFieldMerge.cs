using System.Text.Json;
using System.Text.Json.Nodes;

namespace MediaImporter;

/// <summary>Only a successful field read is authoritative, including a successful empty value.</summary>
internal static class ImportFieldMerge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Fields = ["title", "originalTitle", "alternativeTitles", "description", "genres",
        "tags", "creator", "year", "rating", "episodes", "releaseStatus", "country", "language"];

    internal static bool Available(AdapterItem item, string field) => !Fields.Contains(field, StringComparer.Ordinal)
        || item.FieldStatus.Count == 0 || item.FieldStatus.GetValueOrDefault(field) == "complete";

    internal static AdapterItem Merge(AdapterItem incoming, AdapterItem? previous)
    {
        // Older stored contracts did not describe field availability. Keep their historic behavior.
        if (previous is null || incoming.FieldStatus.Count == 0) return incoming;
        var current = JsonSerializer.SerializeToNode(incoming, Json)!.AsObject();
        var old = JsonSerializer.SerializeToNode(previous, Json)!.AsObject();
        foreach (var field in Fields)
            if (incoming.FieldStatus.GetValueOrDefault(field) != "complete") current[field] = old[field]?.DeepClone();
        var result = current.Deserialize<AdapterItem>(Json)!;
        if (previous.Metadata is { ValueKind: JsonValueKind.Object } oldMetadata
            && incoming.Metadata is { ValueKind: JsonValueKind.Object } newMetadata)
        {
            var metadata = JsonNode.Parse(oldMetadata.GetRawText())!.AsObject();
            foreach (var property in newMetadata.EnumerateObject()) metadata[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            metadata["country"] = result.Country;
            metadata["language"] = result.Language;
            result = result with { Metadata = JsonSerializer.SerializeToElement(metadata, Json) };
        }
        return result;
    }
}
