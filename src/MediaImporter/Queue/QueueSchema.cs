using Npgsql;

namespace MediaImporter;

internal static class QueueSchema
{
    public const int Version = 1;
    public const string VersionsSql = """
        CREATE TABLE IF NOT EXISTS "ImporterSchemaVersions" (
            "Component" text PRIMARY KEY, "Version" integer NOT NULL,
            "AppliedUtc" timestamptz NOT NULL DEFAULT now()
        );
        """;

    // Validate the actual relations resolved by the connection search_path, including
    // columns used by workers and rate limiting, before accepting a version marker.
    private static readonly Dictionary<string, string> Columns = new(StringComparer.Ordinal)
    {
        ["ImporterSchemaVersions"] = "Component:text Version:int4 AppliedUtc:timestamptz",
        ["ImporterJobs"] = "Id:uuid Provider:varchar ExternalId:int8 AdapterVersion:varchar AdapterJson:text State:varchar Stage:varchar Attempts:int4 DueUtc:timestamptz LeaseUntilUtc:timestamptz LeaseToken:uuid ResultJson:jsonb SourceModified:int8 CreatedUtc:timestamptz UpdatedUtc:timestamptz FirstCompletedUtc:timestamptz RefreshEnabled:bool",
        ["ImporterDiscoveryRuns"] = "Id:uuid Provider:varchar AdapterVersion:varchar AdapterJson:text Cursor:text MaximumItems:int4 Discovered:int4 Pages:int4 State:varchar Attempts:int4 DueUtc:timestamptz LeaseUntilUtc:timestamptz LeaseToken:uuid Error:varchar UpdatedUtc:timestamptz PendingItems:jsonb PendingCursor:text",
        ["ImporterRateStates"] = "Provider:varchar Starts:jsonb BlockedUntilUtc:timestamptz LastStartedUtc:timestamptz"
    };

    internal static async Task<bool> HasColumnsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        foreach (var (table, specification) in Columns)
        {
            await using var command = new NpgsqlCommand("""
                SELECT a.attname, t.typname FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_type t ON t.oid=a.atttypid
                WHERE a.attrelid=to_regclass(@relation) AND a.attnum>0 AND NOT a.attisdropped
                """, connection, transaction);
            command.Parameters.AddWithValue("relation", "\"" + table + "\"");
            var actual = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) actual[reader.GetString(0)] = reader.GetString(1);
            foreach (var expected in specification.Split(' '))
            {
                var pair = expected.Split(':');
                if (!actual.TryGetValue(pair[0], out var type) || type != pair[1]) return false;
            }
        }
        return true;
    }

    internal static async Task<int?> ReadVersionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT \"Version\" FROM \"ImporterSchemaVersions\" WHERE \"Component\"='queue'", connection, transaction);
        return await command.ExecuteScalarAsync(ct) is int version ? version : null;
    }
}
