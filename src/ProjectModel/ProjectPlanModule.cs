using System.Text.Json;
using Protocol;

namespace ProjectModel;

public sealed record ProjectTaskChoice(string Name, string Label, string Description)
{
    public IReadOnlyList<string> Groups { get; init; } = [];
    public PlanItemKind Kind { get; init; }
    public bool Applicable { get; init; } = true;
}

public sealed record ProjectTaskGroup(string Name, string Label, string Description, string? Icon, bool DefaultExpand);

public sealed record RunStartAttempt(Guid RunId, RunPlan Plan, string PlanDigest)
{
    // Every non-empty password this Run uses, for telemetry to redact; never part of the Plan or its digest.
    public IReadOnlyList<string> RedactionValues { get; init; } = [];
}

public sealed partial class ProjectPlanModule
{
    private readonly ProjectDefinition _project;
    private readonly IReadOnlyList<ProjectTaskChoice> _pretaskChoices;
    private readonly ConfigurationSetStore _configStore;
    private ConfigurationSet _config;
    private string? _initializationWarning;

    private ProjectPlanModule(ProjectDefinition project, ConfigurationSetStore configStore)
    {
        _project = project;
        _configStore = configStore;
        Tasks = project.Tasks
            .Select(task => new ProjectTaskChoice(task.Name, task.Label, task.Description) { Groups = task.Groups })
            .ToArray();

        _pretaskChoices = project.Pretasks
            .Select(item => new ProjectTaskChoice(item.Name, item.Label, item.Description) {
                Kind = PlanItemKind.Pretask, Applicable = item.Applicable
            }).ToArray();
        Pretasks = _pretaskChoices.Where(item => item.Applicable).ToArray();
        _config = configStore.Load();
        if (configStore.WasMissing) {
            try {
                AddItem(PlanItemKind.MaaTask, Tasks[0].Name);
                InitializedTaskName = Tasks[0].Name;
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                _initializationWarning = $"无法保存首次默认任务，已提供空配置。{exception.Message}";
            }
        }
    }

    public string ProjectName => _project.Provenance.Name;
    public string ProjectVersion => _project.Provenance.Version;
    public string RuntimeProfileDigest => _project.RuntimeProfileDigest;
    public string SourceInterfaceDigest => _project.Provenance.SourceInterfaceDigest;
    public bool HasWelcome => _project.Welcome.Count > 0;
    public ProjectAbout About => _project.About;
    public string Title => _project.Title;
    public ProjectTelemetry? Telemetry => _project.Telemetry;
    // Throws when the icon lies outside the project directory; whether it is an image is known only when shown.
    public string? ResolveIconPath() =>
        _project.Icon is { } icon ? ProjectContent.ResolvePath(_project.ProjectRoot, icon) : null;
    public ProjectWelcome? ResolveWelcome(Action<string> warn) => ProjectWelcomeResolver.Resolve(_project, warn);
    public IReadOnlyList<ProjectTaskChoice> Tasks { get; }
    public IReadOnlyList<ProjectTaskChoice> Pretasks { get; }
    public IReadOnlyList<ProjectTaskGroup> Groups => _project.Groups;
    public IReadOnlyList<string> SelectedPretaskNames => LoadConfig().SelectedPretasks;
    public IReadOnlyList<ProjectTaskChoice> SelectedItems
    {
        get
        {
            var config = LoadConfig();
            var pretasks = config.SelectedPretasks.Select(name =>
                _pretaskChoices.SingleOrDefault(item => item.Name == name)
                ?? new ProjectTaskChoice(name, name, "任务已不在项目中，请移除。") { Kind = PlanItemKind.Pretask });
            return pretasks.Concat(SelectedOrdinaryTasks(config)).ToArray();
        }
    }
    public int SelectedItemCount => SelectedItems.Count;
    // Run progress matches this sequence against the Worker snapshot.
    public IReadOnlyList<ProjectTaskChoice> EnabledItems
    {
        get
        {
            var config = ExecutionConfiguration(LoadConfig());
            return Pretasks.Where(item => config.SelectedPretasks.Contains(item.Name))
                .Concat(SelectedOrdinaryTasks(config)).ToArray();
        }
    }

    private IEnumerable<ProjectTaskChoice> SelectedOrdinaryTasks(TaskConfiguration config) =>
        config.SelectedTasks.Select(name => Tasks.SingleOrDefault(item => item.Name == name)
            ?? new ProjectTaskChoice(name, name, "任务已不在项目中，请移除。"));

