using ProjectModel;

namespace Gui.Views;

// A lone input is named and described by its row; one of several also names its own label.
internal sealed record InspectorInput(
    ProjectInputEditor Input, string Label, string AccessibleName, string HelpText, string? Draft)
{
    internal string SavedValue => Input.Value;
    internal string DisplayValue => Draft ?? Input.Value;
    internal string? Error { get; } = Draft is null ? Input.Error : Input.CheckValue(Draft);
    // A password is never shown as text, only whether it is set.
    internal string Show(string value) => Input.IsPassword && value.Length != 0 ? ProjectInputEditor.PasswordMask : value;
}

// One inspector row per option: a select, switch or checkbox, or an input option with all of its inputs side by side.
internal sealed record InspectorRow(
    ProjectOptionEditor Option, string Label, int Depth, bool IsGlobal, string Description,
    string? CaseDescription, IReadOnlyList<string> SharedWith, IReadOnlyList<InspectorInput> Inputs)
{
    internal ProjectOptionKind Kind => Option.Kind;
    internal bool IsInvalid => Option.SelectionCountError is not null || Inputs.Any(input => input.Error is not null);

    internal IEnumerable<RunFieldProblem> RunProblems(ProjectTaskChoice task)
    {
        if (Option.SelectionCountError is { } countError) {
            yield return new(task, Option.Name, null, $"“{Label}”：{countError}");
        }
        foreach (var field in Inputs) {
            var reason = field.Error ?? (field.Input.Error is null ? null : "请按 Enter 保存当前已修正的输入。");
            if (reason is not null) {
                yield return new(task, Option.Name, field.Input.Name, $"“{field.AccessibleName}”：{reason}");
            }
        }
    }

    internal ProjectCaseEditor? SelectedCase => Option.Cases.SingleOrDefault(item => item.Name == Option.SelectedCase);
}

internal sealed record RunFieldProblem(ProjectTaskChoice Task, string Option, string? Input, string Message);

internal static class ParameterInspector
{
    // Global options first, then task options, each in declared order; a selected case's sub-options follow their
    // parent one level deeper. Drafts are keyed by (option, input) of the configuration being shown.
    internal static IReadOnlyList<InspectorRow> Derive(
        ProjectConfigurationView configuration, Func<string, string, string?> draftFor,
        IReadOnlyList<(string Label, ProjectConfigurationView Configuration)> otherTasks)
    {
        var otherOptions = otherTasks
            .Select(task => (task.Label, Names: task.Configuration.ActiveOptions.Where(item => !item.Global)
                .Select(item => item.Option.Name).ToHashSet(StringComparer.Ordinal)))
            .ToArray();
        return configuration.ActiveOptions
            .Select(item => CreateRow(item.Option, item.Depth, item.Global, draftFor, item.Global ? []
                : otherOptions.Where(other => other.Names.Contains(item.Option.Name))
                    .Select(other => other.Label).Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    // An empty value is spelled out so a note never ends in nothing.
    internal static string ShowValue(string value) => value.Length == 0 ? "空" : value;

    // Checked labels in declaration order.
    internal static string ShowCases(ProjectOptionEditor option)
    {
        var labels = option.Cases.Where(item => option.SelectedCases.Contains(item.Name)).Select(item => item.Label);
        return option.SelectedCases.Count == 0 ? "无" : string.Join("、", labels);
    }

    internal static string? DescribeLimits(ProjectOptionEditor option) => (option.MinCount, option.MaxCount) switch {
        (0, null) => null,
        (0, { } max) => $"最多 {max} 项",
        (var min, null) => $"至少 {min} 项",
        var (min, max) when min == max => $"选择 {min} 项",
        var (min, max) => $"至少 {min} 项，最多 {max} 项"
    };

    private static InspectorRow CreateRow(ProjectOptionEditor option, int depth, bool global,
        Func<string, string, string?> draftFor, IReadOnlyList<string> sharedWith)
    {
        // A PI may declare either label as "": the option label, then the first input's, then the option name.
        var label = FirstNonBlank(option.Label, option.Inputs.FirstOrDefault()?.Label) ?? option.Name;
        var single = option.Inputs.Count == 1;
        var description = FirstNonBlank(option.Description, single ? option.Inputs[0].Description : null)
            ?? string.Empty;
        if (option.Kind != ProjectOptionKind.Input) {
            var selected = option.Cases.SingleOrDefault(item => item.Name == option.SelectedCase);
            return new InspectorRow(option, label, depth, global, description, FirstNonBlank(selected?.Description),
                sharedWith, []);
        }
        var inputs = option.Inputs.Select(input => {
            var draft = draftFor(option.Name, input.Name);
            // An input without a label keeps the old fallback: the option label, then the option name.
            var inputLabel = FirstNonBlank(input.Label, option.Label) ?? option.Name;
            var name = single || inputLabel == label ? label : $"{label} · {inputLabel}";
            return new InspectorInput(input, inputLabel, name, single ? description : input.Description, draft);
        }).ToArray();
        return new InspectorRow(option, label, depth, global, description, null, sharedWith, inputs);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
