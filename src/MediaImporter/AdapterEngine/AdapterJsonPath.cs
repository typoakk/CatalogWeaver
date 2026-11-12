using System.Globalization;
using System.Text.Json;

namespace MediaImporter;

/// <summary>A bounded JSONPath subset for member access, indexes and collection wildcards.</summary>
public static class AdapterJsonPath
{
    public static IReadOnlyList<JsonElement> Select(JsonElement root, string path)
    {
        var tokens = Parse(path);
        List<JsonElement> current = [root];
        foreach (var token in tokens)
        {
            var next = new List<JsonElement>();
            foreach (var node in current)
            {
                if (token == "*")
                {
                    if (node.ValueKind == JsonValueKind.Array) next.AddRange(node.EnumerateArray());
                    else if (node.ValueKind == JsonValueKind.Object) next.AddRange(node.EnumerateObject().Select(x => x.Value));
                }
                else if (token.StartsWith('#'))
                {
                    var index = int.Parse(token.AsSpan(1), CultureInfo.InvariantCulture);
                    if (node.ValueKind == JsonValueKind.Array && index < node.GetArrayLength()) next.Add(node[index]);
                }
                else if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(token, out var value))
                    next.Add(value);
            }
            if (next.Count > 100_000) throw new InvalidDataException("JSON mapping produced too many values.");
            current = next;
        }
        return current;
    }

    public static void Validate(string path) => Parse(path);

    public static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => ""
    };

    private static string[] Parse(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '$' || path.Length > 2048)
            throw new InvalidDataException("A JSON path must start with $.");
        var result = new List<string>();
        for (var index = 1; index < path.Length;)
        {
            if (path[index] == '.')
            {
                var start = ++index;
                while (index < path.Length && path[index] != '.' && path[index] != '[') index++;
                if (start == index) throw new InvalidDataException("Empty JSON path member.");
                result.Add(path[start..index]);
            }
            else if (path[index] == '[')
            {
                var close = path.IndexOf(']', index + 1);
                if (close < 0) throw new InvalidDataException("Unclosed JSON path index.");
                var member = path[(index + 1)..close];
                if (member == "*") result.Add(member);
                else if (member.Length >= 2 && member[0] is '\'' or '"' && member[^1] == member[0])
                    result.Add(member[1..^1]);
                else if (int.TryParse(member, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric >= 0)
                    result.Add("#" + member);
                else throw new InvalidDataException("Unsupported JSON path index.");
                index = close + 1;
            }
            else throw new InvalidDataException("Unsupported JSON path syntax.");
        }
        if (result.Count > 48) throw new InvalidDataException("JSON path is too deep.");
        return result.ToArray();
    }
}