    public bool IsItemEnabled(PlanItemKind kind, string name) =>
        !LoadConfig().DisabledItems.Contains(new TaskKey(kind, name));

    public void SetItemEnabled(PlanItemKind kind, string name, bool enabled)
    {
        var config = LoadConfig();
        if (!config.SelectedNames(kind).Contains(name)) {
            throw new ArgumentException("任务不在当前执行计划中。", nameof(name));
        }
        var key = new TaskKey(kind, name);
        if (config.DisabledItems.Contains(key) == enabled) {
            SaveConfiguration(config with {
                DisabledItems = enabled ? config.DisabledItems.Where(item => item != key).ToArray()
                    : config.DisabledItems.Append(key).ToArray()
            });
        }
    }

    private static TaskConfiguration ExecutionConfiguration(TaskConfiguration config) => config with {
        SelectedTasks = config.SelectedTasks
            .Where(name => !config.DisabledItems.Contains(new TaskKey(PlanItemKind.MaaTask, name))).ToArray(),
        SelectedPretasks = config.SelectedPretasks
            .Where(name => !config.DisabledItems.Contains(new TaskKey(PlanItemKind.Pretask, name))).ToArray()
    };

    public IReadOnlyList<string> SelectedTaskNames => LoadConfig().SelectedTasks;
    public Guid ActiveConfigurationId => _config.ActiveConfigurationId;
    public string? LoadWarning => _initializationWarning ?? _configStore.LoadWarning;
    // The file could not be read, so Configurations is a placeholder until the first save.
    public bool ConfigurationsUnreadable => _configStore.Unreadable;
    public string? InitializedTaskName { get; }
    public IReadOnlyList<TaskConfiguration> Configurations => _config.Configurations;

    public Guid CreateConfiguration() => AddConfiguration(new TaskConfiguration {
        Id = Guid.NewGuid(), Name = $"配置 {_config.Configurations.Count + 1}"
    });

    public void ActivateConfiguration(Guid id)
    {
        _ = FindConfiguration(id);
        if (id != ActiveConfigurationId) {
            SaveConfig(_config with { ActiveConfigurationId = id });
        }
    }

