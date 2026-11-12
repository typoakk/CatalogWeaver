using Npgsql;

namespace MediaImporter;

/// <summary>The private catalog/state schema owned by the standalone tool.</summary>
public static class StandaloneSchema
{
    public const int Version = 2;

    private const string Migration2 = """
        ALTER TABLE "ImporterExportedFiles" ADD COLUMN IF NOT EXISTS "Revision" uuid NOT NULL DEFAULT gen_random_uuid();
        CREATE TABLE IF NOT EXISTS "ImporterPublications" (
            "Id" uuid PRIMARY KEY, "Hash" varchar(64) NOT NULL, "RelativePath" varchar(1024) NOT NULL,
            "ContentType" text NOT NULL, "Bytes" bigint NOT NULL, "OwnsFile" boolean NOT NULL,
            "JobId" uuid, "LeaseToken" uuid, "CreatedUtc" timestamptz NOT NULL DEFAULT now(),
            "ExpiresUtc" timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_ImporterPublications_File" ON "ImporterPublications" ("Hash", "RelativePath");
        CREATE INDEX IF NOT EXISTS "IX_ImporterPublications_Created" ON "ImporterPublications" ("CreatedUtc");
        CREATE INDEX IF NOT EXISTS "IX_ImporterItems_Page" ON "ImporterItems" ("UpdatedUtc" DESC, "MediaId");
        CREATE INDEX IF NOT EXISTS "IX_ImporterItems_IdentityPage" ON "ImporterItems" ("Provider" COLLATE "C", "ExternalId");
        UPDATE "ImporterSchemaVersions" SET "Version"=2,"AppliedUtc"=now() WHERE "Component"='standalone' AND "Version"=1;
        """;

    private const string Sql = """
        CREATE TABLE IF NOT EXISTS "ImporterSchemaVersions" (
            "Component" text PRIMARY KEY, "Version" integer NOT NULL,
            "AppliedUtc" timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS "ImporterItems" (
            "Provider" varchar(24) NOT NULL, "ExternalId" bigint NOT NULL,
            "MediaId" varchar(64) NOT NULL UNIQUE, "AdapterVersion" varchar(64) NOT NULL,
            "MetadataJson" jsonb NOT NULL, "AssetStatusJson" jsonb NOT NULL,
            "WarningsJson" jsonb NOT NULL, "UpdatedUtc" timestamptz NOT NULL,
            PRIMARY KEY ("Provider", "ExternalId")
        );
        CREATE TABLE IF NOT EXISTS "ImporterAssets" (
            "Provider" varchar(24) NOT NULL, "ExternalId" bigint NOT NULL,
            "Role" varchar(32) NOT NULL, "Position" integer NOT NULL,
            "AssetJson" jsonb NOT NULL, "Hash" varchar(64), "RelativePath" varchar(1024),
            PRIMARY KEY ("Provider", "ExternalId", "Role", "Position"),
            FOREIGN KEY ("Provider", "ExternalId") REFERENCES "ImporterItems" ("Provider", "ExternalId") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_ImporterAssets_File" ON "ImporterAssets" ("Hash", "RelativePath");
        CREATE TABLE IF NOT EXISTS "ImporterFiles" (
            "Hash" varchar(64) NOT NULL, "RelativePath" varchar(1024) NOT NULL,
            "ContentType" text NOT NULL, "Bytes" bigint NOT NULL, "CreatedUtc" timestamptz NOT NULL,
            PRIMARY KEY ("Hash", "RelativePath")
        );
        CREATE TABLE IF NOT EXISTS "ImporterExportedFiles" (
            "Hash" varchar(64) NOT NULL, "RelativePath" varchar(1024) NOT NULL,
            "ReservedUtc" timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY ("Hash", "RelativePath")
        );
        INSERT INTO "ImporterSchemaVersions" ("Component", "Version") VALUES ('standalone', 1)
            ON CONFLICT ("Component") DO NOTHING;
        """;

    public static async Task InitializeAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(741857210);" + Sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await using var version = new NpgsqlCommand("SELECT \"Version\" FROM \"ImporterSchemaVersions\" WHERE \"Component\"='standalone'", connection, transaction);
        var current = Convert.ToInt32(await version.ExecuteScalarAsync(ct));
        if (current is < 1 or > Version)
            throw new InvalidOperationException("Unsupported standalone database schema version.");
        if (current < 2)
        {
            await using var migration = new NpgsqlCommand(Migration2, connection, transaction);
            await migration.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public static async Task<bool> IsReadyAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tables = new NpgsqlCommand("""
            SELECT to_regclass('"ImporterSchemaVersions"') IS NOT NULL
                AND to_regclass('"ImporterItems"') IS NOT NULL
                AND to_regclass('"ImporterAssets"') IS NOT NULL
                AND to_regclass('"ImporterFiles"') IS NOT NULL
                AND to_regclass('"ImporterExportedFiles"') IS NOT NULL
                AND to_regclass('"ImporterPublications"') IS NOT NULL
            """, connection);
        if (!(bool)(await tables.ExecuteScalarAsync(ct))!) return false;
        await using var version = new NpgsqlCommand("SELECT \"Version\" FROM \"ImporterSchemaVersions\" WHERE \"Component\"='standalone'", connection);
        if (await version.ExecuteScalarAsync(ct) is not int value || value != Version) return false;
        await using var columns = new NpgsqlCommand("""
            SELECT NOT EXISTS (
                SELECT 1 FROM (VALUES
                    ('ImporterItems','Provider'),('ImporterItems','ExternalId'),('ImporterItems','MediaId'),
                    ('ImporterItems','AdapterVersion'),('ImporterItems','MetadataJson'),('ImporterItems','AssetStatusJson'),
                    ('ImporterItems','WarningsJson'),('ImporterItems','UpdatedUtc'),
                    ('ImporterAssets','Provider'),('ImporterAssets','ExternalId'),('ImporterAssets','Role'),
                    ('ImporterAssets','Position'),('ImporterAssets','AssetJson'),('ImporterAssets','Hash'),('ImporterAssets','RelativePath'),
                    ('ImporterFiles','Hash'),('ImporterFiles','RelativePath'),('ImporterFiles','ContentType'),('ImporterFiles','Bytes'),('ImporterFiles','CreatedUtc'),
                    ('ImporterExportedFiles','Hash'),('ImporterExportedFiles','RelativePath'),('ImporterExportedFiles','ReservedUtc'),('ImporterExportedFiles','Revision'),
                    ('ImporterPublications','Id'),('ImporterPublications','Hash'),('ImporterPublications','RelativePath'),
                    ('ImporterPublications','ContentType'),('ImporterPublications','Bytes'),('ImporterPublications','OwnsFile'),
                    ('ImporterPublications','JobId'),('ImporterPublications','LeaseToken'),('ImporterPublications','CreatedUtc'),('ImporterPublications','ExpiresUtc')
                ) AS required(relation,column_name)
                WHERE NOT EXISTS (SELECT 1 FROM pg_attribute a
                    WHERE a.attrelid=to_regclass(format('%I',required.relation)) AND a.attname=required.column_name
                        AND a.attnum>0 AND NOT a.attisdropped)
            )
            """, connection);
        return (bool)(await columns.ExecuteScalarAsync(ct))!;
    }
}
