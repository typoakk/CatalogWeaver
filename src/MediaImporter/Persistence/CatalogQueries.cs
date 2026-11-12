using System.Text.Json;
using Npgsql;

namespace MediaImporter;

public sealed record StoredImportPage(StoredImportSummary[] Items, string? NextCursor);
public sealed record ExportReservation(CatalogFileReference File, DateTime ReservedUtc, bool LocallyReferenced, Guid Revision);
public sealed record ExportReservationPage(ExportReservation[] Items, string? NextCursor);
public sealed record ExportReleaseSelection(CatalogFileReference File, Guid Revision);
public sealed record ExportReleaseResult(int Released, int Retained);

public sealed partial class StandalonePersistence
{
    private sealed record PageCursor(int Version, string Kind, DateTime At, string Key, string? Path = null);

    public async Task<StoredImportPage> ListPageAsync(int limit, string? cursor, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var after = ParseCursor(cursor, "items");
        var values = new List<StoredImportSummary>();
        await using var command = source.CreateCommand("""
            SELECT "Provider","ExternalId","MediaId","MetadataJson"->>'title',"AdapterVersion","UpdatedUtc"
            FROM "ImporterItems"
            WHERE (@first OR ("Provider" COLLATE "C","ExternalId")>(@key COLLATE "C",@external))
            ORDER BY "Provider" COLLATE "C","ExternalId" LIMIT @limit
            """);
        command.Parameters.AddWithValue("first", after is null);
        command.Parameters.AddWithValue("key", after?.Key ?? "");
        command.Parameters.AddWithValue("external", after is null ? 0L : long.Parse(after.Path!, System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) values.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetDateTime(5)));
        var more = values.Count > limit;
        var page = values.Take(limit).ToArray();
        return new(page, more ? Cursor(new(1, "items", DateTime.UnixEpoch, page[^1].Provider,
            page[^1].ExternalId.ToString(System.Globalization.CultureInfo.InvariantCulture))) : null);
    }

    public async Task<ExportReservationPage> ListExportReservationsAsync(int limit, string? cursor, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var after = ParseCursor(cursor, "exports");
        var values = new List<ExportReservation>();
        await using var command = source.CreateCommand("""
            SELECT e."Hash",e."RelativePath",e."ReservedUtc",EXISTS (
                SELECT 1 FROM "ImporterAssets" a WHERE a."Hash"=e."Hash" AND a."RelativePath"=e."RelativePath"),e."Revision"
            FROM "ImporterExportedFiles" e
            WHERE (@first OR (e."Hash" COLLATE "C",e."RelativePath" COLLATE "C")>(@key COLLATE "C",@path COLLATE "C"))
            ORDER BY e."Hash" COLLATE "C",e."RelativePath" COLLATE "C" LIMIT @limit
            """);
        command.Parameters.AddWithValue("first", after is null);
        command.Parameters.AddWithValue("key", after?.Key ?? "");
        command.Parameters.AddWithValue("path", after?.Path ?? "");
        command.Parameters.AddWithValue("limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) values.Add(new(new(reader.GetString(0), reader.GetString(1)), reader.GetDateTime(2), reader.GetBoolean(3), reader.GetGuid(4)));
        var more = values.Count > limit;
        var page = values.Take(limit).ToArray();
        return new(page, more ? Cursor(new(1, "exports", DateTime.UnixEpoch, page[^1].File.Hash, page[^1].File.RelativePath)) : null);
    }

    /// <summary>Explicit caller attestation is required: arbitrary external schemas cannot be searched reliably for references.</summary>
    public async Task<ExportReleaseResult> ReleaseExportReservationsAsync(ExportReleaseSelection[] files, string confirmation, CancellationToken ct)
    {
        if (confirmation != "external-references-verified-absent")
            throw new InvalidDataException("Confirm that all external references to the selected files have been checked and removed.");
        if (files is null || files.Length is < 1 or > 1000)
            throw new InvalidDataException("Select between 1 and 1000 export reservations.");
        if (files.Any(value => value is null || value.File is null || value.Revision == Guid.Empty))
            throw new InvalidDataException("Each selected reservation requires its reviewed file and revision.");
        var unique = files.DistinctBy(value => value.File).OrderBy(value => value.File.Hash, StringComparer.Ordinal)
            .ThenBy(value => value.File.RelativePath, StringComparer.Ordinal).ToArray();
        foreach (var value in unique) _ = ResolvePath(value.File);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var value in unique) await LockAsync(connection, transaction, FileLock(value.File), ct);
        await using var command = new NpgsqlCommand("""
            DELETE FROM "ImporterExportedFiles" e USING unnest(@hashes,@paths,@revisions) f(hash,path,revision)
            WHERE e."Hash"=f.hash AND e."RelativePath"=f.path AND e."Revision"=f.revision
                AND NOT EXISTS (SELECT 1 FROM "ImporterAssets" a WHERE a."Hash"=f.hash AND a."RelativePath"=f.path)
                AND NOT EXISTS (SELECT 1 FROM "ImporterPublications" p WHERE p."Hash"=f.hash AND p."RelativePath"=f.path
                    AND (p."ExpiresUtc">now() OR EXISTS (SELECT 1 FROM "ImporterJobs" j WHERE j."Id"=p."JobId"
                        AND j."LeaseToken"=p."LeaseToken" AND j."State"='importing' AND j."LeaseUntilUtc">now())))
            """, connection, transaction);
        command.Parameters.AddWithValue("hashes", unique.Select(value => value.File.Hash).ToArray());
        command.Parameters.AddWithValue("paths", unique.Select(value => value.File.RelativePath).ToArray());
        command.Parameters.AddWithValue("revisions", unique.Select(value => value.Revision).ToArray());
        var released = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(released, unique.Length - released);
    }

    private static string Cursor(PageCursor value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, Json))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static PageCursor? ParseCursor(string? text, string kind)
    {
        if (text is null) return null;
        try
        {
            if (text.Length is < 1 or > 4096) throw new FormatException();
            var value = text.Replace('-', '+').Replace('_', '/');
            value = value.PadRight((value.Length + 3) / 4 * 4, '=');
            var parsed = JsonSerializer.Deserialize<PageCursor>(Convert.FromBase64String(value), Json);
            if (parsed is not { Version: 1 } || parsed.Kind != kind || parsed.At.Kind != DateTimeKind.Utc
                || string.IsNullOrEmpty(parsed.Key) || parsed.Key.Length > 1024
                || string.IsNullOrEmpty(parsed.Path)
                || kind == "items" && (!AdapterRegistry.ValidProvider(parsed.Key)
                    || !long.TryParse(parsed.Path, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var external)
                    || external is < 1 or > int.MaxValue)
                || kind == "exports" && !IsHash(parsed.Key)) throw new FormatException();
            return parsed;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new InvalidDataException("Invalid or incompatible pagination cursor."); }
    }
}
