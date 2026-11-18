using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectModel;

public enum ProjectOptionKind { Input, Select, Switch, Checkbox }

// The editor an input asks for; the value is a plain string either way. A password is never a file or folder.
public enum ProjectInputType { Text, Password, File, Directory }

public sealed class ProjectInputEditor
{
    private readonly InputDefinition _definition;

    internal ProjectInputEditor(InputDefinition input, string value, bool isExplicit)
    {
        _definition = input;
        Value = value;
        IsExplicit = isExplicit;
        Error = ProjectInputValue.Parse(input, value, out var parsed);
        ParsedValue = parsed;
    }

    public const string PasswordMask = "••••••";
    public string Name => _definition.Name;
    public string Label => _definition.Label;
    public string Description => _definition.Description;
    public string DefaultValue => _definition.Default;
    public string? Verify => _definition.Verify?.ToString();
    public string? PatternMessage => _definition.PatternMessage;
    public ProjectInputType InputType => _definition.InputType;
    public bool IsPassword => _definition.Password;
    public string Value { get; }
    public bool IsExplicit { get; }
    public string? Error { get; }
    internal JsonNode? ParsedValue { get; }
    public string? CheckValue(string value) => ProjectInputValue.Parse(_definition, value, out _);
}

public sealed record ProjectCaseEditor(string Name, string Label, string Description);

public sealed record ProjectOptionEditor(
    string Name, string Label, string Description, ProjectOptionKind Kind, bool IsExplicit,
    string? SelectedCase, string? DefaultCase,
    IReadOnlyList<ProjectCaseEditor> Cases, IReadOnlyList<ProjectInputEditor> Inputs,
    IReadOnlyList<ProjectOptionEditor> ActiveChildren)
{
    // The active cases in declaration order: the one select/switch case, or every checked checkbox case.
    public IReadOnlyList<string> SelectedCases { get; init; } = [];
    public int MinCount { get; init; }
    public int? MaxCount { get; init; }

    public string? SelectionCountError => CheckSelectionCount(SelectedCases.Count, MinCount, MaxCount);
    internal static string? CheckSelectionCount(int count, int min, int? max) => count < min
        ? $"至少选 {min} 项，当前选了 {count} 项。请再选 {min - count} 项。"
        : count > max ? $"最多选 {max} 项，当前选了 {count} 项。请取消 {count - max} 项。" : null;
}

public sealed record ProjectConfigurationView(
    IReadOnlyList<ProjectOptionEditor> GlobalOptions, IReadOnlyList<ProjectOptionEditor> TaskOptions)
{
    public IEnumerable<(ProjectOptionEditor Option, int Depth, bool Global)> ActiveOptions => EnumerateActiveOptions();

    private IEnumerable<(ProjectOptionEditor Option, int Depth, bool Global)> EnumerateActiveOptions()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Walk(GlobalOptions, 0, true, visited).Concat(Walk(TaskOptions, 0, false, visited))) {
            yield return item;
        }
    }

    private static IEnumerable<(ProjectOptionEditor Option, int Depth, bool Global)> Walk(
        IReadOnlyList<ProjectOptionEditor> options, int depth, bool global, HashSet<string> visited)
    {
        foreach (var option in options) {
            if (!visited.Add(option.Name)) {
                continue;
            }
            yield return (option, depth, global);
            foreach (var child in Walk(option.ActiveChildren, depth + 1, global, visited)) {
                yield return child;
            }
        }
    }
}

internal static partial class ExplicitOptionIntent
{
    internal static IReadOnlyList<ProjectInputEditor> ReadInputs(OptionDefinition option, TaskConfiguration config)
    {
        var saved = config.ExplicitOptions.TryGetValue(option.Name, out var intent)
            ? intent.GetProperty("Inputs") : (JsonElement?)null;
        return option.Inputs.Select(input => {
            string? value = null;
            if (saved is { } fields && fields.TryGetProperty(input.Name, out var field)) {
                value = field.GetString()!;
                if (input.Password) {
                    value = Unprotect(value);
                }
            }
            return new ProjectInputEditor(input, value ?? input.Default, value is not null);
        }).ToArray();
    }

    // Both the editor and the Run resolve choices in declaration order from the reconciled configuration.
    internal static IReadOnlyList<CaseDefinition> ReadActiveCases(OptionDefinition option, TaskConfiguration config)
    {
        var saved = config.ExplicitOptions.TryGetValue(option.Name, out var intent);
        IEnumerable<string> names = option.Kind == ProjectOptionKind.Checkbox
            ? saved ? ReadCaseNames(intent) : option.DefaultCases
            : [saved ? intent.GetProperty("SelectedCase").GetString()! : option.DefaultCase!];
        return option.Cases.Where(item => names.Contains(item.Name)).ToArray();
    }

    private static string[] ReadCaseNames(JsonElement intent) =>
        intent.GetProperty("SelectedCases").EnumerateArray().Select(item => item.GetString()!).ToArray();

    internal static JsonElement CreateInputs(OptionDefinition option, IReadOnlyDictionary<string, string> values)
    {
        var inputs = new JsonObject();
        foreach (var (name, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            inputs[name] = option.IsPassword(name) ? Protect(value) : value;
        }
        return CreateStoredInputs(option, inputs);
    }

    private static JsonElement CreateStoredInputs(OptionDefinition option, JsonObject inputs)
    {
        var passwords = inputs.Where(pair => option.IsPassword(pair.Key)).Select(pair => (JsonNode)pair.Key).ToArray();
        return ToElement(new JsonObject { ["Inputs"] = inputs, ["PasswordInputs"] = new JsonArray(passwords) });
    }

    // Update only the named field, preserving sibling values including their original password ciphertext.
    internal static JsonElement? UpdateInput(OptionDefinition option, TaskConfiguration config, string name,
        string? value)
    {
        var inputs = config.ExplicitOptions.TryGetValue(option.Name, out var current)
            ? current.GetProperty("Inputs").Deserialize<JsonObject>()! : new JsonObject();
        if (value is null) {
            inputs.Remove(name);
        } else {
            inputs[name] = option.IsPassword(name) ? Protect(value) : value;
        }
        return inputs.Count == 0 ? null : CreateStoredInputs(option, inputs);
    }

    private static string Protect(string value) => value.Length == 0 ? value : Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string value)
    {
        try {
            return value.Length == 0 ? value : Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        } catch (Exception exception) when (exception is FormatException or CryptographicException) {
            return null;
        }
    }

    internal static JsonElement CreateSelectedCase(OptionDefinition option, string selectedCase) =>
        ToElement(new JsonObject { ["Kind"] = option.Kind.ToString(), ["SelectedCase"] = selectedCase });

    internal static JsonElement CreateSelectedCases(IEnumerable<string> selectedCases) => ToElement(new JsonObject {
        ["SelectedCases"] = new JsonArray(selectedCases.Select(name => (JsonNode)name).ToArray())
    });

    private static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);
}
