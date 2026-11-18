using System.Text.Json;
using System.Text.Json.Nodes;
using Protocol;

namespace ProjectModel;

internal static class ProjectOptionResolver
{
    internal static ResolvedProjectOptions Resolve(
        ProjectDefinition project, TaskDefinition task, TaskConfiguration config, ISet<string> passwords,
        IReadOnlyDictionary<(string Option, string Input), string>? inputDrafts)
    {
        var pipelineOverrides = new JsonArray { ParseObject(task.PipelineOverride) };

        var globalValues = new JsonObject();
        var taskValues = new JsonObject();
        ResolveScope(project, project.GlobalOptions, config, globalValues, pipelineOverrides, inputDrafts);
        ResolveScope(project, task.Options, config, taskValues, pipelineOverrides, inputDrafts);
        return new ResolvedProjectOptions(ProtocolJson.ToElement(MaskPasswords(project, globalValues, passwords)),
            ProtocolJson.ToElement(MaskPasswords(project, taskValues, passwords)),
            ProtocolJson.ToElement(pipelineOverrides));
    }

    // The program receives validated inputs as entered. Mask passwords in the separate record that reaches logs.
    internal static (JsonElement Values, JsonElement Argument) ResolvePretask(
        ProjectDefinition project, PretaskDefinition pretask, TaskConfiguration config, ISet<string> passwords,
        IReadOnlyDictionary<(string Option, string Input), string>? inputDrafts)
    {
        var values = new JsonObject();
        ResolveScope(project, pretask.Options, config, values, null, inputDrafts);
        var argument = ProtocolJson.ToElement(values);
        return (ProtocolJson.ToElement(MaskPasswords(project, values, passwords)), argument);
    }

    // Resolved values are keyed by option name, so every filled password input can be found by its definition.
    // Masking also collects each original value, which telemetry must redact from whatever it uploads.
    private static JsonObject MaskPasswords(ProjectDefinition project, JsonObject resolvedValues,
        ISet<string> passwords)
    {
        foreach (var (optionName, node) in resolvedValues.ToArray()) {
            if (node is not JsonObject inputs) {
                continue;
            }
            foreach (var input in project.Options[optionName].Inputs.Where(input => input.Password)) {
                if (inputs[input.Name]?.GetValue<string>() is { Length: > 0 } value) {
                    passwords.Add(value);
                    inputs[input.Name] = ProjectInputEditor.PasswordMask;
                }
            }
        }
        return resolvedValues;
    }

    private static void ResolveScope(
        ProjectDefinition project, IReadOnlyList<string> optionNames, TaskConfiguration config,
        JsonObject resolvedValues, JsonArray? pipelineOverrides,
        IReadOnlyDictionary<(string Option, string Input), string>? inputDrafts)
    {
        foreach (var optionName in optionNames) {
            var option = project.Options[optionName];
            if (option.Kind == ProjectOptionKind.Input) {
                var inputs = ExplicitOptionIntent.ReadInputs(option, config);
                var values = new JsonObject();
                foreach (var input in inputs) {
                    if (inputDrafts is not null && inputDrafts.TryGetValue((option.Name, input.Name), out var draft)
                        && input.CheckValue(draft) is { } draftError) {
                        throw new InvalidOperationException($"参数“{option.Label} · {input.Label}”：{draftError}");
                    }
                    if (input.Error is { } error) {
                        throw new InvalidOperationException($"参数“{option.Label}”：{error}");
                    }
                    values[input.Name] = input.Value;
                }
                resolvedValues[optionName] = values;
                pipelineOverrides?.Add(CreateTemplatedOverride(option.PipelineOverride,
                    inputs.ToDictionary(input => input.Name, input => input.ParsedValue)));
                continue;
            }

            var active = ExplicitOptionIntent.ReadActiveCases(option, config);
            if (ProjectOptionEditor.CheckSelectionCount(active.Count, option.MinCount, option.MaxCount) is { } reason) {
                throw new InvalidOperationException($"参数“{option.Label}”：{reason}");
            }
            resolvedValues[optionName] = option.Kind == ProjectOptionKind.Checkbox
                ? new JsonArray(active.Select(item => (JsonNode)item.Name).ToArray()) : (JsonNode)active[0].Name;
            foreach (var selected in active) {
                pipelineOverrides?.Add(ParseObject(selected.PipelineOverride));
                ResolveScope(project, selected.Options, config, resolvedValues, pipelineOverrides, inputDrafts);
            }
        }
    }

    private static JsonObject CreateTemplatedOverride(
        JsonElement template, IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        var node = ParseObject(template);
        Substitute(node, substitutions);
        return node;
    }

    private static void Substitute(JsonNode? node, IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        if (node is JsonObject obj) {
            foreach (var property in obj.ToArray()) {
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text)) {
                    obj[property.Key] = SubstituteString(text, substitutions);
                } else {
                    Substitute(property.Value, substitutions);
                }
            }
        } else if (node is JsonArray array) {
            for (var index = 0; index < array.Count; index++) {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text)) {
                    array[index] = SubstituteString(text, substitutions);
                } else {
                    Substitute(array[index], substitutions);
                }
            }
        }
    }

    private static JsonNode? SubstituteString(string text, IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        if (text.Length > 2 && text[0] == '{' && text[^1] == '}') {
            var name = text[1..^1];
            if (substitutions.TryGetValue(name, out var exact)) {
                return exact?.DeepClone();
            }
        }

        var result = text;
        foreach (var (name, value) in substitutions) {
            var replacement = value switch {
                JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue) => stringValue,
                null => string.Empty,
                _ => value.ToJsonString(ProtocolJson.Options)
            };
            result = result.Replace($"{{{name}}}", replacement, StringComparison.Ordinal);
        }
        return JsonValue.Create(result);
    }

    private static JsonObject ParseObject(JsonElement element) =>
        JsonNode.Parse(element.GetRawText())!.AsObject();
}
