using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using MediaImporter.Database;
using MediaImporter.Images;
using Microsoft.AspNetCore.Http.Metadata;

namespace MediaImporter;

/// <summary>OpenAPI 3.1 routes come from the actual endpoint table; CLR request schemas share serialization rules.</summary>
public static class ApiContract
{
    public static void Map(RouteGroupBuilder api, WebApplication app)
    {
        api.MapGet("/openapi.json", () => Results.Json(Build(app)));
        api.MapGet("/schemas/{name}", (string name) =>
        {
            if (name is not ("adapter.schema.json" or "encoder.schema.json" or "database.schema.json")) return Results.NotFound();
            var root = Path.Combine(app.Environment.ContentRootPath, "schemas");
            if (!Directory.Exists(root)) root = Path.Combine(AppContext.BaseDirectory, "schemas");
            return Results.File(Path.Combine(root, name), "application/schema+json");
        });
    }

    public static JsonObject Build(WebApplication app)
    {
        var schemas = new JsonObject();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        string Schema(Type type)
        {
            var name = type.Name;
            if (!schemas.ContainsKey(name))
            {
                var node = options.GetJsonSchemaAsNode(type);
                RewriteRefs(node, "#/components/schemas/" + name);
                schemas[name] = node;
            }
            return "#/components/schemas/" + name;
        }
        foreach (var type in new[] { typeof(AdapterDefinition), typeof(EncoderModuleDefinition), typeof(ImportResult),
            typeof(DatabaseDestination), typeof(ImportErrorInfo), typeof(StoredImportSummary), typeof(BackgroundLoopStatus) }) Schema(type);
        var paths = new JsonObject();
        foreach (var endpoint in ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText;
            if (raw is null) continue;
            var path = Regex.Replace(raw, @"\{([^}:]+):[^}]+\}", "{$1}");
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            if (paths[path] is not JsonObject operations) paths[path] = operations = new JsonObject();
            foreach (var method in methods)
            {
                var parameters = new JsonArray();
                foreach (var parameter in endpoint.RoutePattern.Parameters)
                    parameters.Add(new JsonObject { ["name"] = parameter.Name, ["in"] = "path", ["required"] = true,
                        ["schema"] = new JsonObject { ["type"] = "string" } });
                if (method == "GET" && (path.EndsWith("/items") || path.EndsWith("/jobs") || path.EndsWith("/export-reservations")))
                {
                    parameters.Add(Query("limit", "integer", 1, 100));
                    parameters.Add(Query("cursor", "string"));
                }
                if (method == "GET" && path == "/api/v1/importer/jobs") { parameters.Add(Query("provider", "string")); parameters.Add(Query("state", "string")); }
                if (method == "PUT" || path.EndsWith("/enabled")) parameters.Add(new JsonObject
                {
                    ["name"] = "If-Match", ["in"] = "header", ["required"] = false,
                    ["description"] = "Existing configurations require their strong ETag; missing returns 428, stale returns 412.",
                    ["schema"] = new JsonObject { ["type"] = "string" }
                });
                var responses = new JsonObject { ["200"] = Response("Operation result"), ["202"] = Response("Accepted into the durable queue"),
                    ["400"] = Response("Invalid input"), ["409"] = Response("Conflicting active operation"), ["422"] = Response("Source or target rejected the operation"),
                    ["401"] = Response("Management key required"), ["404"] = Response("Not found"),
                    ["412"] = Response("Stale configuration ETag"), ["428"] = Response("If-Match required for an update"),
                    ["503"] = Response("Temporary source or database failure"), ["504"] = Response("Execution deadline exceeded"),
                    ["500"] = Response("Internal failure; see traceId") };
                var responseSchema = SuccessSchema(path, method);
                var successStatus = method == "POST" && (path.EndsWith("/jobs") || path.EndsWith("/discovery") || path.EndsWith("/retry")) ? "202" : "200";
                if (responseSchema is not null) responses[successStatus]!["content"] = new JsonObject
                { ["application/json"] = new JsonObject { ["schema"] = responseSchema } };
                if (method == "GET" && (path == "/api/v1/importer/items" || path == "/api/v1/importer/jobs")) responses["200"]!["headers"] = new JsonObject
                { ["X-Next-Cursor"] = new JsonObject { ["description"] = "Opaque continuation cursor; absent on the last page.",
                    ["schema"] = new JsonObject { ["type"] = "string" } } };
                if ((path == "/api/v1/importer/adapters/{provider}" || path == "/api/v1/importer/encoders/{id}") && method is "GET" or "PUT"
                    || path == "/api/v1/importer/adapters/{provider}/enabled" && method == "POST")
                    responses["200"]!["headers"] = new JsonObject { ["ETag"] = new JsonObject
                    { ["description"] = "Strong configuration revision. Send it unchanged in If-Match when updating.",
                        ["schema"] = new JsonObject { ["type"] = "string" } } };
                var operation = new JsonObject { ["operationId"] = Regex.Replace(method + "_" + path, @"[^A-Za-z0-9_]", "_"),
                    ["parameters"] = parameters, ["responses"] = responses };
                if (path.StartsWith("/api/", StringComparison.Ordinal)) operation["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() });
                if (endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType is { } requestType)
                {
                    JsonObject bodySchema = requestType == typeof(JsonElement)
                        ? new JsonObject { ["$ref"] = Schema(path.Contains("/encoders/") ? typeof(EncoderModuleDefinition) : typeof(AdapterDefinition)) }
                        : new JsonObject { ["$ref"] = Schema(requestType) };
                    operation["requestBody"] = new JsonObject { ["required"] = true,
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = bodySchema } } };
                }
                operations[method.ToLowerInvariant()] = operation;
            }
        }
        return new JsonObject { ["openapi"] = "3.1.0", ["info"] = new JsonObject { ["title"] = "CatalogWeaver", ["version"] = "0.2.0" },
            ["paths"] = paths, ["components"] = new JsonObject { ["schemas"] = schemas,
                ["securitySchemes"] = new JsonObject { ["bearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" } } } };

