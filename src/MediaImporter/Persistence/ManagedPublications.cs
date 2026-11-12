using MediaImporter.Images;
using Npgsql;
using NpgsqlTypes;

namespace MediaImporter;

public sealed partial class StandalonePersistence
{
    /// <summary>Durable intent precedes the filesystem mutation; the shared lock also fences garbage collection.</summary>
    public async Task<PublishedImage> PublishAsync(PreparedImage image, ImportJob? job, CancellationToken ct)
    {
        var relative = Path.GetRelativePath(MediaRoot, Path.Combine(image.DestinationDirectory, image.Hash))
            .Replace(Path.DirectorySeparatorChar, '/');
        var file = new CatalogFileReference(image.Hash, relative);
        var path = ResolvePath(file);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var mutex = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key,0))", connection);
        mutex.Parameters.AddWithValue("key", FileLock(file));
        await mutex.ExecuteNonQueryAsync(ct);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (job is not null)
            {
                await using var fence = new NpgsqlCommand("""
                    SELECT 1 FROM "ImporterJobs" WHERE "Id"=@id AND "LeaseToken"=@token AND "State"='importing' FOR SHARE
                    """, connection, transaction);
                fence.Parameters.AddWithValue("id", job.Id);
                fence.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, (object?)job.LeaseToken ?? DBNull.Value);
                if (await fence.ExecuteScalarAsync(ct) is null) throw new OperationCanceledException("Publication job lease is no longer current.", ct);
            }
            await using var journal = new NpgsqlCommand("""
                INSERT INTO "ImporterPublications"
                    ("Id","Hash","RelativePath","ContentType","Bytes","OwnsFile","JobId","LeaseToken","CreatedUtc","ExpiresUtc")
                VALUES (@id,@hash,@path,@type,@bytes,
                    @new OR EXISTS(SELECT 1 FROM "ImporterFiles" WHERE "Hash"=@hash AND "RelativePath"=@path)
                        OR EXISTS(SELECT 1 FROM "ImporterPublications" WHERE "Hash"=@hash AND "RelativePath"=@path AND "OwnsFile"),
                    @job,@token,@now,@expires)
                """, connection, transaction);
            journal.Parameters.AddWithValue("id", Guid.NewGuid());
            FileIdentity(journal, file);
            journal.Parameters.AddWithValue("type", image.ContentType);
            journal.Parameters.AddWithValue("bytes", image.Bytes);
            journal.Parameters.AddWithValue("new", !File.Exists(path));
            journal.Parameters.AddWithValue("job", NpgsqlDbType.Uuid, (object?)job?.Id ?? DBNull.Value);
            journal.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, (object?)job?.LeaseToken ?? DBNull.Value);
            var now = clock.GetUtcNow();
            journal.Parameters.AddWithValue("now", now);
            journal.Parameters.AddWithValue("expires", now.AddSeconds(Math.Clamp(configuration.GetValue("Importer:ImportTimeoutSeconds", 1800), 30, 86400) + 300));
            await journal.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return await images.PublishAsync(image, ct);
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key,0))", connection);
                release.Parameters.AddWithValue("key", FileLock(file));
                try { await release.ExecuteNonQueryAsync(CancellationToken.None); }
                catch (NpgsqlException) { /* Closing the failed session releases its advisory locks. */ }
            }
        }
    }

    private static async Task ClearPublicationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CatalogFileReference[] files, ImportJob? job, CancellationToken ct)
    {
        if (files.Length == 0) return;
        await using var clear = new NpgsqlCommand("""
            DELETE FROM "ImporterPublications" p USING unnest(@hashes,@paths) f(hash,path)
            WHERE p."Hash"=f.hash AND p."RelativePath"=f.path
                AND (p."JobId" IS NOT DISTINCT FROM @job) AND (p."LeaseToken" IS NOT DISTINCT FROM @token)
            """, connection, transaction);
        clear.Parameters.AddWithValue("hashes", files.Select(file => file.Hash).ToArray());
        clear.Parameters.AddWithValue("paths", files.Select(file => file.RelativePath).ToArray());
        clear.Parameters.AddWithValue("job", NpgsqlDbType.Uuid, (object?)job?.Id ?? DBNull.Value);
        clear.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, (object?)job?.LeaseToken ?? DBNull.Value);
        await clear.ExecuteNonQueryAsync(ct);
    }
}
