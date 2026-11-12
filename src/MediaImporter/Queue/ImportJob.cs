namespace MediaImporter;

public sealed record ImportJob(Guid Id, string Provider, long ExternalId, string AdapterVersion,
    string AdapterJson, string State, string Stage, int Attempts, DateTime DueUtc,
    DateTime? LeaseUntilUtc, Guid? LeaseToken, string? ResultJson, DateTime CreatedUtc,
    DateTime UpdatedUtc, DateTime? FirstCompletedUtc);

public sealed record DiscoveryRun(Guid Id, string Provider, string AdapterVersion, string AdapterJson,
    string? Cursor, int? MaximumItems, int Discovered, int Pages, string State, int Attempts,
    DateTime DueUtc, DateTime? LeaseUntilUtc, Guid? LeaseToken, string? Error, DateTime UpdatedUtc,
    DiscoveryEntry[]? PendingItems = null, string? PendingCursor = null);

public sealed record QueueSummary(string Provider, string State, long Count);
