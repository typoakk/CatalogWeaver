using MediaImporter.Database;

namespace MediaImporter;

public sealed record ImportPreviewPlan
{
    public string MediaId { get; init; } = "";
    public string StateOperation { get; init; } = "insert";
    public Dictionary<string, string> AssetStatus { get; init; } = new(StringComparer.Ordinal);
    public AssetResult[] Assets { get; init; } = [];
    public DatabaseChangePlan? Database { get; init; }
    public ImportFileChange[] Files { get; init; } = [];
    public string[] DeferredRoles { get; init; } = [];
    public string[] Warnings { get; init; } = [];
}

public sealed record ImportFileChange(string Role, int Position, string Operation,
    string SourceUrl, string? RelativePath, string? Hash, string? ContentType, long Bytes,
    string? CurrentRelativePath = null, string? Reason = null);

internal sealed record AssetMergeResult(AssetResult[] Assets, Dictionary<string, string> Status,
    Dictionary<string, string> DeliveryStatus, string[] DeferredRoles);

internal static class ImportAssetMerge
{
    internal static AssetMergeResult Merge(AdapterItem item, IReadOnlyList<AssetResult> attempted,
        IReadOnlyList<AssetResult> previous, Dictionary<string, string> previousStatus, bool preview)
    {
        var current = previous.ToList();
        var effectiveStatus = new Dictionary<string, string>(previousStatus, StringComparer.Ordinal);
        var deliveryStatus = new Dictionary<string, string>(item.AssetStatus, StringComparer.Ordinal);
        var deferred = new List<string>();
        foreach (var (role, status) in item.AssetStatus)
        {
            var roleResults = attempted.Where(asset => asset.Role == role).OrderBy(asset => asset.Position).ToArray();
            var unknown = preview && roleResults.Any(asset => asset.State == "planned");
            if (status == "retry" || roleResults.Any(asset => asset.State == "retry") || unknown)
            {
                deliveryStatus[role] = "retry";
                effectiveStatus[role] = unknown ? previousStatus.GetValueOrDefault(role, "retry") : "retry";
                if (unknown) deferred.Add(role);
                continue;
            }
            var old = current.Where(asset => asset.Role == role).ToArray();
            current.RemoveAll(asset => asset.Role == role);
            foreach (var asset in roleResults)
            {
                var retained = asset.State == "missing"
                    ? old.FirstOrDefault(value => value.Position == asset.Position && value.Hash is not null) : null;
                current.Add(retained is null ? asset : retained with
                {
                    Error = asset.Error, RequestedProfile = asset.RequestedProfile,
                    Warnings = retained.Warnings.Concat(asset.Warnings).Distinct().ToArray()
                });
            }
            effectiveStatus[role] = deliveryStatus[role] = "complete";
        }
        return new(current.OrderBy(asset => asset.Role, StringComparer.Ordinal).ThenBy(asset => asset.Position).ToArray(),
            effectiveStatus, deliveryStatus, deferred.ToArray());
    }
}
