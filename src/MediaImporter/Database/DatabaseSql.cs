using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace MediaImporter.Database;

public sealed partial class PostgresDatabaseWriter
{
    private static string Parameter(NpgsqlCommand command, DatabaseColumnSchema column, object? value)
    {
        var name = "p" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        var parameter = new NpgsqlParameter { ParameterName = name };
        var type = column.TypeName;
        try
        {
        if (value is null)
        { parameter.Value = DBNull.Value; parameter.NpgsqlDbType = NpgsqlDbType.Unknown; }
        else if (type is "json" or "jsonb")
        {
            parameter.NpgsqlDbType = type == "jsonb" ? NpgsqlDbType.Jsonb : NpgsqlDbType.Json;
            parameter.Value = value is JsonElement json ? json.GetRawText() : JsonSerializer.Serialize(value, Json);
        }
        else if (type.StartsWith('_'))
        {
            var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, Json);
            if (json.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Array column requires a list: " + column.Name);
            parameter.Value = type switch
            {
                "_text" or "_varchar" => json.EnumerateArray().Select(element => StringValue(element)).ToArray(),
                "_int4" => json.EnumerateArray().Select(element => int.Parse(StringValue(element), CultureInfo.InvariantCulture)).ToArray(),
                "_int8" => json.EnumerateArray().Select(element => long.Parse(StringValue(element), CultureInfo.InvariantCulture)).ToArray(),
                "_uuid" => json.EnumerateArray().Select(element => Guid.Parse(StringValue(element))).ToArray(),
                _ => throw new InvalidDataException("Unsupported array type: " + type)
            };
        }
        else
        {
            var text = StringValue(value);
            if (column.MaximumLength is { } limit && text.EnumerateRunes().Count() > limit) throw new InvalidDataException("Value exceeds column length: " + column.Name);
            parameter.Value = type switch
            {
                "int2" => short.Parse(text, CultureInfo.InvariantCulture), "int4" => int.Parse(text, CultureInfo.InvariantCulture),
                "int8" => long.Parse(text, CultureInfo.InvariantCulture), "numeric" => decimal.Parse(text, CultureInfo.InvariantCulture),
                "float4" => float.Parse(text, CultureInfo.InvariantCulture), "float8" => double.Parse(text, CultureInfo.InvariantCulture),
                "bool" => bool.Parse(text), "uuid" => Guid.Parse(text), "date" => DateOnly.Parse(text, CultureInfo.InvariantCulture),
                "timestamp" => DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Unspecified),
                "timestamptz" => value is DateTime date ? date.ToUniversalTime() : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).UtcDateTime,
                _ => text
            };
            if (parameter.Value is string) parameter.NpgsqlDbType = NpgsqlDbType.Unknown;
        }
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("Invalid value for column " + column.Name + " (" + type + ").", error);
        }
        command.Parameters.Add(parameter);
        return "@" + name;
    }

    private static string StringValue(object value) => value is JsonElement element
        ? element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText()
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    private static bool ValidSource(string value)
    {
        if (value is "mediaId" or "updatedUtc" or "assets" or "item" or "adapterVersion" or "assetStatus" or "warnings" or "row.value" or "row.index") return true;
        var parts = value.Split('.');
        if (parts.Length < 2 || !parts.All(Identifier)) return false;
        if (parts[0] == "result") return parts.Length == 2;
        if (parts[0] == "asset") return parts.Length == 2 && AssetFields.Contains(parts[1]);
        return parts[0] == "item" && ItemFields.Contains(parts[1]) && (parts.Length == 2 || parts[1] == "metadata");
    }
    private static bool Identifier(string? value) => value is { Length: > 0 and <= 63 }
        && (char.IsLetter(value[0]) || value[0] == '_') && value.All(c => char.IsLetterOrDigit(c) || c == '_');
    private static string Quote(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Qualified(DatabaseTableSchema table) => Quote(table.Schema) + "." + Quote(table.Name);
}
