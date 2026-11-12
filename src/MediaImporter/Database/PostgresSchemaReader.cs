using Npgsql;

namespace MediaImporter.Database;

internal static class PostgresSchemaReader
{
    internal static async Task<DatabaseSchema> ReadAsync(NpgsqlConnection connection, CancellationToken ct,
        IReadOnlyCollection<(string Schema, string Name)>? selected = null)
    {
        var tables = new List<DatabaseTableSchema>();
        await using (var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, c.relkind::text
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE c.relkind IN ('r','p','v','m') AND n.nspname NOT IN ('pg_catalog','information_schema')
                AND n.nspname NOT LIKE 'pg_toast%' AND n.nspname NOT LIKE 'pg_temp%'
                AND has_table_privilege(c.oid,'SELECT') ORDER BY n.nspname,c.relname
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (selected is null || selected.Contains((reader.GetString(0), reader.GetString(1))))
                    tables.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), [], [], [], [], []));

        var result = new List<DatabaseTableSchema>();
        var warnings = new List<string>();
        foreach (var table in tables)
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            string? prepared = null;
            try
            {
                await using (var settings = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL lock_timeout = '200ms'", connection, transaction))
                    await settings.ExecuteNonQueryAsync(ct);
                var qualified = Quote(table.Schema) + "." + Quote(table.Name);
                if (table.Kind == "m")
                {
                    prepared = "cw_metadata_" + Guid.NewGuid().ToString("N");
                    // PREPARE only parses/rewrites; the business query is never planned or executed.
                    // Its parser lock protects materialized views, which LOCK TABLE rejects.
                    await using var prepare = new NpgsqlCommand("PREPARE " + Quote(prepared) + " AS SELECT 1 FROM ONLY " + qualified + " LIMIT 0", connection, transaction);
                    await prepare.ExecuteNonQueryAsync(ct);
                }
                else
                {
                    await using var relationLock = new NpgsqlCommand("LOCK TABLE ONLY " + qualified + " IN ACCESS SHARE MODE", connection, transaction);
                    await relationLock.ExecuteNonQueryAsync(ct);
                }
                await using var kindCommand = new NpgsqlCommand("SELECT c.relkind::text FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@table", connection, transaction);
                kindCommand.Parameters.AddWithValue("schema", table.Schema);
                kindCommand.Parameters.AddWithValue("table", table.Name);
                var actualKind = await kindCommand.ExecuteScalarAsync(ct) as string;
                if (actualKind is not ("r" or "p" or "v" or "m"))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    warnings.Add("Relation kind changed during inspection: " + table.Schema + "." + table.Name);
                    continue;
                }
            var columns = new List<DatabaseColumnSchema>();
            await using (var command = Command("""
                SELECT a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),t.typname,
                    NOT a.attnotnull,pg_get_expr(d.adbin,d.adrelid),a.attidentity<>'',a.attgenerated<>'',
                    CASE WHEN t.typname IN ('varchar','bpchar') AND a.atttypmod>=4 THEN a.atttypmod-4 END
                FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                JOIN pg_catalog.pg_type t ON t.oid=a.atttypid
                LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
                WHERE n.nspname=@schema AND c.relname=@table AND a.attnum>0 AND NOT a.attisdropped
                ORDER BY a.attnum
                """, connection, table, transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    columns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6),
                        reader.IsDBNull(7) ? null : reader.GetInt32(7)));

            string[] primary = [];
            var unique = new List<DatabaseUniqueKey>();
            var indexes = new List<DatabaseIndex>();
            await using (var command = Command("""
                SELECT ci.relname,i.indisunique,i.indisprimary,
                    ARRAY(SELECT a.attname::text FROM unnest(i.indkey) WITH ORDINALITY k(attnum,ord)
                        JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                        WHERE k.ord<=i.indnkeyatts ORDER BY k.ord),
                    pg_get_indexdef(i.indexrelid),i.indpred IS NULL AND i.indexprs IS NULL AND i.indimmediate
                FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                JOIN pg_catalog.pg_class ci ON ci.oid=i.indexrelid
                WHERE n.nspname=@schema AND c.relname=@table AND i.indisvalid ORDER BY ci.relname
                """, connection, table, transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                {
                    var names = reader.GetFieldValue<string[]>(3);
                    indexes.Add(new(reader.GetString(0), names, reader.GetBoolean(1), reader.GetString(4)));
                    if (reader.GetBoolean(1) && reader.GetBoolean(5)) unique.Add(new(reader.GetString(0), names));
                    if (reader.GetBoolean(2)) primary = names;
                }

            var foreign = new List<DatabaseForeignKey>();
            await using (var command = Command("""
                SELECT f.conname,
                    ARRAY(SELECT a.attname::text FROM unnest(f.conkey) WITH ORDINALITY k(attnum,ord)
                        JOIN pg_catalog.pg_attribute a ON a.attrelid=f.conrelid AND a.attnum=k.attnum ORDER BY k.ord),
                    tn.nspname,tc.relname,
                    ARRAY(SELECT a.attname::text FROM unnest(f.confkey) WITH ORDINALITY k(attnum,ord)
                        JOIN pg_catalog.pg_attribute a ON a.attrelid=f.confrelid AND a.attnum=k.attnum ORDER BY k.ord)
                FROM pg_catalog.pg_constraint f JOIN pg_catalog.pg_class c ON c.oid=f.conrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                JOIN pg_catalog.pg_class tc ON tc.oid=f.confrelid
                JOIN pg_catalog.pg_namespace tn ON tn.oid=tc.relnamespace
                WHERE f.contype='f' AND n.nspname=@schema AND c.relname=@table ORDER BY f.conname
                """, connection, table, transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    foreign.Add(new(reader.GetString(0), reader.GetFieldValue<string[]>(1), reader.GetString(2),
                        reader.GetString(3), reader.GetFieldValue<string[]>(4)));
            var inspected = table with { Kind = actualKind, Columns = [.. columns], PrimaryKey = primary, UniqueKeys = [.. unique],
                ForeignKeys = [.. foreign], Indexes = [.. indexes] };
                if (prepared is not null)
                {
                    await using var deallocate = new NpgsqlCommand("DEALLOCATE " + Quote(prepared), connection, transaction);
                    await deallocate.ExecuteNonQueryAsync(ct);
                    prepared = null;
                }
                await transaction.CommitAsync(ct);
                result.Add(inspected);
            }
            catch (PostgresException error) when (ConcurrentChange(error))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                warnings.Add("Relation changed or was locked during inspection: " + table.Schema + "." + table.Name);
            }
            finally
            {
                if (prepared is not null && connection.State == System.Data.ConnectionState.Open)
                {
                    try
                    {
                        await using var deallocate = new NpgsqlCommand("DEALLOCATE " + Quote(prepared), connection);
                        await deallocate.ExecuteNonQueryAsync(CancellationToken.None);
                    }
                    catch (PostgresException) { /* Outer connection disposal resets a failed session. */ }
                }
            }
        }
        return new([.. result]) { Warnings = [.. warnings] };
    }

    private static bool ConcurrentChange(PostgresException error) => error.SqlState is "42P01" or "3F000" or "55P03" or "40P01"
        || error.SqlState == "XX000" && (error.MessageText.Contains("could not open relation", StringComparison.Ordinal)
            || error.MessageText.Contains("cache lookup failed for relation", StringComparison.Ordinal));
    private static string Quote(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection, DatabaseTableSchema table, NpgsqlTransaction transaction)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("schema", table.Schema);
        command.Parameters.AddWithValue("table", table.Name);
        return command;
    }
}
