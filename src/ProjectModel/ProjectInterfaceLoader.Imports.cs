using System.Text.Json;

namespace ProjectModel;

internal static partial class ProjectInterfaceLoader
{
    private static readonly HashSet<string> ImportFields = ["task", "option", "preset", "group", "pretask", "global_option", "setting"];

    private static JsonElement MergeImports(JsonElement main, string projectRoot, IReadOnlyList<string> paths)
    {
        var sources = new List<JsonElement> { main };
        for (var i = 0; i < paths.Count; i++) {
            var location = $"$.import[{i}] ({paths[i]})";
            if (Path.IsPathRooted(paths[i])) {
                throw new InvalidDataException($"{location} 必须是相对路径。");
            }
            var path = Path.GetFullPath(paths[i], projectRoot);
            try {
                using var file = File.OpenRead(path);
                using var document = JsonDocument.Parse(file);
                var fragment = RequireObject(document.RootElement, location);
                RejectUnknownProperties(fragment, ImportFields, location);
                sources.Add(fragment.Clone());
            } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
                throw new FileNotFoundException($"{location} 引用的片段不存在。", path, ex);
            } catch (JsonException ex) {
                throw new InvalidDataException($"{location} 不是有效的 JSON。", ex);
            }
        }

        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var property in main.EnumerateObject()) {
            result[property.Name] = property.Value;
        }
        foreach (var field in ImportFields) {
            var values = sources.Where(source => source.TryGetProperty(field, out _))
                .Select(source => source.GetProperty(field)).ToList();
            if (values.Count == 0) {
                continue;
            }
            if (field == "option") {
                var options = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var value in values) {
                    foreach (var option in RequireObject(value, "$.option").EnumerateObject()) {
                        options[option.Name] = option.Value;
                    }
                }
                result[field] = options;
                continue;
            }

            var items = new List<JsonElement>();
            foreach (var value in values) {
                if (field == "pretask" && value.ValueKind == JsonValueKind.Object) {
                    items.Add(value);
                } else {
                    RequireArrayValue(value, $"$.{field}");
                    items.AddRange(value.EnumerateArray());
                }
            }
            result[field] = field switch {
                "group" => items.DistinctBy(item => RequireString(RequireObject(item, "$.group[]"), "name", "$.group[]")),
                "global_option" => items.DistinctBy(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString() : throw new InvalidDataException("$.global_option 只能包含 string。")),
                _ => items
            };
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
