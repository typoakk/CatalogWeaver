using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaImporter;

/// <summary>Checks the meanings of draft adapter instructions, independently of any provider.</summary>
internal static class AdapterSemanticValidation
{
    internal static readonly string[] Fields = ["externalId", "title", "originalTitle", "alternativeTitles", "description",
        "genres", "tags", "creator", "year", "rating", "episodes", "releaseStatus", "country", "language"];
    private static readonly Regex Template = new(@"\{\{([A-Za-z][A-Za-z0-9_:.-]*)(?:\|(upper|lower))?\}\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex VariableName = new(@"\A[A-Za-z][A-Za-z0-9_:.-]*\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static IEnumerable<string> Validate(AdapterDefinition definition, string mediaRoot)
    {
        var errors = new List<string>();
        var names = Fields.ToHashSet(StringComparer.Ordinal);
        foreach (var name in definition.Mapping.Fields.Keys)
            if (!names.Contains(name)) errors.Add($"$.mapping.fields.{name}: unknown normalized field.");
        if (!definition.Mapping.Fields.ContainsKey("title")) errors.Add("$.mapping.fields.title: a title mapping is required.");
        foreach (var name in definition.Variables.Keys)
            if (!VariableName.IsMatch(name) || name.StartsWith("secret:", StringComparison.Ordinal)) errors.Add($"$.variables.{name}: invalid or reserved variable name.");
        var destination = definition.Destinations.Files.Directory;
        if (!string.IsNullOrWhiteSpace(destination))
        {
            var staging = Path.Combine(Path.GetFullPath(mediaRoot), ".staging");
            var resolved = Path.GetFullPath(Path.IsPathRooted(destination) ? destination : Path.Combine(mediaRoot, destination));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (resolved.Equals(staging, comparison) || resolved.StartsWith(staging + Path.DirectorySeparatorChar, comparison))
                errors.Add("$.destinations.files.directory: .staging is reserved for temporary files.");
        }
        var needed = definition.Mapping.Fields.Values.Select(field => field.Request)
            .Concat(definition.Mapping.Assets.Where(pair => definition.AssetPolicies.ContainsKey(pair.Key))
                .SelectMany(pair => pair.Value.Sources.Length > 0 ? pair.Value.Sources.Select(source => source.Request) : [pair.Value.Request]))
            .ToHashSet(StringComparer.Ordinal);
        var itemVariables = new HashSet<string>(definition.Variables.Keys, StringComparer.Ordinal) { "externalId" };
        foreach (var (name, request) in definition.Requests)
        {
            var pointer = "$.requests." + name;
            if (request.ResponseKind is not (null or "object" or "array")) errors.Add(pointer + ".responseKind: expected object, array or null.");
            var discovery = name == definition.Discovery.Request;
            var available = discovery ? new HashSet<string>(definition.Variables.Keys, StringComparer.Ordinal) { "pageSize",
                definition.Discovery.Pagination.Type == "page" ? "page" : "cursor" } : new HashSet<string>(itemVariables, StringComparer.Ordinal);
            foreach (var variable in request.AlternativeVariables.Keys)
            {
                if (!VariableName.IsMatch(variable) || variable.StartsWith("secret:", StringComparison.Ordinal)) errors.Add(pointer + ".alternativeVariables." + variable + ": invalid or reserved variable name.");
                available.Add(variable);
            }
            RequestTemplates(request, pointer, available);
            if (discovery && needed.Contains(name))
            {
                // A single request may be referenced by both modes. It must work in both
                // scopes; discovery pagination variables are not injected into item reads.
                var itemScope = new HashSet<string>(itemVariables, StringComparer.Ordinal);
                itemScope.UnionWith(request.AlternativeVariables.Keys);
                RequestTemplates(request, pointer, itemScope);
            }
            if (request.Supplement is { } supplement)
            {
                foreach (var variable in supplement.Variables.Keys)
                {
                    if (!VariableName.IsMatch(variable) || variable.StartsWith("secret:", StringComparison.Ordinal)) errors.Add(pointer + ".supplement.variables." + variable + ": invalid or reserved variable name.");
                }
                foreach (var (variable, path) in supplement.CaptureVariables)
                {
                    if (!VariableName.IsMatch(variable) || variable.StartsWith("secret:", StringComparison.Ordinal)) errors.Add(pointer + ".supplement.captureVariables." + variable + ": invalid or reserved variable name.");
                    CheckPath(path, pointer + ".supplement.captureVariables." + variable, available, false);
                }
                for (var index = 0; index < supplement.FillPaths.Length; index++)
                    if (!Regex.IsMatch(supplement.FillPaths[index], @"^\$\.[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
                        errors.Add(pointer + $".supplement.fillPaths[{index}]: only a direct object property can be filled.");
                Paths(supplement.WhenMissingPaths, pointer + ".supplement.whenMissingPaths", available, false);
                Paths(supplement.FillPaths, pointer + ".supplement.fillPaths", available, false);
                for (var index = 0; index < supplement.WhenMissingGroups.Length; index++)
                    Paths(supplement.WhenMissingGroups[index], pointer + $".supplement.whenMissingGroups[{index}]", available, false);
            }
            if (needed.Contains(name))
            {
                itemVariables.UnionWith(request.AlternativeVariables.Keys);
                if (request.Supplement is { } capture)
                {
                    itemVariables.UnionWith(capture.CaptureVariables.Keys);
                    itemVariables.Add("resultLanguage");
                }
            }
        }
        CheckTemplate(definition.SourceUrlTemplate, "$.sourceUrlTemplate", itemVariables, false);
        CheckPath(definition.Discovery.ItemsPath, "$.discovery.itemsPath", [], false);
        CheckPath(definition.Discovery.ExternalIdPath, "$.discovery.externalIdPath", [], false);
        CheckPath(definition.Discovery.NamePath, "$.discovery.namePath", [], false);
        CheckPath(definition.Discovery.ModifiedPath, "$.discovery.modifiedPath", [], false);
        CheckPath(definition.Discovery.Pagination.NextCursorPath, "$.discovery.pagination.nextCursorPath", [], false);
        CheckPath(definition.Discovery.Pagination.HasMorePath, "$.discovery.pagination.hasMorePath", [], false);
        foreach (var (name, mapping) in definition.Mapping.Fields)
        {
            var pointer = "$.mapping.fields." + name;
            CheckPath(mapping.Path, pointer + ".path", itemVariables);
            Paths(mapping.Paths, pointer + ".paths", itemVariables);
            CheckPath(mapping.ItemsPath, pointer + ".itemsPath", [], false);
            CheckTemplate(mapping.Constant, pointer + ".constant", itemVariables, false);
        }
        foreach (var (role, mapping) in definition.Mapping.Assets)
        {
            var sources = mapping.Sources.Length > 0 ? mapping.Sources : [mapping];
            for (var index = 0; index < sources.Length; index++)
            {
                var source = sources[index];
                var pointer = "$.mapping.assets." + role + (mapping.Sources.Length > 0 ? $".sources[{index}]" : "");
                CheckPath(source.ItemsPath, pointer + ".itemsPath", [], false);
                Paths(source.ItemsPaths, pointer + ".itemsPaths", [], false);
                CheckPath(source.UrlPath, pointer + ".urlPath", [], false);
                Paths(source.UrlPaths, pointer + ".urlPaths", [], false);
                CheckPath(source.TitlePath, pointer + ".titlePath", [], false);
                CheckPath(source.FormatPath, pointer + ".formatPath", [], false);
                Paths(source.FilePaths, pointer + ".filePaths", [], false);
                for (var template = 0; template < source.UrlTemplates.Length; template++)
                    CheckTemplate(source.UrlTemplates[template], pointer + $".urlTemplates[{template}]", itemVariables, false);
                CheckTemplate(source.RequiredFormatPrefix, pointer + ".requiredFormatPrefix", itemVariables, false);
                CheckTemplate(source.RequiredFilenamePrefix, pointer + ".requiredFilenamePrefix", itemVariables, false);
            }
        }
        return errors;

        void RequestTemplates(RequestDefinition request, string pointer, HashSet<string> available)
        {
            CheckTemplate(request.Path, pointer + ".path", available, true);
            foreach (var (name, value) in request.Headers) CheckTemplate(value, pointer + ".headers." + name, available, true);
            foreach (var (name, value) in request.Query) JsonTemplates(value, pointer + ".query." + name, available);
            if (request.Body is { } body) JsonTemplates(body, pointer + ".body", available);
            CheckPath(request.ResponseRootPath, pointer + ".responseRootPath", available);
            CheckPath(request.AvailabilityPath, pointer + ".availabilityPath", available);
            CheckPath(request.IdentityPath, pointer + ".identityPath", available);
            Paths(request.IdentityPaths, pointer + ".identityPaths", available);
            Paths(request.RequiredPaths, pointer + ".requiredPaths", available);
            CheckPath(request.SuccessPath, pointer + ".successPath", [], false);
            CheckPath(request.FilterPath, pointer + ".filterPath", [], false);
        }
        void JsonTemplates(JsonElement value, string pointer, HashSet<string> available)
        {
            if (value.ValueKind == JsonValueKind.String) CheckTemplate(value.GetString(), pointer, available, true);
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var member in value.EnumerateObject()) JsonTemplates(member.Value, pointer + "." + member.Name, available);
            else if (value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var element in value.EnumerateArray()) JsonTemplates(element, pointer + $"[{index++}]", available);
            }
        }
        void CheckTemplate(string? value, string pointer, HashSet<string> available, bool secrets)
        {
            if (value is null) return;
            var remainder = Template.Replace(value, match =>
            {
                var variable = match.Groups[1].Value;
                if (variable.StartsWith("secret:", StringComparison.Ordinal))
                {
                    if (!secrets || !VariableName.IsMatch(variable[7..])) errors.Add(pointer + ": secret references are only allowed in request inputs.");
                }
                else if (!available.Contains(variable)) errors.Add(pointer + ": unknown or unavailable template variable '" + variable + "'.");
                return "0";
            });
            if (remainder.Contains("{{", StringComparison.Ordinal) || remainder.Contains("}}", StringComparison.Ordinal))
                errors.Add(pointer + ": malformed template expression.");
        }
        void CheckPath(string? value, string pointer, HashSet<string> available, bool templates = true)
        {
            if (value is null) return;
            CheckTemplate(value, pointer, templates ? available : [], false);
            if (!templates && value.Contains("{{", StringComparison.Ordinal)) errors.Add(pointer + ": templates are not supported in this path.");
            try { AdapterJsonPath.Validate(Template.Replace(value, "0")); }
            catch (InvalidDataException) { errors.Add(pointer + ": unsupported JSON path."); }
        }
        void Paths(string[] values, string pointer, HashSet<string> available, bool templates = true)
        {
            for (var index = 0; index < values.Length; index++) CheckPath(values[index], pointer + $"[{index}]", available, templates);
        }
    }
}