    public void RenameConfiguration(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new InvalidDataException("配置名称不能为空。");
        }
        var configuration = FindConfiguration(id);
        if (configuration.Name != name.Trim()) {
            SaveConfiguration(configuration with { Name = name.Trim() });
        }
    }

    public bool DeleteConfiguration(Guid id)
    {
        var index = _config.Configurations.ToList().FindIndex(item => item.Id == id);
        if (index < 0 || _config.Configurations.Count == 1) {
            return false;
        }
        var active = id == ActiveConfigurationId
            ? _config.Configurations[index == 0 ? 1 : index - 1].Id : ActiveConfigurationId;
        SaveConfig(_config with {
            ActiveConfigurationId = active,
            Configurations = _config.Configurations.Where(item => item.Id != id).ToArray()
        });
        return true;
    }

    public void ValidateConfiguration() => ValidateActiveConfiguration(LoadConfig());

    public static ProjectPlanModule Open(string projectDirectory, string configPath, Action<string>? log = null)
    {
        var project = ProjectInterfaceLoader.Load(projectDirectory);
        return new ProjectPlanModule(project, new ConfigurationSetStore(configPath, log));
    }

    public bool AddItem(PlanItemKind kind, string name)
    {
        if (kind == PlanItemKind.Pretask) {
            if (!FindPretask(name).Applicable) {
                throw new InvalidOperationException($"前置任务 {name} 不适用于当前控制器或资源。");
            }
        } else {
            _ = FindTask(name);
        }
        var config = LoadConfig();
        var names = config.SelectedNames(kind);
        if (names.Contains(name)) {
            return false;
        }
        var updated = WithSelection(config, kind, names.Append(name).ToArray());
        ValidateActiveConfiguration(updated);
        SaveConfiguration(updated);
        return true;
    }

    public bool RemoveItem(PlanItemKind kind, string name)
    {
        var config = LoadConfig();
        var names = config.SelectedNames(kind);
        if (!names.Contains(name)) {
            return false;
        }
        var updated = WithSelection(config, kind, names.Where(item => item != name).ToArray());
        SaveConfiguration(updated with {
            DisabledItems = config.DisabledItems.Where(item => item != new TaskKey(kind, name)).ToArray()
        });
        return true;
    }

    private static TaskConfiguration WithSelection(TaskConfiguration config, PlanItemKind kind,
        IReadOnlyList<string> names) => kind == PlanItemKind.Pretask
        ? config with { SelectedPretasks = names } : config with { SelectedTasks = names };

    public bool MoveTask(string taskName, int targetIndex, PlanItemKind kind = PlanItemKind.MaaTask)
    {
        var config = LoadConfig();
        var selected = config.SelectedNames(kind).ToList();
        var currentIndex = selected.IndexOf(taskName);
        if (currentIndex < 0) {
            throw new ArgumentException($"执行计划中不存在 task：{taskName}。", nameof(taskName));
        }
        if (targetIndex < 0 || targetIndex >= selected.Count) {
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        }
        if (currentIndex == targetIndex) {
            return false;
        }

        selected.RemoveAt(currentIndex);
        selected.Insert(targetIndex, taskName);
        var updated = WithSelection(config, kind, selected);
        ValidateActiveConfiguration(updated);
        SaveConfiguration(updated);
        return true;
    }

    public ProjectConfigurationView GetConfiguration(string taskName, PlanItemKind kind = PlanItemKind.MaaTask)
    {
        var config = LoadConfig();
        if (!config.SelectedNames(kind).Contains(taskName, StringComparer.Ordinal)) {
            throw new InvalidOperationException($"{kind} {taskName} 不在当前执行计划中。");
        }
        return kind == PlanItemKind.Pretask ? BuildPretaskConfiguration(config, FindPretask(taskName))
            : BuildConfiguration(config, taskName);
    }

    // Checks only the edited value, so a typo can be shown at its field instead of as a whole-config failure.
    public string? CheckInputValue(string optionName, string inputName, string value)
    {
        var input = FindOption(optionName).Inputs.SingleOrDefault(item => item.Name == inputName)
            ?? throw new ArgumentException($"option {optionName} 不包含 input {inputName}。", nameof(inputName));
        var typed = IsTypedOption(LoadConfig(), optionName);
        try {
            _ = ProjectInputValue.Parse(input, value, $"option {optionName} input {inputName} 的值", typed);
            return null;
        } catch (InvalidDataException exception) {
            return exception.Message;
        }
    }

    public ProjectConfigurationView SetInputValue(string optionName, string inputName, string value)
        => SetInputValue(ActiveConfigurationId, optionName, inputName, value);

    public ProjectConfigurationView SetInputValue(
        Guid configurationId, string optionName, string inputName, string value)
    {
        var option = FindOption(optionName);
        if (option.Kind != OptionDefinitionKind.Input) {
            throw new ArgumentException($"option {optionName} 不是 input。", nameof(optionName));
        }
        if (!option.Inputs.Any(input => input.Name == inputName)) {
            throw new ArgumentException(
                $"option {optionName} 不包含 input {inputName}。",
                nameof(inputName));
        }

        var config = FindConfiguration(configurationId);
        var values = ExplicitOptionIntent.ReadInputs(option, config)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        values[inputName] = value;
        var updated = ReplaceExplicit(config, optionName, ExplicitOptionIntent.CreateInputs(option, values));
        ValidateActiveConfiguration(updated);
        SaveConfiguration(updated);
        return BuildConfiguration(updated);
    }

    public ProjectConfigurationView SetSelectedCase(string optionName, string selectedCase)
        => SetSelectedCase(ActiveConfigurationId, optionName, selectedCase);

    public ProjectConfigurationView SetSelectedCase(Guid configurationId, string optionName, string selectedCase)
    {
        var option = FindOption(optionName);
        if (option.Kind is not (OptionDefinitionKind.Select or OptionDefinitionKind.Switch)) {
            throw new ArgumentException(
                $"option {optionName} 不是 select/switch。",
                nameof(optionName));
        }
        if (!option.Cases.Any(item => item.Name == selectedCase)) {
            throw new ArgumentException(
                $"option {optionName} 不包含 case {selectedCase}。",
                nameof(selectedCase));
        }

        var config = FindConfiguration(configurationId);
        var updated = ReplaceExplicit(config, optionName, ExplicitOptionIntent.CreateSelectedCase(selectedCase));
        ValidateActiveConfiguration(updated);
        SaveConfiguration(updated);
        return BuildConfiguration(updated);
    }

    public ProjectConfigurationView SetSelectedCases(string optionName, IReadOnlyCollection<string> selectedCases)
        => SetSelectedCases(ActiveConfigurationId, optionName, selectedCases);

    public ProjectConfigurationView SetSelectedCases(
        Guid configurationId, string optionName, IReadOnlyCollection<string> selectedCases)
    {
        var option = FindOption(optionName);
        if (option.Kind != OptionDefinitionKind.Checkbox) {
            throw new ArgumentException($"option {optionName} 不是 checkbox。", nameof(optionName));
        }
        var ordered = option.Cases.Select(item => item.Name).Where(selectedCases.Contains).ToArray();
        if (ordered.Length != selectedCases.Count || ordered.Length > option.MaxCount) {
            throw new ArgumentException(
                $"option {optionName} 的勾选项不存在、重复或超过上限。", nameof(selectedCases));
        }

        var config = FindConfiguration(configurationId);
        var updated = ReplaceExplicit(config, optionName, ExplicitOptionIntent.CreateSelectedCases(ordered));
        ValidateActiveConfiguration(updated);
        SaveConfiguration(updated);
        return BuildConfiguration(updated);
    }

    public LaunchManifest CreateLaunchManifest(Guid workerInstanceId) => new(
        ProtocolConstants.LaunchContextVersion, workerInstanceId,
        _project.RuntimeProfileDigest, _project.ProjectRoot, _project.Provenance,
        _project.Controller, _project.Resources, _project.Agent);

    public RunStartAttempt CreateRunStartAttempt(Guid configurationId,
        IReadOnlyDictionary<(string Option, string Input), string>? inputErrors = null)
    {
        var config = ExecutionConfiguration(FindConfiguration(configurationId));
        ValidateActiveConfiguration(config);
        if (config.SelectedTasks.Count == 0 && !SelectedPretaskDefinitions(config).Any()) {
            throw new InvalidOperationException("请先勾选要执行的任务或前置任务。");
        }
        RequireRunnableOptions(config, inputErrors);

        var passwords = new HashSet<string>(StringComparer.Ordinal);
        var resolvedItems = config.SelectedTasks.Select(taskName => {
            var task = FindTask(taskName);
            return (Task: task, Resolved: ProjectOptionResolver.Resolve(_project, task, config, passwords));
        }).ToArray();
        var createdAtUtc = DateTime.UtcNow;
        var pretaskItems = SelectedPretaskDefinitions(config).Select(item => {
            var (values, argument) = ProjectOptionResolver.ResolvePretask(_project, item, config, passwords);
            var arguments = item.Arguments.ToList();
            if (item.Options.Count != 0) {
                arguments.Add(JsonSerializer.Serialize(argument, ProtocolJson.Options));
            }
            return new RunPlanItem(Guid.NewGuid(), item.Name, item.Label, string.Empty,
                values, ProtocolJson.ToElement(Array.Empty<object>())) {
                Kind = PlanItemKind.Pretask,
                Program = new PretaskCommand(ResolvePretaskExecutable(item.Executable), arguments)
            };
        });
        var items = pretaskItems.Concat(resolvedItems.Select(item => new RunPlanItem(
            Guid.NewGuid(), item.Task.Name, item.Task.Label, item.Task.Entry,
            item.Resolved.ResolvedTaskOptions, item.Resolved.PipelineOverride))).ToArray();
        var plan = new RunPlan(
            ProtocolConstants.PlanVersion, createdAtUtc,
            _project.Provenance, _project.RuntimeProfileDigest,
            resolvedItems.Length == 0 ? ProtocolJson.ToElement(new { })
                : resolvedItems[0].Resolved.ResolvedGlobalOptions, items);
        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(plan, ProtocolJson.Options);
        if (serializedBytes.Length > ProtocolConstants.MaximumRunPlanBytes) {
            throw new InvalidDataException(
                $"Run Plan 超过 {ProtocolConstants.MaximumRunPlanBytes} bytes：{serializedBytes.Length}。 ");
        }

        return new RunStartAttempt(Guid.NewGuid(), plan, CanonicalDigest.ComputePlanDigestV2(plan)) {
            RedactionValues = passwords.ToArray()
        };
    }

    private TaskConfiguration LoadConfig() => FindConfiguration(ActiveConfigurationId);

    private TaskConfiguration FindConfiguration(Guid id) =>
        _config.Configurations.SingleOrDefault(item => item.Id == id)
        ?? throw new InvalidOperationException("目标配置已不存在。");

    private Guid AddConfiguration(TaskConfiguration configuration)
    {
        SaveConfig(_config with {
            ActiveConfigurationId = configuration.Id,
            Configurations = _config.Configurations.Append(configuration).ToArray()
        });
        return configuration.Id;
    }

    private void SaveConfiguration(TaskConfiguration configuration)
    {
        var updated = _config with {
            Configurations = _config.Configurations.Select(item => item.Id == configuration.Id ? configuration : item)
                .ToArray()
        };
        SaveConfig(updated);
    }

    private void SaveConfig(ConfigurationSet config)
    {
        _configStore.Save(config);
        _config = config;
        _initializationWarning = null;
    }

    // Only what a Run would execute is checked, so a disabled item never blocks edits or Start.
    private void ValidateActiveConfiguration(TaskConfiguration config)
    {
        config = ExecutionConfiguration(config);
        ValidateSelectedTasks(config);
        foreach (var taskName in config.SelectedTasks) {
            _ = ProjectOptionResolver.Resolve(_project, FindTask(taskName), config);
        }
        foreach (var name in config.SelectedPretasks) {
            var pretask = _project.Pretasks.SingleOrDefault(item => item.Name == name)
                ?? throw new InvalidDataException($"任务配置选择的 pretask 不再存在：{name}。");
            if (pretask.Applicable) {
                _ = ProjectOptionResolver.ResolvePretask(_project, pretask, config);
            }
        }
    }

    // Only enabled MaaTasks resolve typed pipeline inputs, including active children and global options.
    private bool IsTypedOption(TaskConfiguration config, string optionName)
    {
        config = ExecutionConfiguration(config);
        IEnumerable<string> roots = config.SelectedTasks.Count == 0 ? []
            : _project.GlobalOptions.Concat(_project.Tasks
                .Where(task => config.SelectedTasks.Contains(task.Name, StringComparer.Ordinal))
                .SelectMany(task => task.Options));
        var pending = new Stack<string>(roots);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var name)) {
            if (name == optionName) {
                return true;
            }
            var option = _project.Options[name];
            if (!visited.Add(name) || option.Kind == OptionDefinitionKind.Input) {
                continue;
            }
            var active = ExplicitOptionIntent.ReadActiveCases(option, config);
            foreach (var nested in active.SelectMany(item => item.Options)) {
                pending.Push(nested);
            }
        }
        return false;
    }

    private ProjectConfigurationView BuildConfiguration(TaskConfiguration config)
    {
        var taskName = config.SelectedTasks.FirstOrDefault();
        if (taskName is not null) {
            return BuildConfiguration(config, taskName);
        }
        var pretask = SelectedPretaskDefinitions(config).FirstOrDefault();
        return pretask is null ? new ProjectConfigurationView([], []) : BuildPretaskConfiguration(config, pretask);
    }

    private ProjectConfigurationView BuildPretaskConfiguration(TaskConfiguration config, PretaskDefinition pretask) =>
        new([], BuildEditors(pretask.Options, config));

    private ProjectConfigurationView BuildConfiguration(TaskConfiguration config, string? taskName)
    {
        var global = BuildEditors(_project.GlobalOptions, config);
        var task = taskName is null
            ? []
            : BuildEditors(FindTask(taskName).Options, config);
        return new ProjectConfigurationView(global, task);
    }

    private void ValidateSelectedTasks(TaskConfiguration config)
    {
        foreach (var taskName in config.SelectedTasks) {
            if (!_project.Tasks.Any(task => task.Name == taskName)) {
                throw new InvalidDataException($"任务配置选择的 task 不再存在：{taskName}。 ");
            }
        }
    }

    private IReadOnlyList<ProjectOptionEditor> BuildEditors(IReadOnlyList<string> names, TaskConfiguration config) =>
        names.Select(name => BuildEditor(name, config)).ToArray();

    private ProjectOptionEditor BuildEditor(string optionName, TaskConfiguration config)
    {
        var option = FindOption(optionName);
        if (option.Kind == OptionDefinitionKind.Input) {
            var explicitInputs = ExplicitOptionIntent.ReadInputs(option, config);
            var inputs = option.Inputs.Select(input => new ProjectInputEditor(
                input.Name, input.Label, input.Description, input.Default,
                explicitInputs.TryGetValue(input.Name, out var value) ? value : input.Default,
                explicitInputs.ContainsKey(input.Name), input.Verify, input.PatternMessage) {
                // Saved values passed verify when they were set; only an untouched default can still fail it.
                Error = explicitInputs.ContainsKey(input.Name) || ProjectInputValue.Matches(input, input.Default,
                    $"option {optionName} input {input.Name}") ? null : input.PatternMessage ?? "未通过校验",
                InputType = input.InputType
            }).ToArray();
            return new ProjectOptionEditor(
                option.Name, option.Label, option.Description, ProjectOptionKind.Input,
                explicitInputs.Count != 0, null, null, [], inputs, []);
        }

        var active = ExplicitOptionIntent.ReadActiveCases(option, config);
        var checkbox = option.Kind == OptionDefinitionKind.Checkbox;
        var children = BuildEditors(active.SelectMany(item => item.Options).ToArray(), config);
        return new ProjectOptionEditor(
            option.Name, option.Label, option.Description,
            option.Kind switch {
                OptionDefinitionKind.Switch => ProjectOptionKind.Switch,
                OptionDefinitionKind.Checkbox => ProjectOptionKind.Checkbox,
                _ => ProjectOptionKind.Select
            },
            config.ExplicitOptions.ContainsKey(option.Name), checkbox ? null : active[0].Name, option.DefaultCase,
            option.Cases.Select(item => new ProjectCaseEditor(
                item.Name, item.Label, item.Description)).ToArray(),
            [],
            children) {
            SelectedCases = active.Select(item => item.Name).ToArray(),
            MinCount = option.MinCount, MaxCount = option.MaxCount
        };
    }

    // A checkbox below min_count or an input whose default still fails verify may be saved, but cannot start a Run.
    private void RequireRunnableOptions(TaskConfiguration config,
        IReadOnlyDictionary<(string Option, string Input), string>? inputErrors)
    {
        IEnumerable<string> roots = config.SelectedTasks.Count == 0 ? [] : _project.GlobalOptions;
        roots = roots.Concat(config.SelectedTasks.SelectMany(name => FindTask(name).Options))
            .Concat(SelectedPretaskDefinitions(config).SelectMany(item => item.Options));
        foreach (var option in Flatten(BuildEditors(roots.ToArray(), config))) {
            if (option.StartBlocker is { } blocker) {
                throw new InvalidOperationException($"参数“{option.Label}”：{blocker}。");
            }
            if (inputErrors is { Count: > 0 }) {
                foreach (var input in option.Inputs) {
                    if (inputErrors.TryGetValue((option.Name, input.Name), out var error)) {
                        throw new InvalidOperationException($"参数“{option.Label}”：{error}");
                    }
                }
            }
        }
    }

    private static IEnumerable<ProjectOptionEditor> Flatten(IEnumerable<ProjectOptionEditor> options) =>
        options.SelectMany(option => Flatten(option.ActiveChildren).Prepend(option));

    private TaskDefinition FindTask(string taskName) =>
        _project.Tasks.SingleOrDefault(task => task.Name == taskName)
        ?? throw new ArgumentException($"PI 中不存在 task：{taskName}。", nameof(taskName));

    private PretaskDefinition FindPretask(string name) =>
        _project.Pretasks.SingleOrDefault(item => item.Name == name)
        ?? throw new ArgumentException($"PI 中不存在 pretask：{name}。", nameof(name));

    // The pretasks a Run executes: selected, applicable, and in Project Interface order.
    private IEnumerable<PretaskDefinition> SelectedPretaskDefinitions(TaskConfiguration config) => _project.Pretasks
        .Where(item => item.Applicable && config.SelectedPretasks.Contains(item.Name, StringComparer.Ordinal));

    // The loader has rejected drive- and root-relative paths; a path with a separator is relative to the project root.
    private string ResolvePretaskExecutable(string executable)
    {
        return executable.Contains('/') || executable.Contains('\\')
            ? Path.GetFullPath(executable, _project.ProjectRoot) : executable;
    }

    private OptionDefinition FindOption(string optionName) =>
        _project.Options.GetValueOrDefault(optionName)
        ?? throw new ArgumentException($"PI 中不存在 option：{optionName}。", nameof(optionName));

    private static TaskConfiguration ReplaceExplicit(TaskConfiguration config, string optionName, JsonElement value)
    {
        var values = config.ExplicitOptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        values[optionName] = value;
        return config with { ExplicitOptions = values };
    }
}
