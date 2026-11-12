using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace MediaImporter.Database;

public sealed partial class PostgresDatabaseWriter
{
    public static string[] ValidateDefinition(DatabaseDestination destination)
    {
        var errors = new List<string>();
        if (!Identifier(destination.Connection)) errors.Add("Database connection must reference a named ConnectionStrings entry.");
        if (destination.Tables is null || destination.Tables.Length is < 1 or > 64)
            return [.. errors, "Database destination requires between 1 and 64 table mappings."];
        if (destination.LocalUrlTemplate is { } template && (template.Length > 2048
            || template.Replace("{hash}", "").Replace("{relativePath}", "").IndexOfAny(['{','}']) >= 0))
            errors.Add("localUrlTemplate supports only {hash} and {relativePath}.");
        var returned = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        if (destination.MediaId is not null) ValidateValue(destination.MediaId, returned, "item", errors, 0);
        foreach (var mapping in destination.Tables)
        {
            if (mapping is null) { errors.Add("Table mappings cannot be null."); continue; }
            if (!Identifier(mapping.Name) || !Identifier(mapping.Schema) || !Identifier(mapping.Table)) errors.Add("Mappings require simple names/schema/table identifiers.");
            if (!aliases.Add(mapping.Name)) errors.Add("Table mapping names must be unique.");
            if (mapping.Scope is not ("item" or "assets" or "role" or "files" or "tags" or "genres")) errors.Add("Unsupported table scope.");
            if (mapping.Operation is not ("upsert" or "update")) errors.Add("operation must be upsert or update.");
            if (mapping.Role is not null && !Identifier(mapping.Role)) errors.Add("Role must be a simple identifier.");
            if (mapping.Scope == "role" && mapping.Role is null) errors.Add("Single role scope requires role.");
            if (mapping.Columns is null || mapping.MatchColumns is null || mapping.ReplaceScope is null || mapping.States is null || mapping.PreserveOnStates is null)
            { errors.Add("Mapping collections cannot be null."); continue; }
            if (mapping.Columns.Count is < 1 or > 128 || mapping.MatchColumns.Length is < 1 or > 32
                || mapping.MatchColumns.Distinct(StringComparer.Ordinal).Count() != mapping.MatchColumns.Length)
                errors.Add("Mappings require columns and distinct matchColumns.");
            if (mapping.MatchColumns.Any(column => !mapping.Columns.ContainsKey(column))) errors.Add("Every match column must have a value mapping.");
            if (mapping.Returning is not null && (mapping.Scope != "item" || !Identifier(mapping.Returning))) errors.Add("Only item mappings can return a parent key.");
            if (mapping.Scope is "item" or "role" && mapping.ReplaceScope.Count > 0) errors.Add("replaceScope requires a collection mapping.");
            if (mapping.Operation == "update" && mapping.ReplaceScope.Count > 0) errors.Add("Update-only mappings cannot replace collections.");
            if (mapping.Scope is "assets" or "files" && mapping.ReplaceScope.Count > 0
                && (mapping.Role is null || mapping.ReplaceScope.Count < 2 || !mapping.ReplaceScope.Values.Any(value => value is not null
                    && (value.Source == "asset.role" || value.Constant is { ValueKind: JsonValueKind.String } c && c.GetString() == mapping.Role))))
                errors.Add("Replacing assets requires a role filter and a replaceScope predicate containing that role.");
            foreach (var (column, value) in mapping.Columns.Concat(mapping.ReplaceScope))
            {
                if (!Identifier(column)) errors.Add("Column names must be simple identifiers.");
                if (value is null) { errors.Add("Value definitions cannot be null."); continue; }
                ValidateValue(value, returned, mapping.Scope, errors, 0);
            }
            if (mapping.Returning is not null) returned.Add(mapping.Name);
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateValue(DatabaseValue value, HashSet<string> returned, string scope, List<string> errors, int depth)
    {
        if (depth > 16) { errors.Add("Value expressions are limited to 16 levels."); return; }
        if (value.OnNull is not ("write" or "preserve" or "default")) errors.Add("onNull must be write, preserve or default.");
        if (value.OnUpdate is not ("write" or "preserve")) errors.Add("onUpdate must be write or preserve.");
        if (value.MapMissing is not ("original" or "null")) errors.Add("mapMissing must be original or null.");
        if (value.Limit is < 0 or > 1_000_000) errors.Add("limit is outside supported bounds.");
        if (value.WhenRole is not null && !Identifier(value.WhenRole) || value.Role is not null && !Identifier(value.Role)) errors.Add("Invalid role identifier.");
        if (value.Coalesce is null || value.Merge is null) { errors.Add("Expression collections cannot be null."); return; }
        var primary = (value.Source is null ? 0 : 1) + (value.Constant is null ? 0 : 1) + (value.Lookup is null ? 0 : 1)
            + (value.Template is null ? 0 : 1) + (value.Object is null ? 0 : 1) + (value.Condition is null ? 0 : 1)
            + (value.Coalesce.Length == 0 ? 0 : 1) + (value.Merge.Length == 0 ? 0 : 1);
        if (primary > 1) errors.Add("A value expression must have only one primary operation.");
        if (value.Source is { } source)
        {
            if (!ValidSource(source)) errors.Add("Unsupported value source: " + source);
            if (source.StartsWith("result.", StringComparison.Ordinal) && !returned.Contains(source[7..])) errors.Add("Parent result sources must reference an earlier item mapping with returning.");
            if (source.StartsWith("asset.", StringComparison.Ordinal) && scope == "item" && !value.First) errors.Add("Asset sources in item scope require first=true.");
            if (source.StartsWith("row.", StringComparison.Ordinal) && scope == "item") errors.Add("Row sources require collection scope.");
        }
        if ((value.Select is not null || value.SelectValue is not null) && value.Source != "assets") errors.Add("select and selectValue require source=assets.");
        if (value.Select is not null && value.SelectValue is not null) errors.Add("select and selectValue cannot be combined.");
        if (value.Template is { } template)
        {
            foreach (Match token in Regex.Matches(template, "\\{([^{}]+)\\}"))
            {
                var tokenSource = token.Groups[1].Value;
                if (!ValidSource(tokenSource)) errors.Add("Unsupported template source: " + tokenSource);
                if (tokenSource.StartsWith("result.", StringComparison.Ordinal) && !returned.Contains(tokenSource[7..])) errors.Add("Template parent references must be earlier mappings.");
            }
            if (Regex.Replace(template, "\\{([^{}]+)\\}", "").IndexOfAny(['{','}']) >= 0) errors.Add("Invalid value template.");
        }
        if (value.Lookup is { } lookup)
        {
            if (!Identifier(lookup.Schema) || !Identifier(lookup.Table) || !Identifier(lookup.Column) || lookup.Where is null || lookup.Where.Count is < 1 or > 32)
                errors.Add("Lookups require simple table/column identifiers and bounded where predicates.");
            else foreach (var column in lookup.Where.Keys) if (!Identifier(column)) errors.Add("Invalid lookup predicate column.");
        }
        if (value.Condition is { } condition && condition.Operator is not ("eq" or "ne" or "isNull" or "notNull")) errors.Add("Unsupported condition operator.");
        foreach (var child in Children(value))
        {
            if (child is null) { errors.Add("Nested value definitions cannot be null."); continue; }
            ValidateValue(child, returned, value.Select is not null || value.SelectValue is not null || value.First ? "assets" : scope, errors, depth + 1);
        }
    }

    private static IEnumerable<DatabaseValue> Children(DatabaseValue value)
    {
        foreach (var child in value.Coalesce) yield return child;
        foreach (var child in value.Merge) yield return child;
        if (value.Lookup?.Where is { } where) foreach (var child in where.Values) yield return child;
        if (value.Object is { } obj) foreach (var child in obj.Values) yield return child;
        if (value.Select is { } select) foreach (var child in select.Values) yield return child;
        if (value.SelectValue is not null) yield return value.SelectValue;
        if (value.Condition is { } condition) { yield return condition.Left; if (condition.Right is not null) yield return condition.Right; }
        if (value.Then is not null) yield return value.Then;
        if (value.Else is not null) yield return value.Else;
    }
    private static IEnumerable<DatabaseValue> Expressions(DatabaseDestination destination)
    {
        var roots = destination.Tables.SelectMany(table => table.Columns.Values.Concat(table.ReplaceScope.Values));
        if (destination.MediaId is not null) roots = roots.Prepend(destination.MediaId);
        foreach (var root in roots) foreach (var child in Walk(root)) yield return child;
        static IEnumerable<DatabaseValue> Walk(DatabaseValue value)
        { yield return value; foreach (var child in Children(value)) foreach (var nested in Walk(child)) yield return nested; }
    }
    private static Task<DatabaseSchema> ReadSchemaAsync(DatabaseDestination destination, NpgsqlConnection connection, CancellationToken ct)
    {
        var tables = destination.Tables.Select(table => (table.Schema, table.Table))
            .Concat(Expressions(destination).Where(value => value.Lookup is not null).Select(value => (value.Lookup!.Schema, value.Lookup.Table)))
            .Distinct().ToArray();
        return PostgresSchemaReader.ReadAsync(connection, ct, tables);
    }
    private static async Task<DatabaseSchema> RequireSchemaAsync(DatabaseDestination destination, NpgsqlConnection connection, CancellationToken ct)
    {
        var errors = ValidateDefinition(destination);
        if (errors.Length != 0) throw new InvalidDataException(string.Join(" ", errors));
        var schema = await ReadSchemaAsync(destination, connection, ct);
        errors = ValidateSchema(destination, schema);
        if (errors.Length != 0) throw new InvalidDataException(string.Join(" ", errors));
        return schema;
    }
    private static string[] ValidateSchema(DatabaseDestination destination, DatabaseSchema schema)
    {
        var errors = new List<string>();
        foreach (var mapping in destination.Tables)
        {
            var table = schema.Tables.SingleOrDefault(table => table.Schema == mapping.Schema && table.Name == mapping.Table);
            if (table is null) { errors.Add("Target table is unavailable: " + mapping.Schema + "." + mapping.Table); continue; }
            if (table.Kind is not ("r" or "p")) errors.Add("Target must be a physical table: " + mapping.Table);
            foreach (var name in mapping.Columns.Keys.Concat(mapping.ReplaceScope.Keys).Concat(mapping.Returning is null ? [] : new[] { mapping.Returning }))
                if (table.Columns.All(column => column.Name != name)) errors.Add("Target column is unavailable: " + mapping.Table + "." + name);
            foreach (var column in table.Columns)
            {
                if (mapping.Columns.ContainsKey(column.Name) && (column.Generated || column.Identity)
                    && !(mapping.Operation == "update" && mapping.MatchColumns.Contains(column.Name)))
                    errors.Add("Generated/identity columns must be omitted: " + mapping.Table + "." + column.Name);
                if (mapping.Operation != "update" && !column.Nullable && column.Default is null && !column.Identity && !column.Generated && !mapping.Columns.ContainsKey(column.Name))
                    errors.Add("Required column has no mapping or database default: " + mapping.Table + "." + column.Name);
            }
            if (!table.UniqueKeys.Any(key => key.Columns.Length == mapping.MatchColumns.Length && key.Columns.ToHashSet(StringComparer.Ordinal).SetEquals(mapping.MatchColumns)))
                errors.Add("matchColumns must match an existing non-partial unique key: " + mapping.Table);
        }
        foreach (var lookup in Expressions(destination).Where(value => value.Lookup is not null).Select(value => value.Lookup!))
        {
            var table = schema.Tables.SingleOrDefault(table => table.Schema == lookup.Schema && table.Name == lookup.Table);
            if (table is null) { errors.Add("Lookup table is unavailable: " + lookup.Schema + "." + lookup.Table); continue; }
            foreach (var column in lookup.Where.Keys.Append(lookup.Column))
                if (table.Columns.All(value => value.Name != column)) errors.Add("Lookup column is unavailable: " + lookup.Table + "." + column);
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }
}
