using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace MediaImporter.Database;

public sealed partial class PostgresDatabaseWriter
{
    private sealed record UnknownValue(string Reference);
    private sealed class SkipValue { public static readonly SkipValue Instance = new(); }
    private sealed record Evaluation(AdapterItem Item, IReadOnlyList<AssetResult> Assets, string MediaId,
        DateTime UpdatedUtc, Dictionary<string, object?> Returned, DatabaseExecutionContext Execution,
        string? UrlTemplate, DatabaseSchema Schema, AssetResult? Asset = null, object? Row = null,
        int Index = 0, string? Role = null);

    private static async Task<object?> EvaluateAsync(DatabaseValue value, Evaluation context,
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        if (value.First && value.Source != "assets" && context.Asset is null)
            context = context with { Asset = context.Assets.Where(asset => (value.Role ?? context.Role) is not { } role || asset.Role == role)
                .OrderBy(asset => asset.Position).FirstOrDefault(), Role = value.Role ?? context.Role };
        if (value.WhenRole is { } gated && !Complete(context, gated)) return SkipValue.Instance;
        object? result;
        if (value.Coalesce.Length > 0)
        {
            result = null;
            foreach (var candidate in value.Coalesce)
            {
                result = await EvaluateAsync(candidate, context, connection, transaction, ct);
                if (result is not null && result is not SkipValue) break;
            }
        }
        else if (value.Merge.Length > 0)
        {
            var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var candidate in value.Merge)
            {
                var part = await EvaluateAsync(candidate, context, connection, transaction, ct);
                if (part is UnknownValue) return part;
                if (part is null or SkipValue) continue;
                var element = ToJson(part);
                if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Merge operands must be JSON objects.");
                foreach (var property in element.EnumerateObject()) merged[property.Name] = property.Value.Clone();
            }
            result = JsonSerializer.SerializeToElement(merged, Json);
        }
        else if (value.Condition is { } condition)
        {
            var left = await EvaluateAsync(condition.Left, context, connection, transaction, ct);
            var right = condition.Right is null ? null : await EvaluateAsync(condition.Right, context, connection, transaction, ct);
            if (left is UnknownValue) return left;
            if (right is UnknownValue) return right;
            var matches = condition.Operator switch
            {
                "isNull" => left is null, "notNull" => left is not null,
                "eq" => Equal(left, right), "ne" => !Equal(left, right),
                _ => throw new InvalidDataException("Unsupported condition operator.")
            };
            var branch = matches ? value.Then : value.Else;
            result = branch is null ? null : await EvaluateAsync(branch, context, connection, transaction, ct);
        }
        else if (value.Object is { } definition)
            result = await ObjectAsync(definition, context, connection, transaction, ct);
        else if (value.Lookup is { } lookup)
            result = await LookupAsync(lookup, context, connection, transaction, ct);
        else if (value.Template is { } template)
        {
            result = template;
            foreach (Match token in Regex.Matches(template, "\\{([^{}]+)\\}"))
            {
                var replacement = Source(token.Groups[1].Value, context);
                if (replacement is UnknownValue) return replacement;
                result = ((string)result).Replace(token.Value, replacement is null ? "" : StringValue(replacement), StringComparison.Ordinal);
            }
        }
        else if (value.Source == "assets")
        {
            var selected = context.Assets.Where(asset => (value.Role ?? context.Role) is not { } role || asset.Role == role)
                .OrderBy(asset => asset.Position).ToArray();
            var values = new List<object?>();
            foreach (var asset in selected)
            {
                var row = context with { Asset = asset, Role = asset.Role, Index = asset.Position };
                var projected = value.SelectValue is not null ? await EvaluateAsync(value.SelectValue, row, connection, transaction, ct)
                    : value.Select is null ? (object)JsonSerializer.SerializeToElement(asset, Json)
                    : await ObjectAsync(value.Select, row, connection, transaction, ct);
                if (projected is UnknownValue) return projected;
                values.Add(projected);
                if (value.First) break;
            }
            result = value.First ? values.FirstOrDefault() : JsonSerializer.SerializeToElement(values, Json);
        }
        else if (value.Source is { } source)
        {
            if (source.StartsWith("asset.", StringComparison.Ordinal) && value.First)
            {
                var asset = context.Assets.Where(asset => (value.Role ?? context.Role) is not { } role || asset.Role == role)
                    .OrderBy(asset => asset.Position).FirstOrDefault();
                result = asset is null ? null : Source(source, context with { Asset = asset, Role = asset.Role, Index = asset.Position });
            }
            else result = Source(source, context);
        }
        else result = value.Constant;

