using ProjectModel;

namespace Gui.Views;

// An invalid, unsaved text for one input; the saved configuration still holds the previous value.
internal sealed record InputDraft(string Text, string Message);

// A lone input is named and described by its row; one of several also names its own label.
internal sealed record InspectorInput(
    ProjectInputEditor Input, string Label, string AccessibleName, string HelpText, InputDraft? Draft)
{
    internal string SavedValue => Input.Value;
    internal string DisplayValue => Draft?.Text ?? Input.Value;
    // An unsaved draft's problem, otherwise a saved default that still fails verify.
    internal string? Error => Draft?.Message ?? Input.Error;
    // A password is never shown as text, only whether it is set.
    internal string Show(string value) => Input.IsPassword && value.Length != 0 ? ProjectInputEditor.PasswordMask : value;
}

internal sealed record InspectorBranch(string Path, IReadOnlyList<InspectorRow> Rows);

// One inspector row per option, with a branch for each active case that declares children.
internal sealed record InspectorRow(
    ProjectOptionEditor Option, string Label, int Depth, bool IsGlobal, string Description,
    string? CaseDescription, IReadOnlyList<string> SharedWith, IReadOnlyList<InspectorInput> Inputs)
{
    internal IReadOnlyList<InspectorBranch> Branches { get; init; } = [];
    internal ProjectOptionKind Kind => Option.Kind;
    internal bool IsInvalid => Option.StartBlocker is not null || Inputs.Any(input => input.Draft is not null);
    internal ProjectCaseEditor? SelectedCase => Option.Cases.SingleOrDefault(item => item.Name == Option.SelectedCase);
}

internal static class ParameterInspector
{
    // Drafts are keyed by (option, input) of the configuration being shown.
    internal static IReadOnlyList<InspectorRow> Derive(
        ProjectConfigurationView configuration, IReadOnlyDictionary<(string Option, string Input), InputDraft> drafts,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? owners = null)
    {
        IReadOnlyList<InspectorRow> Rows(IReadOnlyList<ProjectOptionEditor> options, string path, int depth, bool global) =>
            options.Select((option, index) => {
                var row = CreateRow(option, depth, global, drafts, owners?.GetValueOrDefault(option.Name) ?? []);
                var branches = option.Cases.Select((item, caseIndex) => (Item: item, Index: caseIndex))
                    .Where(item => item.Item.ActiveOptions.Count > 0)
                    .Select(item => {
                        var childPath = $"{path}/{index}:{item.Index}";
                        return new InspectorBranch(childPath, Rows(item.Item.ActiveOptions, childPath, depth + 1, global));
                    }).ToArray();
                return row with { Branches = branches };
            }).ToArray();
        return Rows(configuration.GlobalOptions, "global", 0, true)
            .Concat(Rows(configuration.TaskOptions, "task", 0, false)).ToArray();
    }

    internal static IEnumerable<InspectorRow> Flatten(IEnumerable<InspectorRow> rows) =>
        rows.SelectMany(row => Flatten(row.Branches.SelectMany(group => group.Rows)).Prepend(row));

    // An empty value is spelled out so a note never ends in nothing.
    internal static string ShowValue(string value) => value.Length == 0 ? "空" : value;

    internal static string? DescribeLimits(ProjectOptionEditor option) => (option.MinCount, option.MaxCount) switch {
        (0, null) => null,
        (0, { } max) => $"最多 {max} 项",
        (var min, null) => $"至少 {min} 项",
        var (min, max) when min == max => $"选择 {min} 项",
        var (min, max) => $"至少 {min} 项，最多 {max} 项"
    };

    internal static string GetOptionLabel(ProjectOptionEditor option) =>
        FirstNonBlank(option.Label, option.Inputs.FirstOrDefault()?.Label) ?? option.Name;

    private static InspectorRow CreateRow(ProjectOptionEditor option, int depth, bool global,
        IReadOnlyDictionary<(string Option, string Input), InputDraft> drafts, IReadOnlyList<string> sharedWith)
    {
        var label = GetOptionLabel(option);
        var single = option.Inputs.Count == 1;
        var description = FirstNonBlank(option.Description, single ? option.Inputs[0].Description : null)
            ?? string.Empty;
        if (option.Kind != ProjectOptionKind.Input) {
            var selected = option.Cases.SingleOrDefault(item => item.Name == option.SelectedCase);
            return new InspectorRow(option, label, depth, global, description, FirstNonBlank(selected?.Description),
                sharedWith, []);
        }
        var inputs = option.Inputs.Select(input => {
            var draft = drafts.GetValueOrDefault((option.Name, input.Name));
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
