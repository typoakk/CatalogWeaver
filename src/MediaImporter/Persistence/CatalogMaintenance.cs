using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace MediaImporter;

public sealed record SweepStatistics(int Candidates, int Removed, int Batches, long ElapsedMilliseconds);

public sealed partial class StandalonePersistence
{
    public SweepStatistics? LastSweep { get; private set; }

    public async Task<int> SweepOrphansAsync(TimeSpan minimumAge, CancellationToken ct)
    {
        if (minimumAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumAge));
        var stopwatch = Stopwatch.StartNew();
        var cutoff = clock.GetUtcNow().UtcDateTime - minimumAge;
        var batchSize = Math.Clamp(configuration.GetValue("Importer:CleanupBatchSize", 256), 1, 2000);
        var allowUnknown = configuration.GetValue<bool>("Importer:CleanupUnregisteredFiles");
        var candidates = 0;
        var removed = 0;
        var batches = 0;
        string lastHash = "", lastPath = "";
        while (true)
        {
            var batch = new List<CatalogFileReference>();
            await using (var query = source.CreateCommand("""
                SELECT owned.hash,owned.path FROM (
                    SELECT "Hash" hash,"RelativePath" path,"CreatedUtc" created FROM "ImporterFiles"
                    UNION ALL SELECT "Hash","RelativePath","CreatedUtc" FROM "ImporterPublications" WHERE "OwnsFile"
                ) owned
                WHERE owned.created<=@cutoff AND (owned.hash COLLATE "C",owned.path COLLATE "C")>(@hash COLLATE "C",@path COLLATE "C")
                    AND NOT EXISTS(SELECT 1 FROM "ImporterAssets" a WHERE a."Hash"=owned.hash AND a."RelativePath"=owned.path)
                    AND NOT EXISTS(SELECT 1 FROM "ImporterExportedFiles" e WHERE e."Hash"=owned.hash AND e."RelativePath"=owned.path)
                    AND NOT EXISTS(SELECT 1 FROM "ImporterPublications" p WHERE p."Hash"=owned.hash AND p."RelativePath"=owned.path
                        AND (p."ExpiresUtc">now() OR EXISTS(SELECT 1 FROM "ImporterJobs" j
                            WHERE j."Id"=p."JobId" AND j."LeaseToken"=p."LeaseToken" AND j."State"='importing' AND j."LeaseUntilUtc">now())))
                GROUP BY owned.hash,owned.path ORDER BY owned.hash COLLATE "C",owned.path COLLATE "C" LIMIT @limit
                """))
            {
                query.Parameters.AddWithValue("cutoff", cutoff);
                query.Parameters.AddWithValue("hash", lastHash);
                query.Parameters.AddWithValue("path", lastPath);
                query.Parameters.AddWithValue("limit", batchSize);
                await using var reader = await query.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) batch.Add(new(reader.GetString(0), reader.GetString(1)));
            }
            if (batch.Count == 0) break;
            lastHash = batch[^1].Hash;
            lastPath = batch[^1].RelativePath;
            candidates += batch.Count;
            batches++;
            removed += await DeleteBatchAsync(batch.ToArray(), cutoff, false, ct);
        }
        // This legacy opt-in is deliberately separate: the default only considers recorded owned files.
        if (allowUnknown && Directory.Exists(MediaRoot))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            var batch = new List<CatalogFileReference>(batchSize);
            foreach (var path in Directory.EnumerateFiles(MediaRoot, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                var hash = Path.GetFileName(path);
                if (!IsHash(hash) || File.GetLastWriteTimeUtc(path) > cutoff) continue;
                var relative = Path.GetRelativePath(MediaRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith(".staging/", StringComparison.Ordinal)) continue;
                batch.Add(new(hash, relative));
                if (batch.Count < batchSize) continue;
                candidates += batch.Count;
                batches++;
                removed += await DeleteBatchAsync(batch.ToArray(), cutoff, true, ct);
                batch.Clear();
            }
            if (batch.Count > 0)
            {
                candidates += batch.Count;
                batches++;
                removed += await DeleteBatchAsync(batch.ToArray(), cutoff, true, ct);
            }
        }
        await using (var prune = source.CreateCommand("""
            DELETE FROM "ImporterPublications" p WHERE p."ExpiresUtc"<=now()
                AND NOT EXISTS(SELECT 1 FROM "ImporterJobs" j WHERE j."Id"=p."JobId" AND j."LeaseToken"=p."LeaseToken"
                    AND j."State"='importing' AND j."LeaseUntilUtc">now())
                AND (NOT p."OwnsFile" OR EXISTS(SELECT 1 FROM "ImporterFiles" f WHERE f."Hash"=p."Hash" AND f."RelativePath"=p."RelativePath"))
            """)) await prune.ExecuteNonQueryAsync(ct);
        LastSweep = new(candidates, removed, batches, stopwatch.ElapsedMilliseconds);
        return removed;
    }

    private async Task<int> DeleteBatchAsync(CatalogFileReference[] files, DateTime? cutoff, bool allowUnknown, CancellationToken ct)
    {
        if (files.Length == 0) return 0;
        var ordered = files.Distinct().OrderBy(file => file.Hash, StringComparer.Ordinal).ThenBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        foreach (var file in ordered) _ = ResolvePath(file);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // One round trip acquires all locks in the same deterministic order used by publication and save.
        await using (var locks = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(hashtextextended('standalone-file:'||f.hash||':'||f.path,0))
            FROM unnest(@hashes,@paths) WITH ORDINALITY f(hash,path,position) ORDER BY f.position
            """, connection, transaction))
        {
            BatchIdentity(locks, ordered);
            await locks.ExecuteNonQueryAsync(ct);
        }
        var eligible = new List<CatalogFileReference>();
        await using (var check = new NpgsqlCommand("""
            SELECT f.hash,f.path FROM unnest(@hashes,@paths) f(hash,path)
            WHERE (@unknown OR EXISTS(SELECT 1 FROM "ImporterFiles" i WHERE i."Hash"=f.hash AND i."RelativePath"=f.path)
                OR EXISTS(SELECT 1 FROM "ImporterPublications" p WHERE p."Hash"=f.hash AND p."RelativePath"=f.path AND p."OwnsFile"))
                AND NOT EXISTS(SELECT 1 FROM "ImporterAssets" a WHERE a."Hash"=f.hash AND a."RelativePath"=f.path)
                AND NOT EXISTS(SELECT 1 FROM "ImporterExportedFiles" e WHERE e."Hash"=f.hash AND e."RelativePath"=f.path)
                AND NOT EXISTS(SELECT 1 FROM "ImporterPublications" p WHERE p."Hash"=f.hash AND p."RelativePath"=f.path
                    AND (p."ExpiresUtc">now() OR EXISTS(SELECT 1 FROM "ImporterJobs" j WHERE j."Id"=p."JobId"
                        AND j."LeaseToken"=p."LeaseToken" AND j."State"='importing' AND j."LeaseUntilUtc">now())))
            """, connection, transaction))
        {
            BatchIdentity(check, ordered);
            check.Parameters.AddWithValue("unknown", allowUnknown);
            await using var reader = await check.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) eligible.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        var deleted = new List<CatalogFileReference>();
        var removed = 0;
        foreach (var file in eligible)
        {
            var path = ResolvePath(file);
            var exists = File.Exists(path);
            if (exists && cutoff is { } age && File.GetLastWriteTimeUtc(path) > age) continue;
            await images.DeletePublishedAsync(file.RelativePath, ct);
            deleted.Add(file);
            if (exists) removed++;
        }
        if (deleted.Count > 0)
        {
            await using var clear = new NpgsqlCommand("""
                DELETE FROM "ImporterFiles" i USING unnest(@hashes,@paths) f(hash,path) WHERE i."Hash"=f.hash AND i."RelativePath"=f.path;
                DELETE FROM "ImporterPublications" p USING unnest(@hashes,@paths) f(hash,path) WHERE p."Hash"=f.hash AND p."RelativePath"=f.path;
                """, connection, transaction);
            BatchIdentity(clear, deleted.ToArray());
            await clear.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return removed;
    }

    private static void BatchIdentity(NpgsqlCommand command, CatalogFileReference[] files)
    {
        command.Parameters.AddWithValue("hashes", files.Select(file => file.Hash).ToArray());
        command.Parameters.AddWithValue("paths", files.Select(file => file.RelativePath).ToArray());
    }
}
