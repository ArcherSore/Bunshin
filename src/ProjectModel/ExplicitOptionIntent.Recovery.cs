using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectModel;

internal static partial class ExplicitOptionIntent
{
    // Check the stored format before using the PI. A damaged record must go through file recovery.
    internal static void ValidateStoredShape(string name, JsonElement intent)
    {
        bool Only(params string[] allowed) => intent.EnumerateObject().All(p => allowed.Contains(p.Name));
        static bool Names(JsonElement value) => value.ValueKind == JsonValueKind.Array
            && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(item.GetString()));

        if (intent.TryGetProperty("Inputs", out var inputs) && Only("Inputs", "PasswordInputs")
            && inputs.ValueKind == JsonValueKind.Object && inputs.EnumerateObject().Any()
            && inputs.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.String)
            && (!intent.TryGetProperty("PasswordInputs", out var passwords) || Names(passwords)
                && passwords.EnumerateArray().All(p => inputs.TryGetProperty(p.GetString()!, out _)))) {
            return;
        }
        if (intent.TryGetProperty("SelectedCase", out var single) && Only("Kind", "SelectedCase")
            && single.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(single.GetString())
            && (!intent.TryGetProperty("Kind", out var kind) || kind.ValueKind == JsonValueKind.String
                && kind.GetString() is nameof(ProjectOptionKind.Select) or nameof(ProjectOptionKind.Switch))) {
            return;
        }
        if (intent.TryGetProperty("SelectedCases", out var multiple) && Only("SelectedCases") && Names(multiple)) {
            return;
        }
        throw new InvalidDataException($"任务配置中选项 {name} 的保存格式无效。");
    }

    internal static IReadOnlyDictionary<string, JsonElement> Reconcile(TaskConfiguration config,
        ProjectDefinition project, ProjectDefinition? previousProject, List<string> changes)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, intent) in config.ExplicitOptions) {
            void Report(string message) => changes.Add($"配置“{config.Name}”的选项 {name} {message}");
            if (!project.Options.TryGetValue(name, out var option)) {
                Report("已从项目定义中移除");
                continue;
            }

            var previous = previousProject?.Options.GetValueOrDefault(name);
            if (!MatchesKind(intent, option.Kind, previous?.Kind)) {
                Report("类型已变化，采用新类型的项目默认值");
                continue;
            }
            var before = changes.Count;
            var updated = option.Kind == ProjectOptionKind.Input
                ? ReconcileInputs(intent, option, previous, Report) : ReconcileCases(intent, option, Report);
            if (updated is { } value) {
                result.Add(name, value);
                if (changes.Count == before && !JsonElement.DeepEquals(value, intent)) {
                    Report("已补全保存类型");
                }
            }
        }
        return result;
    }

    private static bool MatchesKind(JsonElement intent, ProjectOptionKind current, ProjectOptionKind? previous)
    {
        if (intent.TryGetProperty("Inputs", out _)) {
            return current == ProjectOptionKind.Input;
        }
        if (intent.TryGetProperty("SelectedCases", out _)) {
            return current == ProjectOptionKind.Checkbox;
        }
        if (intent.TryGetProperty("Kind", out var saved)) {
            return saved.GetString() == current.ToString();
        }
        // Older single-choice records had the same shape for select and switch.
        return current is ProjectOptionKind.Select or ProjectOptionKind.Switch
            && (previous is not (ProjectOptionKind.Select or ProjectOptionKind.Switch) || previous == current);
    }

    private static JsonElement? ReconcileCases(JsonElement intent, OptionDefinition option, Action<string> report)
    {
        var checkbox = option.Kind == ProjectOptionKind.Checkbox;
        var saved = checkbox ? ReadCaseNames(intent) : [intent.GetProperty("SelectedCase").GetString()!];
        var defined = option.Cases.Select(item => item.Name).ToArray();
        var retired = saved.Except(defined, StringComparer.Ordinal).ToArray();
        if (retired.Length == 0) {
            return checkbox || intent.TryGetProperty("Kind", out _) ? intent : CreateSelectedCase(option, saved[0]);
        }
        var kept = defined.Where(saved.Contains).ToArray();
        IEnumerable<string> effective = kept.Length > 0 ? kept
            : checkbox ? defined.Where(option.DefaultCases.Contains) : [option.DefaultCase!];
        var choice = string.Join("、", effective);
        report($"的旧选择 {string.Join("、", retired)} 已移除，采用 {(choice.Length == 0 ? "无" : choice)}");
        return kept.Length == 0 ? null : CreateSelectedCases(kept);
    }

    private static JsonElement? ReconcileInputs(JsonElement intent, OptionDefinition option,
        OptionDefinition? previous, Action<string> report)
    {
        var inputs = new JsonObject();
        var passwords = intent.TryGetProperty("PasswordInputs", out var savedPasswords)
            ? savedPasswords.EnumerateArray().Select(p => p.GetString()!).ToHashSet(StringComparer.Ordinal) : null;
        foreach (var field in intent.GetProperty("Inputs").EnumerateObject()) {
            var definition = option.Inputs.FirstOrDefault(input => input.Name == field.Name);
            if (definition is null) {
                report($"已移除旧字段 {field.Name}");
                continue;
            }

            var value = field.Value.GetString()!;
            bool? wasPassword = passwords?.Contains(field.Name)
                ?? previous?.Inputs.FirstOrDefault(input => input.Name == field.Name)?.Password;
            // Untagged Windows DPAPI blobs share the version/provider header, even for another account.
            if (passwords is null && value.StartsWith("AQAAANCMnd8BFdERjHoAwE/Cl+", StringComparison.Ordinal)) {
                wasPassword = true;
            }
            if (wasPassword == true && !definition.Password) {
                report($"已移除字段 {field.Name} 的旧密码，采用普通输入默认值");
            } else if (definition.Password && wasPassword == false) {
                inputs[field.Name] = Protect(value);
                report($"字段 {field.Name} 已改为密码，已加密保存");
            } else if (definition.Password && Unprotect(value) is null) {
                // Without trustworthy old type information, a damaged ciphertext cannot be used as plaintext.
                report($"字段 {field.Name} 的旧密码无法安全读取，已清除");
            } else {
                inputs[field.Name] = value;
            }
        }
        return inputs.Count == 0 ? null : CreateStoredInputs(option, inputs);
    }
}
