using System.Text.Json;
using System.Text.Json.Serialization;
using MediaImporter.Images;

namespace MediaImporter;

public sealed record AdapterDefinition
{
    public int ConfigVersion { get; init; } = 1;
    public string Provider { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool Enabled { get; init; }
    public string Kind { get; init; } = "";
    public int RefreshHours { get; init; } = 24;
    public string BaseUrl { get; init; } = "";
    public string SourceUrlTemplate { get; init; } = "";
    public string[] AllowedHosts { get; init; } = [];
    public RateLimitDefinition RateLimit { get; init; } = new();
    public DestinationDefinition Destinations { get; init; } = new();
    public DiscoveryDefinition Discovery { get; init; } = new();
    public Dictionary<string, RequestDefinition> Requests { get; init; } = [];
    public MappingDefinition Mapping { get; init; } = new();
    public Dictionary<string, AssetPolicy> AssetPolicies { get; init; } = [];
    public Dictionary<string, string> Variables { get; init; } = [];
}

public sealed record RateLimitDefinition
{
    public double RequestsPerSecond { get; init; } = 1;
    public int RequestsPerMinute { get; init; } = 30;
}

public sealed record DestinationDefinition
{
    public FileDestinationDefinition Files { get; init; } = new();
    public MediaImporter.Database.DatabaseDestination? Database { get; init; }
}

public sealed record FileDestinationDefinition
{
    public string Directory { get; init; } = "";
}

public sealed record DiscoveryDefinition
{
    public string Request { get; init; } = "discover";
    public PaginationDefinition Pagination { get; init; } = new();
    public string ItemsPath { get; init; } = "$";
    public string ExternalIdPath { get; init; } = "$.id";
    public string? NamePath { get; init; }
    public string? ModifiedPath { get; init; }
}

public sealed record PaginationDefinition
{
    public string Type { get; init; } = "page";
    public int StartPage { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public bool StopOnEmpty { get; init; } = true;
    public string? NextCursorPath { get; init; }
    public string? HasMorePath { get; init; }
    public string? HasMoreWhenMissing { get; init; }
    public bool CursorFromLastId { get; init; }
}

public sealed record RequestDefinition
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "";
    public string? BaseUrl { get; init; }
    public Dictionary<string, JsonElement> Query { get; init; } = [];
    public Dictionary<string, string> Headers { get; init; } = [];
    public JsonElement? Body { get; init; }
    public bool Required { get; init; }
    public bool GraphQl { get; init; }
    public int MaximumResponseBytes { get; init; } = 2 * 1024 * 1024;
    public string? ResponseRootPath { get; init; }
    public string? ResponseKind { get; init; }
    public string[] RequiredPaths { get; init; } = [];
    public bool AllowEmptyRoot { get; init; }
    public string? AvailabilityPath { get; init; }
    public string? IdentityPath { get; init; }
    public string[] IdentityPaths { get; init; } = [];
    public string? SuccessPath { get; init; }
    public string[] SuccessValues { get; init; } = ["true"];
    public string? FilterPath { get; init; }
    public string? FilterValue { get; init; }
    public Dictionary<string, string[]> AlternativeVariables { get; init; } = [];
    public RequestSupplement? Supplement { get; init; }
}

public sealed record RequestSupplement
{
    public string[] WhenMissingPaths { get; init; } = [];
    public string[][] WhenMissingGroups { get; init; } = [];
    public string[] FillPaths { get; init; } = [];
    public Dictionary<string, string> Variables { get; init; } = [];
    public Dictionary<string, string> CaptureVariables { get; init; } = [];
}

public sealed record MappingDefinition
{
    public Dictionary<string, FieldMapping> Fields { get; init; } = [];
    public Dictionary<string, AssetMapping> Assets { get; init; } = [];
}

public sealed record FieldMapping
{
    public string Request { get; init; } = "item";
    public string? Path { get; init; }
    public string[] Paths { get; init; } = [];
    public string? ItemsPath { get; init; }
    public string? Selection { get; init; }
    public string? Transform { get; init; }
    public string? Constant { get; init; }
}

public sealed record AssetMapping : AssetSourceDefinition
{
    public AssetSourceDefinition[] Sources { get; init; } = [];
}

public record AssetSourceDefinition
{
    public string Request { get; init; } = "item";
    public string? ItemsPath { get; init; }
    public string[] ItemsPaths { get; init; } = [];
    public string? UrlPath { get; init; }
    public string[] UrlPaths { get; init; } = [];
    public string? TitlePath { get; init; }
    public string[] UrlTemplates { get; init; } = [];
    public bool ResolveRelativeUrls { get; init; }
    public bool UpgradeHttp { get; init; }
    public bool EmitAllUrls { get; init; }
    public bool RecursiveMediaUrls { get; init; }
    public bool RequireUrl { get; init; }
    public string? FormatPath { get; init; }
    public string[] FilePaths { get; init; } = [];
    public string FormatToken { get; init; } = "${FILENAME}";
    public string? UrlBase { get; init; }
    public string? RequiredFormatPrefix { get; init; }
    public string? RequiredFilenamePrefix { get; init; }
}

public sealed record AssetPolicy
{
    public string Storage { get; init; } = "link";
    public string? ImageProfile { get; init; }
    public int? MaxItems { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageEncodingProfile? ResolvedImageProfile { get; init; }
}

public sealed record AdapterSnapshot(AdapterDefinition Definition, string Version, string Json)
{
    public string Provider => Definition.Provider;
}

public sealed record AdapterRegistration(string Provider, string DisplayName, bool Enabled,
    string? Version, string[] Errors, DateTime CheckedUtc);

public class AdapterReadException(string code, bool retryable) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; init; }
    public int? HttpStatusCode { get; init; }
}

public sealed class AdapterUnavailableException() : AdapterReadException("unavailable", false);
public sealed class AdapterExcludedException() : AdapterReadException("excluded", false);
