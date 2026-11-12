using System.Text.Json;
using System.Text.RegularExpressions;
using MediaImporter.Database;
using MediaImporter.Images;
using Npgsql;
using NpgsqlTypes;

namespace MediaImporter;

public sealed record StoredImportSummary(string Provider, long ExternalId, string MediaId,
    string Title, string AdapterVersion, DateTime UpdatedUtc);

/// <summary>
/// Persists normalized imports in the tool's own database. A configured target database
/// receives a separate idempotent delivery while the source job's lease is fenced.
/// </summary>
public sealed partial class StandalonePersistence(NpgsqlDataSource source, IConfiguration configuration,
    TimeProvider clock, ImagePipeline images, PostgresDatabaseWriter databaseWriter) : IImportPersistence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string MediaRoot => Path.GetFullPath(configuration["Importer:MediaRoot"] ?? "/data/media");

    public async Task<PersistenceResult> SaveAsync(AdapterItem imported, IReadOnlyList<AssetResult> results,
        string adapterVersion, CancellationToken ct, ImportJob? job = null, DatabaseDestination? destination = null)
    {
        Validate(imported, results, adapterVersion);
        if (destination is not null)
        {
            var errors = await databaseWriter.ValidateAsync(destination, ct);
            if (errors.Length != 0) throw new InvalidDataException(string.Join(" ", errors));
        }
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (job is not null)
        {
            await using var fence = new NpgsqlCommand("""
                SELECT 1 FROM "ImporterJobs" WHERE "Id"=@id AND "LeaseToken"=@token AND "State"='importing' FOR UPDATE
                """, connection, transaction);
            fence.Parameters.AddWithValue("id", job.Id);
            fence.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, (object?)job.LeaseToken ?? DBNull.Value);
            if (await fence.ExecuteScalarAsync(ct) is null)
                throw new OperationCanceledException("Import job lease is no longer current.", ct);
        }
        await LockAsync(connection, transaction, "standalone-item:" + imported.Provider + ":" + imported.ExternalId, ct);
        string mediaId = imported.Provider + "-" + imported.ExternalId;
        var effectiveStatus = new Dictionary<string, string>(StringComparer.Ordinal);
        var current = new List<AssetResult>();
        var created = true;
        await using (var existing = new NpgsqlCommand("""
            SELECT "MediaId", "AssetStatusJson"::text, "MetadataJson"::text FROM "ImporterItems" WHERE "Provider"=@provider AND "ExternalId"=@external
            """, connection, transaction))
        {
            Identity(existing, imported.Provider, imported.ExternalId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                mediaId = reader.GetString(0);
                effectiveStatus = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1), Json) ?? [];
                imported = ImportFieldMerge.Merge(imported, JsonSerializer.Deserialize<AdapterItem>(reader.GetString(2), Json));
                created = false;
            }
        }
        await using (var existing = new NpgsqlCommand("""
            SELECT "AssetJson"::text FROM "ImporterAssets" WHERE "Provider"=@provider AND "ExternalId"=@external ORDER BY "Role", "Position"
            """, connection, transaction))
        {
            Identity(existing, imported.Provider, imported.ExternalId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) current.Add(JsonSerializer.Deserialize<AssetResult>(reader.GetString(0), Json)!);
        }
        var oldFiles = References(current).ToHashSet();
        var merge = ImportAssetMerge.Merge(imported, results, current, effectiveStatus, false);
        effectiveStatus = merge.Status;
        var deliveryStatus = merge.DeliveryStatus;
        var effective = merge.Assets;
        var references = References(effective).Distinct().OrderBy(file => file.Hash, StringComparer.Ordinal)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        foreach (var file in references)
        {
            await LockAsync(connection, transaction, FileLock(file), ct);
            var path = ResolvePath(file);
            var asset = effective.First(value => value.Hash == file.Hash && value.RelativePath == file.RelativePath);
            if (!File.Exists(path)) throw new IOException("An imported image is missing before commit.");
            if (new FileInfo(path).Length != asset.Bytes) throw new InvalidDataException("Imported image length changed before commit.");
        }
        var warnings = imported.Warnings.Concat(results.SelectMany(asset => asset.Warnings)).Distinct().ToArray();
        if (destination is not null)
        {
            mediaId = await databaseWriter.ResolveMediaIdAsync(destination, imported, mediaId, ct);
            ValidateMediaId(mediaId);
            // A successful target commit followed by a lost state connection must never make media
            // eligible for blind orphan deletion. Reservations use their own table/connection and
            // deliberately do not acquire the file advisory locks held by this transaction.
            await ReserveExportedFilesAsync(oldFiles.Concat(references).Distinct(), ct);
            var written = await databaseWriter.WriteAsync(destination, imported with { AssetStatus = deliveryStatus }, effective, mediaId, ct,
                new DatabaseExecutionContext { AdapterVersion = adapterVersion, AssetStatus = effectiveStatus, Warnings = warnings });
            mediaId = written.MediaId ?? mediaId;
        }
        ValidateMediaId(mediaId);
        var normalized = imported with
        {
            AssetStatus = effectiveStatus,
            Assets = effective.Select(asset => new AdapterAsset
            {
                Role = asset.Role, SourceUrl = asset.SourceUrl, Storage = asset.Storage,
                ImageProfile = asset.RequestedProfile, Title = asset.Title
            }).ToArray()
        };
        await using (var save = new NpgsqlCommand("""
            INSERT INTO "ImporterItems" ("Provider","ExternalId","MediaId","AdapterVersion","MetadataJson","AssetStatusJson","WarningsJson","UpdatedUtc")
            VALUES (@provider,@external,@media,@version,@metadata,@status,@warnings,@now)
            ON CONFLICT ("Provider","ExternalId") DO UPDATE SET
                "MediaId"=EXCLUDED."MediaId", "AdapterVersion"=EXCLUDED."AdapterVersion", "MetadataJson"=EXCLUDED."MetadataJson",
                "AssetStatusJson"=EXCLUDED."AssetStatusJson", "WarningsJson"=EXCLUDED."WarningsJson", "UpdatedUtc"=EXCLUDED."UpdatedUtc"
            """, connection, transaction))
        {
            Identity(save, imported.Provider, imported.ExternalId);
            save.Parameters.AddWithValue("media", mediaId);
            save.Parameters.AddWithValue("version", adapterVersion);
            AddJson(save, "metadata", normalized); AddJson(save, "status", effectiveStatus); AddJson(save, "warnings", warnings);
            save.Parameters.AddWithValue("now", clock.GetUtcNow());
            await save.ExecuteNonQueryAsync(ct);
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM \"ImporterAssets\" WHERE \"Provider\"=@provider AND \"ExternalId\"=@external", connection, transaction))
        {
            Identity(delete, imported.Provider, imported.ExternalId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        foreach (var asset in effective)
        {
            await using var save = new NpgsqlCommand("""
                INSERT INTO "ImporterAssets" ("Provider","ExternalId","Role","Position","AssetJson","Hash","RelativePath")
                VALUES (@provider,@external,@role,@position,@json,@hash,@path)
                """, connection, transaction);
            Identity(save, imported.Provider, imported.ExternalId);
            save.Parameters.AddWithValue("role", asset.Role); save.Parameters.AddWithValue("position", asset.Position);
            AddJson(save, "json", asset);
            save.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, (object?)asset.Hash ?? DBNull.Value);
            save.Parameters.AddWithValue("path", NpgsqlDbType.Varchar, (object?)asset.RelativePath ?? DBNull.Value);
            await save.ExecuteNonQueryAsync(ct);
        }
        foreach (var file in references)
        {
            var asset = effective.First(value => value.Hash == file.Hash && value.RelativePath == file.RelativePath);
            await using var save = new NpgsqlCommand("""
                INSERT INTO "ImporterFiles" ("Hash","RelativePath","ContentType","Bytes","CreatedUtc") VALUES (@hash,@path,@type,@bytes,@now)
                ON CONFLICT ("Hash","RelativePath") DO UPDATE SET "ContentType"=EXCLUDED."ContentType", "Bytes"=EXCLUDED."Bytes"
                """, connection, transaction);
            FileIdentity(save, file); save.Parameters.AddWithValue("type", asset.ContentType!);
            save.Parameters.AddWithValue("bytes", asset.Bytes); save.Parameters.AddWithValue("now", clock.GetUtcNow());
            await save.ExecuteNonQueryAsync(ct);
        }
        await ClearPublicationsAsync(connection, transaction, references, job, ct);
        await transaction.CommitAsync(ct);
        var retainedFiles = references.ToHashSet();
        return new PersistenceResult(mediaId, created,
            oldFiles.Concat(References(results)).Where(file => !retainedFiles.Contains(file)).Distinct().ToArray())
        { Item = normalized, Assets = effective, AssetStatus = effectiveStatus };
    }

    public async Task CleanupAsync(PersistenceResult result, CancellationToken ct)
    {
        foreach (var file in result.CleanupFiles.OrderBy(file => file.Hash, StringComparer.Ordinal)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal))
            await DeleteUnreferencedAsync(file, null, ct);
    }

    public async Task<ImportPreviewPlan> PlanAsync(AdapterItem imported, IReadOnlyList<AssetResult> results,
        string adapterVersion, CancellationToken ct, DatabaseDestination? destination = null)
    {
        Validate(imported, results, adapterVersion, true);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(ct);
        var mediaId = imported.Provider + "-" + imported.ExternalId;
        var created = true;
        var oldStatus = new Dictionary<string, string>(StringComparer.Ordinal);
        var previous = new List<AssetResult>();
        await using (var command = new NpgsqlCommand("""
            SELECT "MediaId","AssetStatusJson"::text,"MetadataJson"::text FROM "ImporterItems" WHERE "Provider"=@provider AND "ExternalId"=@external
            """, connection, transaction))
        {
            Identity(command, imported.Provider, imported.ExternalId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                mediaId = reader.GetString(0);
                oldStatus = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1), Json) ?? [];
                imported = ImportFieldMerge.Merge(imported, JsonSerializer.Deserialize<AdapterItem>(reader.GetString(2), Json));
                created = false;
            }
        }
        await using (var command = new NpgsqlCommand("""
            SELECT "AssetJson"::text FROM "ImporterAssets" WHERE "Provider"=@provider AND "ExternalId"=@external ORDER BY "Role","Position"
            """, connection, transaction))
        {
            Identity(command, imported.Provider, imported.ExternalId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) previous.Add(JsonSerializer.Deserialize<AssetResult>(reader.GetString(0), Json)!);
        }
        var merge = ImportAssetMerge.Merge(imported, results, previous, oldStatus, true);
        var warnings = imported.Warnings.Concat(results.SelectMany(asset => asset.Warnings))
            .Concat(merge.DeferredRoles.Select(role => "plan_requires_image_download:" + role)).Distinct().ToArray();
        DatabaseChangePlan? databasePlan = null;
        if (destination is not null)
        {
            databasePlan = await databaseWriter.PlanAsync(destination,
                imported with { AssetStatus = merge.DeliveryStatus }, merge.Assets, mediaId, ct,
                new DatabaseExecutionContext { AdapterVersion = adapterVersion, AssetStatus = merge.Status, Warnings = warnings });
            mediaId = databasePlan.MediaId;
        }
        ValidateMediaId(mediaId);
        var changes = new List<ImportFileChange>();
        var plannedFiles = new HashSet<CatalogFileReference>();
        foreach (var asset in results.Where(asset => asset.Storage == "local"))
        {
            var old = previous.FirstOrDefault(value => value.Role == asset.Role && value.Position == asset.Position);
            var retained = merge.Assets.FirstOrDefault(value => value.Role == asset.Role && value.Position == asset.Position);
            var operation = asset.State switch
            {
                "planned" => "requires_download",
                "retry" or "missing" when retained?.Hash is not null => "retain",
                "retry" => "retry",
                "missing" => "missing",
                _ when asset.Hash is not null && File.Exists(ResolvePath(new(asset.Hash, asset.RelativePath!))) => "reuse",
                _ when asset.Hash is not null => "publish",
                _ => "unresolved"
            };
            if (asset.Hash is not null && asset.RelativePath is not null
                && !plannedFiles.Add(new(asset.Hash, asset.RelativePath)) && operation == "publish")
                operation = "reuse_planned";
            changes.Add(new(asset.Role, asset.Position, operation, asset.SourceUrl, asset.RelativePath,
                asset.Hash, asset.ContentType, asset.Bytes, old?.RelativePath,
                asset.State == "planned" ? "Image processing was not requested; no existing material will be cleared." : asset.Error));
        }
        var retainedFiles = References(merge.Assets).ToHashSet();
        foreach (var old in previous.Where(asset => asset.Hash is not null && asset.RelativePath is not null)
            .Where(asset => !retainedFiles.Contains(new(asset.Hash!, asset.RelativePath!)))
            .DistinctBy(asset => (asset.Hash, asset.RelativePath)))
        {
            await using var references = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM "ImporterExportedFiles" WHERE "Hash"=@hash AND "RelativePath"=@path),
                    EXISTS (SELECT 1 FROM "ImporterAssets" WHERE "Hash"=@hash AND "RelativePath"=@path
                        AND NOT ("Provider"=@provider AND "ExternalId"=@external))
                """, connection, transaction);
            FileIdentity(references, new(old.Hash!, old.RelativePath!)); Identity(references, imported.Provider, imported.ExternalId);
            await using var reader = await references.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var exported = reader.GetBoolean(0); var shared = reader.GetBoolean(1);
            changes.Add(new(old.Role, old.Position, exported || destination is not null ? "retain_exported" : shared ? "retain_shared" : "cleanup_after_commit",
                old.SourceUrl, old.RelativePath, old.Hash, old.ContentType, old.Bytes, Reason: destination is not null && !exported
                    ? "The external destination will reserve previous managed files before delivery." : null));
        }
        await transaction.CommitAsync(ct);
        return new ImportPreviewPlan
        {
            Item = imported, MediaId = mediaId, StateOperation = created ? "insert" : "update", AssetStatus = merge.Status,
            Assets = merge.Assets, Database = databasePlan, Files = changes.ToArray(), DeferredRoles = merge.DeferredRoles,
            Warnings = warnings.Concat(databasePlan?.Warnings ?? []).Distinct().ToArray()
        };
    }

    public Task<int> SweepOrphansAsync(CancellationToken ct) => SweepOrphansAsync(TimeSpan.FromHours(24), ct);

    public async Task<ImportResult?> ReadAsync(string mediaId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(ct);
        AdapterItem item; string version; string[] warnings; DateTime updated;
        await using (var command = new NpgsqlCommand("""
            SELECT "MetadataJson"::text,"AdapterVersion","WarningsJson"::text,"UpdatedUtc" FROM "ImporterItems" WHERE "MediaId"=@media
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("media", mediaId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            item = JsonSerializer.Deserialize<AdapterItem>(reader.GetString(0), Json)!;
            version = reader.GetString(1); warnings = JsonSerializer.Deserialize<string[]>(reader.GetString(2), Json)!;
            updated = reader.GetDateTime(3);
        }
        var assets = new List<AssetResult>();
        await using (var command = new NpgsqlCommand("""
            SELECT "AssetJson"::text FROM "ImporterAssets" WHERE "Provider"=@provider AND "ExternalId"=@external ORDER BY "Role","Position"
            """, connection, transaction))
        {
            Identity(command, item.Provider, item.ExternalId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) assets.Add(JsonSerializer.Deserialize<AssetResult>(reader.GetString(0), Json)!);
        }
        await transaction.CommitAsync(ct);
        return new ImportResult { Mode = "stored", MediaId = mediaId, Provider = item.Provider,
            ExternalId = item.ExternalId, AdapterVersion = version, Item = item, Assets = assets.ToArray(),
            Status = item.AssetStatus.Values.Any(value => value == "retry") || item.FieldStatus.Values.Any(value => value == "retry")
                || warnings.Any(value => value.StartsWith("supplement:", StringComparison.Ordinal)) ? "partial" : "completed",
            Warnings = warnings, UpdatedUtc = updated };
    }

    public async Task<StoredImportSummary[]> ListAsync(int limit, CancellationToken ct)
    {
        var values = new List<StoredImportSummary>();
        await using var command = source.CreateCommand("""
            SELECT "Provider","ExternalId","MediaId","MetadataJson"->>'title',"AdapterVersion","UpdatedUtc"
            FROM "ImporterItems" ORDER BY "UpdatedUtc" DESC,"MediaId" COLLATE "C" LIMIT @limit
            """);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) values.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetDateTime(5)));
        return values.ToArray();
    }

    private async Task ReserveExportedFilesAsync(IEnumerable<CatalogFileReference> files, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var file in files)
        {
            await using var reserve = new NpgsqlCommand("""
                INSERT INTO "ImporterExportedFiles" ("Hash","RelativePath") VALUES (@hash,@path)
                ON CONFLICT ("Hash","RelativePath") DO UPDATE SET "Revision"=gen_random_uuid(),"ReservedUtc"=now()
                """, connection, transaction);
            FileIdentity(reserve, file);
            await reserve.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task<bool> DeleteUnreferencedAsync(CatalogFileReference file, DateTime? cutoff, CancellationToken ct)
    {
        return await DeleteBatchAsync([file], cutoff, false, ct) != 0;
    }

    private string ResolvePath(CatalogFileReference file)
    {
        if (!IsHash(file.Hash) || string.IsNullOrWhiteSpace(file.RelativePath)
            || Path.IsPathRooted(file.RelativePath) || file.RelativePath.Contains('\\'))
            throw new InvalidDataException("Invalid stored file path.");
        var segments = file.RelativePath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or "..") || segments[^1] != file.Hash)
            throw new InvalidDataException("Invalid stored file path.");
        var path = Path.GetFullPath(Path.Combine(MediaRoot, Path.Combine(segments)));
        if (!AdapterRegistry.Inside(MediaRoot, path)) throw new InvalidDataException("Stored file escaped the media root.");
        var current = MediaRoot;
        foreach (var segment in segments.Prepend(""))
        {
            if (segment.Length > 0) current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Stored image cannot traverse symbolic links.");
        }
        return path;
    }

    private static IEnumerable<CatalogFileReference> References(IEnumerable<AssetResult> assets) =>
        assets.Where(asset => asset.Hash is not null && asset.RelativePath is not null)
            .Select(asset => new CatalogFileReference(asset.Hash!, asset.RelativePath!));
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string FileLock(CatalogFileReference file) => "standalone-file:" + file.Hash + ":" + file.RelativePath;
    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string identity, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", connection, transaction);
        command.Parameters.AddWithValue("key", identity); await command.ExecuteNonQueryAsync(ct);
    }
    private static void Identity(NpgsqlCommand command, string provider, long externalId)
    { command.Parameters.AddWithValue("provider", provider); command.Parameters.AddWithValue("external", externalId); }
    private static void FileIdentity(NpgsqlCommand command, CatalogFileReference file)
    { command.Parameters.AddWithValue("hash", file.Hash); command.Parameters.AddWithValue("path", file.RelativePath); }
    private static void AddJson<T>(NpgsqlCommand command, string parameter, T value) =>
        command.Parameters.AddWithValue(parameter, NpgsqlDbType.Jsonb, JsonSerializer.Serialize(value, Json));
    private static void ValidateMediaId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            throw new InvalidDataException("Resolved media identity must contain 1 to 64 characters.");
    }
    private static void Validate(AdapterItem item, IReadOnlyList<AssetResult> assets, string version, bool preview = false)
    {
        if (!ProviderName().IsMatch(item.Provider) || item.ExternalId is < 1 or > int.MaxValue
            || string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Kind)
            || !double.IsFinite(item.Rating) || item.Rating is < 0 or > 10 || version.Length is < 1 or > 64
            || !Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var sourceUrl) || sourceUrl.Scheme != "https" || sourceUrl.UserInfo.Length != 0)
            throw new InvalidDataException("Invalid normalized item.");
        if (item.AssetStatus.Any(pair => !RoleName().IsMatch(pair.Key) || pair.Value is not ("complete" or "retry")))
            throw new InvalidDataException("Invalid asset status.");
        if (item.FieldStatus.Any(pair => pair.Value is not ("complete" or "retry")))
            throw new InvalidDataException("Invalid field status.");
        foreach (var asset in assets)
        {
            if (!RoleName().IsMatch(asset.Role) || asset.Position < 0 || asset.Storage is not ("local" or "link")
                || asset.State is not ("stored" or "remote" or "retry" or "missing") && !(preview && asset.State == "planned") || asset.Bytes < 0
                || !Uri.TryCreate(asset.SourceUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0)
                throw new InvalidDataException("Invalid asset result.");
            if (asset.Hash is not null)
            {
                if (!IsHash(asset.Hash) || asset.RelativePath is null || string.IsNullOrWhiteSpace(asset.ContentType)
                    || asset.Storage != "local" || asset.Bytes < 1 || Path.IsPathRooted(asset.RelativePath)
                    || asset.RelativePath.Contains('\\') || asset.RelativePath.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")
                    || asset.RelativePath.Split('/')[^1] != asset.Hash)
                    throw new InvalidDataException("Invalid stored image result.");
            }
            else if (asset.RelativePath is not null || asset.State == "stored")
                throw new InvalidDataException("A stored image requires a content hash and relative path.");
        }
        if (assets.GroupBy(asset => (asset.Role, asset.Position)).Any(group => group.Count() != 1))
            throw new InvalidDataException("Asset positions must be unique within each role.");
    }
    [GeneratedRegex("\\A[a-z][a-z0-9-]{0,23}\\z", RegexOptions.CultureInvariant)] private static partial Regex ProviderName();
    [GeneratedRegex("\\A[a-z][a-z0-9_]{0,31}\\z", RegexOptions.CultureInvariant)] private static partial Regex RoleName();
}
