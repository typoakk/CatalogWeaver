using MediaImporter.Database;

namespace MediaImporter;

public sealed record CatalogFileReference(string Hash, string RelativePath);

public sealed record PersistenceResult(string MediaId, bool Created, CatalogFileReference[] CleanupFiles)
{
    public AssetResult[] Assets { get; init; } = [];
    public Dictionary<string, string> AssetStatus { get; init; } = new(StringComparer.Ordinal);
}

public interface IImportPersistence
{
    Task<PersistenceResult> SaveAsync(AdapterItem imported, IReadOnlyList<AssetResult> results,
        string adapterVersion, CancellationToken cancellationToken, ImportJob? job = null,
        DatabaseDestination? destination = null);
    Task CleanupAsync(PersistenceResult result, CancellationToken cancellationToken);
    Task<int> SweepOrphansAsync(CancellationToken cancellationToken);
    Task<ImportPreviewPlan> PlanAsync(AdapterItem imported, IReadOnlyList<AssetResult> results,
        string adapterVersion, CancellationToken cancellationToken, DatabaseDestination? destination = null)
        => throw new NotSupportedException("This persistence implementation does not support preview planning.");
}
