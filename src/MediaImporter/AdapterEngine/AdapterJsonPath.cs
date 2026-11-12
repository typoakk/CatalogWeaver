using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MediaImporter;

/// <summary>A bounded JSONPath subset. Quoted property names never become indexes or wildcards.</summary>
public static class AdapterJsonPath
{
    private enum TokenKind { Property, Index, Wildcard }
    private readonly record struct Token(TokenKind Kind, string? Name = null, int Index = 0);

    public static IReadOnlyList<JsonElement> Select(JsonElement root, string path)
    {
        List<JsonElement> current = [root];
        foreach (var token in Parse(path))
        {
            var next = new List<JsonElement>();
            foreach (var node in current)
            {
                if (token.Kind == TokenKind.Wildcard)
                {
                    if (node.ValueKind == JsonValueKind.Array) next.AddRange(node.EnumerateArray());
                    else if (node.ValueKind == JsonValueKind.Object) next.AddRange(node.EnumerateObject().Select(x => x.Value));
                }
                else if (token.Kind == TokenKind.Index)
                {
                    if (node.ValueKind == JsonValueKind.Array && token.Index < node.GetArrayLength()) next.Add(node[token.Index]);
                }
                else if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(token.Name!, out var value))
                    next.Add(value);
                if (next.Count > 100_000) throw new InvalidDataException("JSON mapping produced too many values.");
            }
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

    private static Token[] Parse(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '$' || path.Length > 2048)
            throw new InvalidDataException("A JSON path must start with $ and contain at most 2048 characters.");
        var result = new List<Token>();
        for (var index = 1; index < path.Length;)
        {
            if (path[index] == '.')
            {
                var start = ++index;
                while (index < path.Length && path[index] != '.' && path[index] != '[') index++;
                if (start == index) throw new InvalidDataException("Empty JSON path member.");
                var member = path[start..index];
                result.Add(member == "*" ? new(TokenKind.Wildcard) : new(TokenKind.Property, member));
            }
            else if (path[index] == '[')
            {
                index++;
                if (index >= path.Length) throw new InvalidDataException("Unclosed JSON path index.");
                if (path[index] is '\'' or '"')
                {
                    var quote = path[index++];
                    var name = new StringBuilder();
                    var closed = false;
                    while (index < path.Length)
                    {
                        var value = path[index++];
                        if (value == quote) { closed = true; break; }
                        if (value == '\\')
                        {
                            if (index >= path.Length) throw new InvalidDataException("Unclosed JSON path escape.");
                            var escaped = path[index++];
                            if (escaped != quote && escaped != '\\') throw new InvalidDataException("Unsupported JSON path escape.");
                            value = escaped;
                        }
                        name.Append(value);
                    }
                    if (!closed || index >= path.Length || path[index++] != ']') throw new InvalidDataException("Unclosed quoted JSON path member.");
                    result.Add(new(TokenKind.Property, name.ToString()));
                }
                else
                {
                    var close = path.IndexOf(']', index);
                    if (close < 0) throw new InvalidDataException("Unclosed JSON path index.");
                    var member = path[index..close];
                    if (member == "*") result.Add(new(TokenKind.Wildcard));
                    else if (int.TryParse(member, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)) result.Add(new(TokenKind.Index, Index: numeric));
                    else throw new InvalidDataException("Unsupported JSON path index.");
                    index = close + 1;
                }
            }
            else throw new InvalidDataException("Unsupported JSON path syntax.");
            if (result.Count > 48) throw new InvalidDataException("JSON path is too deep.");
        }
        return result.ToArray();
    }
}
