using System.Text.Json;
using Npgsql;

namespace MediaImporter.Database;

/// <summary>Shared declarative PostgreSQL writer and read-only change planner. Never changes target DDL.</summary>
public sealed partial class PostgresDatabaseWriter(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ItemFields = typeof(AdapterItem).GetProperties()
        .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> AssetFields = typeof(AssetResult).GetProperties()
        .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Append("url").ToHashSet(StringComparer.Ordinal);

    public async Task<DatabaseSchema> InspectAsync(string connectionName, CancellationToken ct)
    {
        await using var connection = await OpenAsync(connectionName, ct);
        return await PostgresSchemaReader.ReadAsync(connection, ct);
    }

    public async Task<string[]> ValidateAsync(DatabaseDestination destination, CancellationToken ct)
    {
        var errors = ValidateDefinition(destination);
        if (errors.Length != 0) return errors;
        await using var connection = await OpenAsync(destination.Connection, ct);
        var schema = await ReadSchemaAsync(destination, connection, ct);
        return ValidateSchema(destination, schema);
    }

    public async Task<string> ResolveMediaIdAsync(DatabaseDestination destination, AdapterItem item,
        string fallbackMediaId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(destination.Connection, ct);
        var schema = await RequireSchemaAsync(destination, connection, ct);
        var context = new Evaluation(item, [], fallbackMediaId, DateTime.UtcNow, new(StringComparer.Ordinal),
            new(), destination.LocalUrlTemplate, schema);
        return await IdentityAsync(destination, context, connection, null, ct);
    }

    private static async Task<string> IdentityAsync(DatabaseDestination destination, Evaluation context,
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var value = destination.MediaId is null ? context.MediaId
            : await EvaluateAsync(destination.MediaId, context, connection, transaction, ct);
        if (value is null or UnknownValue or SkipValue || string.IsNullOrWhiteSpace(StringValue(value)))
            throw new InvalidDataException("The configured identity must resolve to a non-empty value.");
        var text = StringValue(value);
        if (text.Length > 256) throw new InvalidDataException("The configured identity is too long.");
        return text;
    }

    public async Task<DatabaseWriteResult> WriteAsync(DatabaseDestination destination, AdapterItem item,
        IReadOnlyList<AssetResult> assets, string mediaId, CancellationToken ct, DatabaseExecutionContext? execution = null)
    {
        await using var connection = await OpenAsync(destination.Connection, ct);
        var schema = await RequireSchemaAsync(destination, connection, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var mutex = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,847231))", connection, transaction))
        {
            mutex.Parameters.AddWithValue("key", item.Provider + ":" + item.ExternalId);
            await mutex.ExecuteNonQueryAsync(ct);
        }
        var context = new Evaluation(item, assets, mediaId, DateTime.UtcNow, new(StringComparer.Ordinal), execution ?? new(), destination.LocalUrlTemplate, schema);
        context = context with { MediaId = await IdentityAsync(destination, context, connection, transaction, ct) };
        var writes = new List<DatabaseTableWrite>();
        foreach (var mapping in destination.Tables)
        {
            if (SkipReason(mapping, context) is not null) { writes.Add(new(mapping.Name, 0, 0)); continue; }
            var table = Find(schema, mapping.Schema, mapping.Table);
            var scope = context with { Role = mapping.Role };
            var removed = 0;
            if (mapping.ReplaceScope.Count > 0)
            {
                var predicates = await ValuesAsync(mapping.ReplaceScope, scope, connection, transaction, ct);
                EnsurePredicates(predicates);
                await using var delete = new NpgsqlCommand { Connection = connection, Transaction = transaction };
                delete.CommandText = "DELETE FROM " + Qualified(table) + " WHERE " + Predicate(delete, table, predicates);
                removed = await delete.ExecuteNonQueryAsync(ct);
            }
            var written = 0;
            foreach (var row in Rows(mapping, scope))
            {
                var values = await ValuesAsync(mapping.Columns, row, connection, transaction, ct);
                EnsureMatch(mapping, values);
                var existing = await SnapshotAsync(table, mapping, values, connection, transaction, ct, true);
                EnsureValues(table, mapping, values, existing.Exists);
                object? returned = existing.Returned;
                var assignments = values.Where(pair => !mapping.MatchColumns.Contains(pair.Key)
                    && mapping.Columns[pair.Key].OnUpdate != "preserve").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (existing.Exists)
                {
                    assignments = assignments.Where(pair => !ColumnEqual(table.Columns.Single(c => c.Name == pair.Key), existing.Values.GetValueOrDefault(pair.Key), pair.Value))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    if (assignments.Count > 0)
                    {
                        await using var update = new NpgsqlCommand { Connection = connection, Transaction = transaction };
                        var set = assignments.Select(pair => Quote(pair.Key) + "=" + Parameter(update, table.Columns.Single(c => c.Name == pair.Key), pair.Value)).ToArray();
                        update.CommandText = "UPDATE " + Qualified(table) + " SET " + string.Join(',', set) + " WHERE "
                            + Predicate(update, table, mapping.MatchColumns.ToDictionary(name => name, name => values[name]))
                            + " RETURNING " + (mapping.Returning is null ? "1" : Quote(mapping.Returning));
                        returned = await update.ExecuteScalarAsync(ct);
                    }
                }
                else if (mapping.Operation != "update")
                {
                    await using var insert = new NpgsqlCommand { Connection = connection, Transaction = transaction };
                    var columns = values.Keys.ToArray();
                    var parameters = columns.Select(name => Parameter(insert, table.Columns.Single(c => c.Name == name), values[name])).ToArray();
                    var set = assignments.Keys.Select(name => Quote(name) + "=EXCLUDED." + Quote(name)).ToArray();
                    if (set.Length == 0) set = [Quote(mapping.MatchColumns[0]) + "=EXCLUDED." + Quote(mapping.MatchColumns[0])];
                    insert.CommandText = "INSERT INTO " + Qualified(table) + " (" + string.Join(',', columns.Select(Quote)) + ") VALUES ("
                        + string.Join(',', parameters) + ") ON CONFLICT (" + string.Join(',', mapping.MatchColumns.Select(Quote)) + ") DO UPDATE SET "
                        + string.Join(',', set) + " RETURNING " + (mapping.Returning is null ? "1" : Quote(mapping.Returning));
                    returned = await insert.ExecuteScalarAsync(ct);
                }
                if (mapping.Returning is not null) context.Returned[mapping.Name] = returned is DBNull ? null : returned;
                if (existing.Exists || mapping.Operation != "update") written++;
            }
            writes.Add(new(mapping.Name, written, removed));
        }
        await transaction.CommitAsync(ct);
        return new([.. writes], context.Returned) { MediaId = context.MediaId };
    }

    public async Task<DatabaseChangePlan> PlanAsync(DatabaseDestination destination, AdapterItem item,
        IReadOnlyList<AssetResult> assets, string mediaId, CancellationToken ct, DatabaseExecutionContext? execution = null)
    {
        await using var connection = await OpenAsync(destination.Connection, ct);
        var schema = await RequireSchemaAsync(destination, connection, ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(ct);
        var context = new Evaluation(item, assets, mediaId, DateTime.UtcNow, new(StringComparer.Ordinal), execution ?? new(), destination.LocalUrlTemplate, schema);
        context = context with { MediaId = await IdentityAsync(destination, context, connection, transaction, ct) };
        var virtualRows = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        var plans = new List<DatabaseTablePlan>();
        foreach (var mapping in destination.Tables)
        {
            if (SkipReason(mapping, context) is { } reason)
            { plans.Add(new(mapping.Name, mapping.Schema, mapping.Table, 0, 0, 0, 0, 0, [], reason)); continue; }
            var table = Find(schema, mapping.Schema, mapping.Table);
            var scope = context with { Role = mapping.Role };
            var changes = new List<DatabaseRowPlan>();
            var deletes = 0;
            var replaced = mapping.ReplaceScope.Count > 0;
            if (replaced)
            {
                var values = await ValuesAsync(mapping.ReplaceScope, scope, connection, transaction, ct);
                if (values.Values.Any(value => value is UnknownValue))
                    changes.Add(new("unresolved", Public(values), [], "Collection owner depends on a generated parent key."));
                else
                {
                    EnsurePredicates(values);
                    await using var count = new NpgsqlCommand { Connection = connection, Transaction = transaction };
                    count.CommandText = "SELECT count(*) FROM " + Qualified(table) + " WHERE " + Predicate(count, table, values);
                    deletes = checked((int)(long)(await count.ExecuteScalarAsync(ct))!);
                    if (deletes > 0) changes.Add(new("delete", Public(values), [], "Explicit collection replacement scope.", deletes));
                }
            }
            foreach (var row in Rows(mapping, scope))
            {
                var values = await ValuesAsync(mapping.Columns, row, connection, transaction, ct);
                var match = mapping.MatchColumns.Where(values.ContainsKey).ToDictionary(name => name, name => values[name]);
                if (values.Values.Any(value => value is UnknownValue))
                {
                    changes.Add(new("unresolved", Public(match), values.Keys.ToArray(), "Values depend on a key generated only during writing."));
                    if (mapping.Returning is not null) context.Returned[mapping.Name] = new UnknownValue("result." + mapping.Name);
                    continue;
                }
                EnsureMatch(mapping, values);
                var key = RowKey(table, mapping, values);
                var existing = !replaced && virtualRows.TryGetValue(key, out var pending) ? pending
                    : replaced ? new Snapshot(false, [], null) : await SnapshotAsync(table, mapping, values, connection, transaction, ct, false);
                EnsureValues(table, mapping, values, existing.Exists);
                var changed = values.Where(pair => !mapping.MatchColumns.Contains(pair.Key)
                    && mapping.Columns[pair.Key].OnUpdate != "preserve"
                    && (!existing.Exists || !ColumnEqual(table.Columns.Single(c => c.Name == pair.Key), existing.Values.GetValueOrDefault(pair.Key), pair.Value)))
                    .Select(pair => pair.Key).ToArray();
                var operation = existing.Exists ? changed.Length == 0 ? "noop" : "update"
                    : mapping.Operation == "update" ? "noop" : "insert";
                var unknownDefault = existing.Exists && changed.Any(name => existing.Values.GetValueOrDefault(name) is UnknownValue);
                if (unknownDefault) operation = "unresolved";
                changes.Add(new(operation, Public(match), operation == "insert" ? values.Keys.ToArray() : changed,
                    unknownDefault ? "An earlier insert depends on database default/generated values."
                    : !existing.Exists && mapping.Operation == "update" ? "Update-only target row does not exist." : null));
                object? returned = existing.Returned;
                if (mapping.Returning is not null)
                {
                    returned = existing.Exists ? existing.Values.GetValueOrDefault(mapping.Returning)
                        : values.GetValueOrDefault(mapping.Returning, new UnknownValue("result." + mapping.Name));
                    context.Returned[mapping.Name] = returned;
                }
                if (operation is not "noop" || existing.Exists)
                {
                    var merged = existing.Exists ? new Dictionary<string, object?>(existing.Values, StringComparer.Ordinal)
                        : table.Columns.ToDictionary(column => column.Name, column => column.Default is not null || column.Generated || column.Identity
                            ? (object?)new UnknownValue("default." + column.Name) : null, StringComparer.Ordinal);
                    foreach (var pair in values)
                        if (!existing.Exists || mapping.Columns[pair.Key].OnUpdate != "preserve") merged[pair.Key] = pair.Value;
                    virtualRows[key] = new(true, merged, returned);
                }
            }
            plans.Add(new(mapping.Name, mapping.Schema, mapping.Table, changes.Count(c => c.Operation == "insert"),
                changes.Count(c => c.Operation == "update"), deletes, changes.Count(c => c.Operation == "noop"),
                changes.Count(c => c.Operation == "unresolved"), changes.ToArray()));
        }
        await transaction.CommitAsync(ct);
        return new(context.MediaId, plans.ToArray(), ["Read-only snapshot; concurrent changes, triggers and generated/default values cannot be predicted."]);
    }

    private sealed record Snapshot(bool Exists, Dictionary<string, object?> Values, object? Returned);
    private static async Task<Snapshot> SnapshotAsync(DatabaseTableSchema table, DatabaseTableMapping mapping,
        Dictionary<string, object?> values, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct, bool forUpdate)
    {
        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        command.CommandText = "SELECT " + string.Join(',', table.Columns.Select(column => Quote(column.Name))) + " FROM " + Qualified(table)
            + " WHERE " + Predicate(command, table, mapping.MatchColumns.ToDictionary(name => name, name => values[name])) + (forUpdate ? " FOR UPDATE" : "");
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(false, [], null);
        var current = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < table.Columns.Length; index++) current[table.Columns[index].Name] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        return new(true, current, mapping.Returning is null ? 1 : current.GetValueOrDefault(mapping.Returning));
    }

    private static async Task<Dictionary<string, object?>> ValuesAsync(Dictionary<string, DatabaseValue> definitions, Evaluation context,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, definition) in definitions)
        {
            var result = await EvaluateAsync(definition, context, connection, transaction, ct);
            if (result is SkipValue || result is null && definition.OnNull == "preserve") continue;
            values[name] = result;
        }
        return values;
    }
    private static void EnsureMatch(DatabaseTableMapping mapping, Dictionary<string, object?> values)
    {
        if (mapping.MatchColumns.Any(name => !values.TryGetValue(name, out var value) || value is null or UnknownValue))
            throw new InvalidDataException("Match columns require known non-null values: " + mapping.Name);
    }
    private static void EnsurePredicates(Dictionary<string, object?> values)
    {
        if (values.Count == 0 || values.Values.Any(value => value is null or UnknownValue)) throw new InvalidDataException("Collection predicates require known non-null values.");
    }
    private static void EnsureValues(DatabaseTableSchema table, DatabaseTableMapping mapping, Dictionary<string, object?> values, bool existing)
    {
        foreach (var column in table.Columns)
        {
            if (existing && mapping.Columns.TryGetValue(column.Name, out var definition) && definition.OnUpdate == "preserve") continue;
            if (values.TryGetValue(column.Name, out var value) && value is null && !column.Nullable)
                throw new InvalidDataException("Null value for required column: " + table.Name + "." + column.Name);
            if (!existing && mapping.Operation != "update" && !column.Nullable && column.Default is null && !column.Identity && !column.Generated
                && !values.ContainsKey(column.Name))
                throw new InvalidDataException("Required insert value is unavailable: " + table.Name + "." + column.Name);
        }
    }
    private static string Predicate(NpgsqlCommand command, DatabaseTableSchema table, Dictionary<string, object?> values) =>
        string.Join(" AND ", values.Select(pair => Quote(pair.Key) + " IS NOT DISTINCT FROM " + Parameter(command, table.Columns.Single(c => c.Name == pair.Key), pair.Value)));
    private static string? SkipReason(DatabaseTableMapping mapping, Evaluation context)
    {
        if (mapping.Role is { } role && !Complete(context, role)) return "Role was omitted or needs retry: " + role;
        if (mapping.PreserveOnStates.Length > 0 && context.Assets.Any(asset => (mapping.Role is null || asset.Role == mapping.Role)
            && mapping.PreserveOnStates.Contains(asset.State))) return "Existing role preserved after an unavailable source asset.";
        return null;
    }
    private static IEnumerable<Evaluation> Rows(DatabaseTableMapping mapping, Evaluation context)
    {
        if (mapping.Scope == "item") { yield return context; yield break; }
        if (mapping.Scope == "role")
        {
            var asset = context.Assets.Where(asset => asset.Role == mapping.Role).OrderBy(asset => asset.Position).FirstOrDefault()
                ?? new AssetResult { Role = mapping.Role!, State = "missing" };
            yield return context with { Asset = asset, Role = mapping.Role, Index = 0 }; yield break;
        }
        if (mapping.Scope is "assets" or "files")
        {
            var assets = context.Assets.Where(asset => (mapping.Role is null || asset.Role == mapping.Role)
                && Complete(context, asset.Role) && (mapping.States.Length == 0 || mapping.States.Contains(asset.State)));
            if (mapping.Scope == "files") assets = assets.Where(asset => asset.Hash is not null && asset.RelativePath is not null)
                .DistinctBy(asset => (asset.Hash, asset.RelativePath));
            foreach (var asset in assets) yield return context with { Asset = asset, Role = asset.Role, Index = asset.Position };
            yield break;
        }
        var values = mapping.Scope == "tags" ? context.Item.Tags : context.Item.Genres;
        for (var index = 0; index < values.Length; index++) yield return context with { Row = values[index], Index = index };
    }
    private static bool ColumnEqual(DatabaseColumnSchema column, object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is UnknownValue || right is UnknownValue) return false;
        using var command = new NpgsqlCommand(); Parameter(command, column, right);
        var converted = command.Parameters[0].Value;
        if (column.TypeName is "json" or "jsonb")
        {
            using var a = JsonDocument.Parse(left is string text ? text : JsonSerializer.Serialize(left, Json));
            using var b = JsonDocument.Parse((string)converted!);
            return JsonElement.DeepEquals(a.RootElement, b.RootElement);
        }
        if (left is Array) return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left, Json), JsonSerializer.SerializeToElement(converted, Json));
        return Equals(left, converted);
    }
    private static Dictionary<string, object?> Public(Dictionary<string, object?> values) => values.ToDictionary(pair => pair.Key,
        pair => pair.Value is UnknownValue unknown ? (object)new { unresolved = unknown.Reference } : pair.Value, StringComparer.Ordinal);
    private static string RowKey(DatabaseTableSchema table, DatabaseTableMapping mapping, Dictionary<string, object?> values) => table.Schema + "." + table.Name + ":"
        + JsonSerializer.Serialize(mapping.MatchColumns.Order(StringComparer.Ordinal).Select(name => new { name, value = values[name] }), Json);
    private async Task<NpgsqlConnection> OpenAsync(string name, CancellationToken ct)
    {
        if (!Identifier(name)) throw new InvalidDataException("Invalid connection name.");
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Configure the named database connection: " + name);
        var connection = new NpgsqlConnection(value);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static DatabaseTableSchema Find(DatabaseSchema schema, string schemaName, string tableName) => schema.Tables.Single(table => table.Schema == schemaName && table.Name == tableName);
}
