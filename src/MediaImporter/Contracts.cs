using System.Text.Json;

namespace MediaImporter;

public sealed record DiscoveryEntry(long ExternalId, string? Name = null, long? LastModified = null);

public sealed record DiscoveryPage(DiscoveryEntry[] Items, string? NextCursor);

public sealed record AdapterAsset
{
    public string Role { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string Storage { get; init; } = "link";
    public string? ImageProfile { get; init; }
    public string? Title { get; init; }
    public string[] Alternatives { get; init; } = [];
}

public sealed record AdapterItem
{
    public string Provider { get; init; } = "";
    public long ExternalId { get; init; }
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string OriginalTitle { get; init; } = "";
    public string[] AlternativeTitles { get; init; } = [];
    public string Description { get; init; } = "";
    public string[] Genres { get; init; } = [];
    public string[] Tags { get; init; } = [];
    public string Creator { get; init; } = "";
    public int Year { get; init; }
    public double Rating { get; init; }
    public int? Episodes { get; init; }
    public string ReleaseStatus { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public AdapterAsset[] Assets { get; init; } = [];
    public Dictionary<string, string> AssetStatus { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> FieldStatus { get; init; } = new(StringComparer.Ordinal);
    public JsonElement? Metadata { get; init; }
    public string Country { get; init; } = "";
    public string Language { get; init; } = "";
    public string[] Warnings { get; init; } = [];
}

public sealed record AssetResult
{
    public string Role { get; init; } = "";
    public int Position { get; init; }
    public string SourceUrl { get; init; } = "";
    public string? Title { get; init; }
    public string Storage { get; init; } = "link";
    public string State { get; init; } = "remote";
    public string? Hash { get; init; }
    public string? RelativePath { get; init; }
    public string? ContentType { get; init; }
    public long Bytes { get; init; }
    public string? RequestedProfile { get; init; }
    public string? ActualProfile { get; init; }
    public string? Error { get; init; }
    public string[] Warnings { get; init; } = [];
}

public sealed record ImportResult
{
    public string Mode { get; init; } = "import";
    public string Provider { get; init; } = "";
    public long ExternalId { get; init; }
    public string? MediaId { get; init; }
    public string AdapterVersion { get; init; } = "";
    public string Status { get; init; } = "completed";
    public AdapterItem? Item { get; init; }
    public DiscoveryPage? Discovery { get; init; }
    public ImportPreviewPlan? Plan { get; init; }
    public AssetResult[] Assets { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    public string? Error { get; init; }
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}
