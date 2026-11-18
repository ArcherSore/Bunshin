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
    private readonly bool _typed;

    internal ProjectInputEditor(InputDefinition input, string value, bool isExplicit, bool typed)
    {
        _definition = input;
        _typed = typed;
        Value = value;
        IsExplicit = isExplicit;
        Error = CheckValue(value);
    }

    public const string PasswordMask = "••••••";
    public string Name => _definition.Name;
    public string Label => _definition.Label;
    public string Description => _definition.Description;
    public string DefaultValue => _definition.Default;
    public string? Verify => _definition.Verify;
    public string? PatternMessage => _definition.PatternMessage;
    public ProjectInputType InputType => _definition.InputType;
    public bool IsPassword => _definition.Password;
    public string Value { get; }
    public bool IsExplicit { get; }
    public string? Error { get; }
    public string? CheckValue(string value) => ProjectInputValue.CheckValue(_definition, value, _typed);
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

    public bool IsBelowMinimum => SelectedCases.Count < MinCount;
    public string? SelectionCountError => IsBelowMinimum
        ? $"至少选 {MinCount} 项，当前选了 {SelectedCases.Count} 项。请再选 {MinCount - SelectedCases.Count} 项。"
        : MaxCount is { } max && SelectedCases.Count > max
            ? $"最多选 {max} 项，当前选了 {SelectedCases.Count} 项。请取消 {SelectedCases.Count - max} 项。"
            : null;

    // Why a Run cannot start with this option yet, or null when it can.
    public string? StartBlocker => SelectionCountError
        ?? Inputs.Select(input => input.Error).FirstOrDefault(error => error is not null);
}

public sealed record ProjectConfigurationView(
    IReadOnlyList<ProjectOptionEditor> GlobalOptions, IReadOnlyList<ProjectOptionEditor> TaskOptions);

internal static partial class ExplicitOptionIntent
{
    internal static IReadOnlyDictionary<string, string> ReadInputs(OptionDefinition option, TaskConfiguration config)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.ExplicitOptions.TryGetValue(option.Name, out var intent)) {
            foreach (var field in intent.GetProperty("Inputs").EnumerateObject()) {
                var value = field.Value.GetString()!;
                if ((option.IsPassword(field.Name) ? Unprotect(value) : value) is { } read) {
                    result.Add(field.Name, read);
                }
            }
        }
        return result;
    }

    // Both the editor and the Run resolve choices in declaration order from the reconciled configuration.
    internal static IReadOnlyList<CaseDefinition> ReadActiveCases(OptionDefinition option, TaskConfiguration config)
    {
        var saved = config.ExplicitOptions.TryGetValue(option.Name, out var intent);
        IEnumerable<string> names = option.Kind == OptionDefinitionKind.Checkbox
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