        JsonObject Reference(Type type) => new() { ["$ref"] = Schema(type) };
        JsonObject ArrayOf(Type type) => new() { ["type"] = "array", ["items"] = Reference(type) };
        JsonObject? SuccessSchema(string path, string method)
        {
            if (method == "POST" && path.EndsWith("/jobs")) return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            { ["provider"] = new JsonObject { ["type"] = "string" }, ["enqueued"] = new JsonObject { ["type"] = "integer" } } };
            if (path.EndsWith("/retry")) return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            { ["id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" }, ["status"] = new JsonObject { ["type"] = "string" } } };
            if (path.Contains("/templates/")) return Reference(typeof(TemplateComparison));
            if (path.EndsWith("/preview")) return Reference(typeof(ImportResult));
            if (path.EndsWith("/items")) return ArrayOf(typeof(StoredImportSummary));
            if (path.EndsWith("/items/{mediaId}")) return Reference(typeof(ImportResult));
            if (path.EndsWith("/jobs")) return ArrayOf(typeof(ImportJobResponse));
            if (path.EndsWith("/jobs/{id}") || path.EndsWith("/jobs/{id}/cancel")) return Reference(typeof(ImportJobResponse));
            if (path.EndsWith("/discovery")) return method == "GET" ? ArrayOf(typeof(DiscoveryResponse)) : Reference(typeof(DiscoveryResponse));
            if (path.EndsWith("/export-reservations")) return Reference(typeof(ExportReservationPage));
            if (path.EndsWith("/export-reservations/release")) return Reference(typeof(ExportReleaseResult));
            if (path.EndsWith("/schema")) return Reference(typeof(DatabaseSchema));
            if (path.EndsWith("/database/plan")) return Reference(typeof(DatabaseChangePlan));
            if (path.EndsWith("/adapters") || path.EndsWith("/adapters/reload")) return ArrayOf(typeof(AdapterRegistration));
            if (path.EndsWith("/encoders") || path.EndsWith("/encoders/reload")) return ArrayOf(typeof(EncoderRegistration));
            if (path.EndsWith("/adapters/{provider}") && method == "GET") return Reference(typeof(AdapterDefinition));
            if (path.EndsWith("/encoders/{id}")) return Reference(method == "GET" ? typeof(EncoderModuleDefinition) : typeof(EncoderRegistration));
            return null;
        }
    }

    private static JsonObject Query(string name, string type, int? minimum = null, int? maximum = null)
    {
        var schema = new JsonObject { ["type"] = type };
        if (minimum.HasValue) schema["minimum"] = minimum.Value;
        if (maximum.HasValue) schema["maximum"] = maximum.Value;
        return new JsonObject { ["name"] = name, ["in"] = "query", ["schema"] = schema };
    }
    private static JsonObject Response(string description) => new() { ["description"] = description };
    private static void RewriteRefs(JsonNode? node, string prefix)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference) && reference.StartsWith('#'))
                obj["$ref"] = prefix + reference[1..];
            foreach (var pair in obj.ToArray()) RewriteRefs(pair.Value, prefix);
        }
        else if (node is JsonArray array) foreach (var child in array) RewriteRefs(child, prefix);
    }
}