        result = UnwrapNull(result);
        if (result is UnknownValue or SkipValue) return result;
        if (value.Map is not null && result is not null)
            result = value.Map.TryGetValue(StringValue(result), out var replacement) ? UnwrapNull(replacement)
                : value.MapMissing == "null" ? null : result;
        if (value.Join is not null && result is not null)
        {
            var elements = ToJson(result);
            if (elements.ValueKind != JsonValueKind.Array) throw new InvalidDataException("join requires an array value.");
            result = string.Join(value.Join, elements.EnumerateArray().Select(element => StringValue(element)));
        }
        if (value.Limit is { } limit && result is not null)
        {
            var text = StringValue(result);
            result = text.Length > limit ? text[..limit] : text;
        }
        if (result is null && value.OnNull == "default") result = UnwrapNull(value.Default);
        return result;
    }

    private static async Task<object?> ObjectAsync(Dictionary<string, DatabaseValue> definition, Evaluation context,
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, expression) in definition)
        {
            var result = await EvaluateAsync(expression, context, connection, transaction, ct);
            if (result is UnknownValue) return result;
            if (result is not SkipValue) values[key] = result;
        }
        return JsonSerializer.SerializeToElement(values, Json);
    }

    private static async Task<object?> LookupAsync(DatabaseLookup lookup, Evaluation context,
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var table = context.Schema.Tables.Single(t => t.Schema == lookup.Schema && t.Name == lookup.Table);
        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        var clauses = new List<string>();
        foreach (var (column, expression) in lookup.Where)
        {
            var result = await EvaluateAsync(expression, context, connection, transaction, ct);
            if (result is UnknownValue) return result;
            if (result is SkipValue) throw new InvalidDataException("Lookup predicates cannot be role-gated.");
            clauses.Add(Quote(column) + " IS NOT DISTINCT FROM " + Parameter(command, table.Columns.Single(c => c.Name == column), result));
        }
        command.CommandText = "SELECT " + Quote(lookup.Column) + " FROM " + Qualified(table) + " WHERE " + string.Join(" AND ", clauses) + " LIMIT 2";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        object? resultValue = reader.IsDBNull(0) ? null : reader.GetValue(0);
        if (await reader.ReadAsync(ct)) throw new InvalidDataException("Lookup is ambiguous: " + lookup.Schema + "." + lookup.Table);
        if (resultValue is string json && table.Columns.Single(c => c.Name == lookup.Column).TypeName is "json" or "jsonb")
        { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
        return resultValue;
    }

    private static object? Source(string source, Evaluation context) => source switch
    {
        "mediaId" => context.MediaId, "updatedUtc" => context.UpdatedUtc,
        "item" => JsonSerializer.SerializeToElement(context.Item, Json),
        "adapterVersion" => context.Execution.AdapterVersion,
        "assetStatus" => JsonSerializer.SerializeToElement(context.Execution.AssetStatus ?? context.Item.AssetStatus, Json),
        "warnings" => JsonSerializer.SerializeToElement(context.Execution.Warnings
            ?? context.Item.Warnings.Concat(context.Assets.SelectMany(asset => asset.Warnings)).Distinct().ToArray(), Json),
        "assets" => JsonSerializer.SerializeToElement(context.Assets, Json),
        "row.value" => context.Row, "row.index" => context.Index,
        "asset.role" when context.Asset is null => context.Role,
        "asset.url" when context.Asset is not null => context.Asset.Hash is null ? context.Asset.SourceUrl
            : context.UrlTemplate is null ? context.Asset.RelativePath : context.UrlTemplate.Replace("{hash}", context.Asset.Hash)
                .Replace("{relativePath}", context.Asset.RelativePath ?? ""),
        _ when source.StartsWith("result.", StringComparison.Ordinal) => context.Returned.GetValueOrDefault(source[7..]),
        _ when source.StartsWith("item.", StringComparison.Ordinal) => JsonPath(JsonSerializer.SerializeToElement(context.Item, Json), source[5..]),
        _ when source.StartsWith("asset.", StringComparison.Ordinal) && context.Asset is not null
            => JsonPath(JsonSerializer.SerializeToElement(context.Asset, Json), source[6..]),
        _ => null
    };

    private static object? JsonPath(JsonElement element, string path)
    {
        foreach (var segment in path.Split('.'))
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element)) return null;
        return UnwrapNull(element);
    }
    private static bool Complete(Evaluation context, string role) => context.Item.AssetStatus.TryGetValue(role, out var status) && status == "complete";
    private static JsonElement ToJson(object value) => value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, Json);
    private static object? UnwrapNull(object? value) => value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? null : value;
    private static bool Equal(object? left, object? right) => left is null || right is null ? left is null && right is null
        : string.Equals(StringValue(left), StringValue(right), StringComparison.Ordinal);
}
