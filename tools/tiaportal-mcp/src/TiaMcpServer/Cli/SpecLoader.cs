using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace TiaMcpServer.Cli
{
    /// <summary>
    /// Loads a project spec from a .json (pass-through) or .yaml/.yml file and returns a JSON
    /// string suitable for <c>McpServer.ScaffoldProject</c> / <c>McpServer.PatchProject</c>.
    /// JSON is the canonical, zero-ambiguity form (AIs should emit it); YAML is a human convenience.
    /// </summary>
    public static class SpecLoader
    {
        public static string LoadAsJson(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"spec file not found: {path}");

            var text = File.ReadAllText(path); // ReadAllText strips a UTF-8 BOM if present
            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".json")
                return text; // pass-through, no YAML round-trip

            if (ext == ".yaml" || ext == ".yml")
                return YamlToJson(text);

            // Unknown extension: sniff. Leading { or [ means JSON, otherwise treat as YAML.
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                return text;
            return YamlToJson(text);
        }

        public static string YamlToJson(string yaml)
        {
            var graph = new DeserializerBuilder().Build().Deserialize<object?>(yaml);
            return ToNode(graph)?.ToJsonString() ?? "{}";
        }

        // YamlDotNet maps mappings to Dictionary<object,object>, sequences to List<object>,
        // and all scalars to string. We rebuild a JsonNode and infer scalar types so that
        // the JSON we hand to ScaffoldProject re-parses numbers/bools correctly.
        private static JsonNode? ToNode(object? o)
        {
            switch (o)
            {
                case null:
                    return null;
                case IDictionary<object, object> map:
                {
                    var obj = new JsonObject();
                    foreach (var kv in map)
                        obj[Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? ""] = ToNode(kv.Value);
                    return obj;
                }
                case string s:
                    return InferScalar(s);
                case IEnumerable<object> list:
                {
                    var arr = new JsonArray();
                    foreach (var item in list)
                        arr.Add(ToNode(item));
                    return arr;
                }
                default:
                    return JsonValue.Create(o);
            }
        }

        private static JsonNode? InferScalar(string s)
        {
            if (s.Length == 0) return JsonValue.Create("");
            switch (s)
            {
                case "null": case "Null": case "NULL": case "~": return null;
                case "true": case "True": case "TRUE": return JsonValue.Create(true);
                case "false": case "False": case "FALSE": return JsonValue.Create(false);
            }
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return JsonValue.Create(l);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return JsonValue.Create(d);
            return JsonValue.Create(s);
        }
    }
}
