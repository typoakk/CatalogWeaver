namespace MediaImporter;

public sealed record ImportJobResponse(Guid Id, string Provider, long ExternalId, string AdapterVersion,
    string State, string Stage, int Attempts, DateTime DueUtc, DateTime? LeaseUntilUtc,
    DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? FirstCompletedUtc, bool RefreshEnabled, ImportResult? Result);
public sealed record DiscoveryResponse(Guid Id, string Provider, string AdapterVersion, string? Cursor,
    int? MaximumItems, int Discovered, int Pages, string State, int Attempts, DateTime DueUtc,
    DateTime? LeaseUntilUtc, string? Error, DateTime UpdatedUtc, int PendingItems);
