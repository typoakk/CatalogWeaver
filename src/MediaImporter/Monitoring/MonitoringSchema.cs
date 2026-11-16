using Npgsql;

namespace MediaImporter.Monitoring;

public static class MonitoringSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS "MonitorIdentity" (
            "Singleton" boolean PRIMARY KEY DEFAULT true CHECK ("Singleton"),
            "ServerId" uuid NOT NULL DEFAULT gen_random_uuid(), "SinceUtc" timestamptz NOT NULL DEFAULT now()
        );
        INSERT INTO "MonitorIdentity" ("Singleton") VALUES (true) ON CONFLICT DO NOTHING;
        CREATE TABLE IF NOT EXISTS "MonitorSucceededItems" (
            "Provider" text NOT NULL, "ExternalId" bigint NOT NULL, PRIMARY KEY ("Provider", "ExternalId")
        );
        INSERT INTO "MonitorSucceededItems" SELECT "Provider", "ExternalId" FROM "ImporterJobs"
            WHERE "State"='completed' ON CONFLICT DO NOTHING;
        CREATE TABLE IF NOT EXISTS "MonitorCompletions" (
            "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(), "Utc" timestamptz NOT NULL DEFAULT now(),
            "Provider" text NOT NULL, "ExternalId" bigint NOT NULL, "Kind" text NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_MonitorCompletions_Time" ON "MonitorCompletions" ("Utc", "Provider");
        CREATE OR REPLACE FUNCTION catalogweaver_monitor_completion() RETURNS trigger AS $$
        DECLARE first_success boolean;
        BEGIN
            IF NEW."State"='completed' AND OLD."State"='importing' THEN
                INSERT INTO "MonitorSucceededItems" VALUES (NEW."Provider", NEW."ExternalId")
                    ON CONFLICT DO NOTHING;
                first_success := FOUND;
                INSERT INTO "MonitorCompletions" ("Provider", "ExternalId", "Kind")
                    VALUES (NEW."Provider", NEW."ExternalId", CASE WHEN first_success THEN 'import' ELSE 'refresh' END);
            END IF;
            RETURN NEW;
        END; $$ LANGUAGE plpgsql;
        DROP TRIGGER IF EXISTS catalogweaver_monitor_completion ON "ImporterJobs";
        CREATE TRIGGER catalogweaver_monitor_completion AFTER UPDATE ON "ImporterJobs"
            FOR EACH ROW EXECUTE FUNCTION catalogweaver_monitor_completion();
        CREATE TABLE IF NOT EXISTS "MonitorHttp" (
            "Id" uuid PRIMARY KEY, "Utc" timestamptz NOT NULL, "Provider" text NOT NULL,
            "Category" text NOT NULL, "Host" text NOT NULL, "Status" integer,
            "Error" text, "Received" bigint NOT NULL, "Sent" bigint NOT NULL, "DurationMs" double precision NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_MonitorHttp_Time" ON "MonitorHttp" ("Utc", "Provider");
        CREATE TABLE IF NOT EXISTS "MonitorHttpTotals" (
            "Provider" text NOT NULL, "Category" text NOT NULL, "Requests" bigint NOT NULL,
            "Errors" bigint NOT NULL, "Received" bigint NOT NULL, "Sent" bigint NOT NULL,
            PRIMARY KEY ("Provider", "Category")
        );
        CREATE TABLE IF NOT EXISTS "MonitorSnapshots" (
            "Utc" timestamptz PRIMARY KEY, "Data" jsonb NOT NULL
        );
        CREATE TABLE IF NOT EXISTS "MonitorCurrent" (
            "Singleton" boolean PRIMARY KEY DEFAULT true CHECK ("Singleton"), "Data" jsonb NOT NULL
        );
        """;

    public static async Task InitializeAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(741857211);" + Sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
