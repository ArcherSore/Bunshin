using System.Text.Json;
using Protocol;

namespace ProjectModel;

public sealed record ProjectTaskChoice(string Name, string Label, string Description)
{
    public IReadOnlyList<string> Groups { get; init; } = [];
    public PlanItemKind Kind { get; init; }
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
    private readonly ConfigurationSetStore _configStore;
    private readonly Action<string>? _log;
    private ConfigurationSet _config;
    private string? _initializationWarning;
    private Exception? _initializationException;
    private readonly HashSet<(Guid Configuration, PlanItemKind Kind, string Name)> _disabledItems = [];
    private readonly Dictionary<(string Name, PlanItemKind Kind), ProjectConfigurationView> _configurationViews = [];

    private ProjectPlanModule(ProjectDefinition project, ConfigurationSetStore configStore, Action<string>? log,
        ProjectDefinition? previousProject)
    {
        _log = log;
        _project = project;
        _configStore = configStore;
        Tasks = project.Tasks
            .Select(task => new ProjectTaskChoice(task.Name, task.Label, task.Description) { Groups = task.Groups })
            .ToArray();

        Pretasks = project.Pretasks.Where(item => item.Applicable)
            .Select(item => new ProjectTaskChoice(item.Name, item.Label, item.Description) {
                Kind = PlanItemKind.Pretask
            }).ToArray();
        _config = configStore.Load(project, previousProject);
        if (configStore.WasMissing) {
            try {
                AddTask(Tasks[0].Name);
                InitializedTaskName = Tasks[0].Name;
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                _initializationWarning = "首次默认任务未保存，已显示空配置。请检查目录写入权限后重新加载。";
                _initializationException = exception;
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
    public IReadOnlyList<ProjectTaskChoice> SelectedItems => SelectedItemsOf(LoadConfig()).ToArray();

    private IEnumerable<ProjectTaskChoice> SelectedItemsOf(TaskConfiguration config) =>
        Pretasks.Where(item => config.SelectedPretasks.Contains(item.Name))
            .Concat(config.SelectedTasks.Select(name => Tasks.Single(item => item.Name == name)));
    public string? SkippedPretaskMessage => GetSkippedPretaskMessage(LoadConfig());

    private string? GetSkippedPretaskMessage(TaskConfiguration config)
    {
        var skipped = _project.Pretasks.Where(item => !item.Applicable && config.SelectedPretasks.Contains(item.Name))
            .Select(item => $"“{item.Label}”（{item.SkipReason}）").ToArray();
        return skipped.Length == 0 ? null : $"已跳过前置任务：{string.Join("、", skipped)}。";
    }

    public int SelectedItemCount => SelectedItems.Count;
    public IReadOnlyList<ProjectTaskChoice> EnabledItems => GetExecutionItems();
    public IReadOnlyList<ProjectTaskChoice> GetExecutionItems(bool startupOnly = false) =>
        GetExecutionItems(LoadConfig(), startupOnly);

    private ProjectTaskChoice[] GetExecutionItems(TaskConfiguration config, bool startupOnly) =>
        SelectedItemsOf(config).Where(item => (!startupOnly || item.Kind == PlanItemKind.Pretask)
            && !_disabledItems.Contains((config.Id, item.Kind, item.Name))).ToArray();

    public bool IsItemEnabled(PlanItemKind kind, string name) =>
        !_disabledItems.Contains((ActiveConfigurationId, kind, name));

    // Temporary execution choices belong to this open workspace, never to the saved configuration.
    public void SetItemEnabled(PlanItemKind kind, string name, bool enabled)
    {
        if (!SelectedItems.Any(item => item.Kind == kind && item.Name == name)) {
            throw new ArgumentException("任务不在当前执行计划中。", nameof(name));
        }
        var key = (ActiveConfigurationId, kind, name);
        if (enabled) {
            _disabledItems.Remove(key);
        } else {
            _disabledItems.Add(key);
        }
    }

    public IReadOnlyList<string> SelectedTaskNames => LoadConfig().SelectedTasks;
    public Guid ActiveConfigurationId => _config.ActiveConfigurationId;
    public Exception? LoadException => _initializationException ?? _configStore.LoadException;
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
        _disabledItems.RemoveWhere(item => item.Configuration == id);
        return true;
    }

    // Read the declaration even when its task or option branch is outside the current execution scope.
    public ProjectInputType? GetInputType(string optionName, string inputName) =>
        _project.Options.GetValueOrDefault(optionName)?.Inputs
            .FirstOrDefault(input => input.Name == inputName)?.InputType;

    public static ProjectPlanModule Open(string projectDirectory, string configPath, ProjectPlanModule? previous = null,
        Action<string>? log = null)
    {
        var project = ProjectInterfaceLoader.Load(projectDirectory);
        if (previous is not null && previous.ProjectName != project.Provenance.Name) {
            throw new InvalidDataException("项目身份已改变。请恢复原项目的资源包后重试，原配置已保留。");
        }
        var store = new ConfigurationSetStore(configPath, log);
        var module = new ProjectPlanModule(project, store, log, previous?._project);
        if (previous is not null) {
            module._disabledItems.UnionWith(previous._disabledItems.Where(item => module.Configurations.Any(config =>
                config.Id == item.Configuration && (item.Kind == PlanItemKind.Pretask
                    ? config.SelectedPretasks : config.SelectedTasks).Contains(item.Name, StringComparer.Ordinal))));
        }
        return module;
    }

    public bool AddTask(string taskName)
    {
        _ = FindTask(taskName);
        var config = LoadConfig();
        if (config.SelectedTasks.Contains(taskName, StringComparer.Ordinal)) {
            return false;
        }

        var updated = config with { SelectedTasks = config.SelectedTasks.Append(taskName).ToArray() };
        SaveConfiguration(updated);
        return true;
    }

    public bool RemoveTask(string taskName)
    {
        var config = LoadConfig();
        if (!config.SelectedTasks.Contains(taskName, StringComparer.Ordinal)) {
            return false;
        }

        var updated = config with {
            SelectedTasks = config.SelectedTasks.Where(name => name != taskName).ToArray()
        };
        SaveConfiguration(updated);
        _disabledItems.Remove((ActiveConfigurationId, PlanItemKind.MaaTask, taskName));
        return true;
    }

    public bool AddPretask(string name)
    {
        var pretask = FindPretask(name);
        if (!pretask.Applicable) {
            throw new InvalidOperationException($"前置任务 {name} 不适用于当前控制器或资源。");
        }
        var config = LoadConfig();
        if (config.SelectedPretasks.Contains(name, StringComparer.Ordinal)) {
            return false;
        }
        var updated = config with { SelectedPretasks = config.SelectedPretasks.Append(name).ToArray() };
        SaveConfiguration(updated);
        return true;
    }

    public bool RemovePretask(string name)
    {
        var config = LoadConfig();
        if (!config.SelectedPretasks.Contains(name, StringComparer.Ordinal)) {
            return false;
        }
        SaveConfiguration(config with {
            SelectedPretasks = config.SelectedPretasks.Where(item => item != name).ToArray()
        });
        _disabledItems.Remove((ActiveConfigurationId, PlanItemKind.Pretask, name));
        return true;
    }

    public bool MoveTask(string taskName, int targetIndex)
    {
        var config = LoadConfig();
        var currentIndex = config.SelectedTasks.ToList().IndexOf(taskName);
        if (currentIndex < 0) {
            throw new ArgumentException($"执行计划中不存在 task：{taskName}。", nameof(taskName));
        }
        if (targetIndex < 0 || targetIndex >= config.SelectedTasks.Count) {
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        }
        if (currentIndex == targetIndex) {
            return false;
        }

        var selected = config.SelectedTasks.ToList();
        selected.RemoveAt(currentIndex);
        selected.Insert(targetIndex, taskName);
        var updated = config with { SelectedTasks = selected };
        SaveConfiguration(updated);
        return true;
    }

    public ProjectConfigurationView GetConfiguration(string taskName, PlanItemKind kind = PlanItemKind.MaaTask)
    {
        if (_configurationViews.TryGetValue((taskName, kind), out var view)) {
            return view;
        }
        var config = LoadConfig();
        if (kind == PlanItemKind.MaaTask && !config.SelectedTasks.Contains(taskName)) {
            throw new InvalidOperationException($"{kind} {taskName} 不在当前执行计划中。");
        }
        return _configurationViews[(taskName, kind)] = BuildConfiguration(config, taskName, kind);
    }

    public void SetInputValue(string optionName, string inputName, string value)
        => SetInputValue(ActiveConfigurationId, optionName, inputName, value);

    public void SetInputValue(Guid configurationId, string optionName, string inputName, string value)
    {
        if (TrySetInputValue(configurationId, optionName, inputName, value) is { } error) {
            throw new InvalidDataException(error);
        }
    }

    public string? TrySetInputValue(Guid configurationId, string optionName, string inputName, string value)
    {
        var option = FindOption(optionName);
        var config = FindConfiguration(configurationId);
        var input = option.Inputs.SingleOrDefault(item => item.Name == inputName)
            ?? throw new ArgumentException($"option {option.Name} 不包含 input {inputName}。", nameof(inputName));
        if (ProjectInputValue.Parse(input, value, out _) is { } error) {
            return error;
        }
        var intent = ExplicitOptionIntent.UpdateInput(option, config, inputName, value);
        SaveConfiguration(ReplaceExplicit(config, optionName, intent));
        return null;
    }

    public void RestoreOptionDefault(Guid configurationId, string optionName, string? inputName = null)
    {
        var option = FindOption(optionName);
        var config = FindConfiguration(configurationId);
        if ((option.Kind == ProjectOptionKind.Input) != (inputName is not null)
            || inputName is not null && option.Inputs.All(input => input.Name != inputName)) {
            throw new ArgumentException($"无法恢复 {optionName} 的指定字段。", nameof(inputName));
        }
        if (!config.ExplicitOptions.TryGetValue(optionName, out var current)) {
            return;
        }

        if (inputName is not null && !current.GetProperty("Inputs").TryGetProperty(inputName, out _)) {
            return;
        }
        var remaining = inputName is null ? null : ExplicitOptionIntent.UpdateInput(option, config, inputName, null);
        SaveConfiguration(ReplaceExplicit(config, optionName, remaining));
    }

    public void SetSelectedCase(string optionName, string selectedCase)
        => SetSelectedCase(ActiveConfigurationId, optionName, selectedCase);

    public void SetSelectedCase(Guid configurationId, string optionName, string selectedCase)
    {
        var option = FindOption(optionName);
        if (option.Kind is not (ProjectOptionKind.Select or ProjectOptionKind.Switch)) {
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
        var intent = ExplicitOptionIntent.CreateSelectedCase(option, selectedCase);
        SaveConfiguration(ReplaceExplicit(config, optionName, intent));
    }

    public void SetSelectedCases(string optionName, IReadOnlyCollection<string> selectedCases)
        => SetSelectedCases(ActiveConfigurationId, optionName, selectedCases);

    public void SetSelectedCases(
        Guid configurationId, string optionName, IReadOnlyCollection<string> selectedCases)
    {
        var option = FindOption(optionName);
        if (option.Kind != ProjectOptionKind.Checkbox) {
            throw new ArgumentException($"option {optionName} 不是 checkbox。", nameof(optionName));
        }
        var ordered = option.Cases.Select(item => item.Name).Where(selectedCases.Contains).ToArray();
        if (ordered.Length != selectedCases.Count) {
            throw new ArgumentException($"option {optionName} 的勾选项不存在或重复。", nameof(selectedCases));
        }
        var config = FindConfiguration(configurationId);
        // A lower PI limit can leave several excess choices. Let the user remove them one at a time.
        if (ordered.Length > option.MaxCount) {
            var previous = ExplicitOptionIntent.ReadActiveCases(option, config).Select(item => item.Name).ToHashSet();
            if (previous.SetEquals(ordered)) {
                return;
            }
            if (!previous.IsSupersetOf(ordered)) {
                throw new ArgumentException($"option {optionName} 的勾选项超过上限。", nameof(selectedCases));
            }
        }

        SaveConfiguration(ReplaceExplicit(config, optionName, ExplicitOptionIntent.CreateSelectedCases(ordered)));
    }

    public LaunchManifest CreateLaunchManifest(Guid workerInstanceId) => new(
        ProtocolConstants.LaunchContextVersion, workerInstanceId,
        _project.RuntimeProfileDigest, _project.ProjectRoot, _project.Provenance,
        _project.Controller, _project.Resources, _project.Agent);

    public RunStartAttempt CreateRunStartAttempt(bool startupOnly = false) =>
        CreateRunStartAttempt(ActiveConfigurationId, startupOnly);

    public RunStartAttempt CreateRunStartAttempt(Guid configurationId, bool startupOnly = false,
        IReadOnlyDictionary<(string Option, string Input), string>? inputDrafts = null)
    {
        var config = FindConfiguration(configurationId);
        var skippedMessage = GetSkippedPretaskMessage(config);
        if (skippedMessage is { } skipped) {
            _log?.Invoke(skipped);
        }
        var selected = GetExecutionItems(config, startupOnly);
        if (selected.Length == 0) {
            throw new InvalidOperationException(skippedMessage ?? "请先勾选要执行的任务或前置任务。");
        }

        var passwords = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<RunPlanItem>(selected.Length);
        var global = ProtocolJson.ToElement(new { });
        foreach (var choice in selected) {
            if (inputDrafts is { Count: > 0 }) {
                var view = BuildConfiguration(config, choice.Name, choice.Kind);
                foreach (var (option, _, _) in view.ActiveOptions) {
                    foreach (var input in option.Inputs) {
                        if (inputDrafts.TryGetValue((option.Name, input.Name), out var draft)
                            && input.CheckValue(draft) is { } error) {
                            throw new InvalidOperationException($"参数“{option.Label} · {input.Label}”：{error}");
                        }
                    }
                }
            }
            if (choice.Kind == PlanItemKind.Pretask) {
                var item = FindPretask(choice.Name);
                var (values, argument) = ProjectOptionResolver.ResolvePretask(_project, item, config, passwords);
                var arguments = item.Arguments.ToList();
                if (item.Options.Count != 0) {
                    arguments.Add(JsonSerializer.Serialize(argument, ProtocolJson.Options));
                }
                items.Add(new RunPlanItem(Guid.NewGuid(), item.Name, item.Label, string.Empty,
                    values, ProtocolJson.ToElement(Array.Empty<object>())) {
                    Kind = PlanItemKind.Pretask,
                    Program = new PretaskCommand(ResolvePretaskExecutable(item.Executable), arguments)
                });
            } else {
                var task = FindTask(choice.Name);
                var resolved = ProjectOptionResolver.Resolve(_project, task, config, passwords);
                global = resolved.ResolvedGlobalOptions;
                items.Add(new RunPlanItem(Guid.NewGuid(), task.Name, task.Label, task.Entry,
                    resolved.ResolvedTaskOptions, resolved.PipelineOverride));
            }
        }
        var plan = new RunPlan(ProtocolConstants.PlanVersion, DateTime.UtcNow,
            _project.Provenance, _project.RuntimeProfileDigest, global, items.ToArray());
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
        config = config with { ProjectName = ProjectName };
        _configStore.Save(config);
        _config = config;
        _configurationViews.Clear();
        _initializationWarning = null;
        _initializationException = null;
    }

    private ProjectConfigurationView BuildConfiguration(TaskConfiguration config, string name, PlanItemKind kind)
    {
        return kind == PlanItemKind.Pretask ? new([], BuildEditors(FindPretask(name).Options, config))
            : new(BuildEditors(_project.GlobalOptions, config), BuildEditors(FindTask(name).Options, config));
    }

    private IReadOnlyList<ProjectOptionEditor> BuildEditors(IEnumerable<string> names, TaskConfiguration config) =>
        names.Select(name => BuildEditor(name, config)).ToArray();

    private ProjectOptionEditor BuildEditor(string optionName, TaskConfiguration config)
    {
        var option = FindOption(optionName);
        if (option.Kind == ProjectOptionKind.Input) {
            var inputs = ExplicitOptionIntent.ReadInputs(option, config);
            return new ProjectOptionEditor(
                option.Name, option.Label, option.Description, ProjectOptionKind.Input,
                inputs.Any(input => input.IsExplicit), null, null, [], inputs, []);
        }

        var active = ExplicitOptionIntent.ReadActiveCases(option, config);
        var checkbox = option.Kind == ProjectOptionKind.Checkbox;
        var children = BuildEditors(active.SelectMany(item => item.Options), config);
        return new ProjectOptionEditor(
            option.Name, option.Label, option.Description, option.Kind,
            config.ExplicitOptions.ContainsKey(option.Name), checkbox ? null : active[0].Name, option.DefaultCase,
            option.Cases.Select(item => new ProjectCaseEditor(
                item.Name, item.Label, item.Description)).ToArray(),
            [],
            children) {
            SelectedCases = active.Select(item => item.Name).ToArray(),
            MinCount = option.MinCount, MaxCount = option.MaxCount
        };
    }

    private TaskDefinition FindTask(string taskName) =>
        _project.Tasks.SingleOrDefault(task => task.Name == taskName)
        ?? throw new ArgumentException($"PI 中不存在 task：{taskName}。", nameof(taskName));

    private PretaskDefinition FindPretask(string name) =>
        _project.Pretasks.SingleOrDefault(item => item.Name == name)
        ?? throw new ArgumentException($"PI 中不存在 pretask：{name}。", nameof(name));

    // The loader has rejected drive- and root-relative paths; a path with a separator is relative to the project root.
    private string ResolvePretaskExecutable(string executable)
    {
        return executable.Contains('/') || executable.Contains('\\')
            ? Path.GetFullPath(executable, _project.ProjectRoot) : executable;
    }

    private OptionDefinition FindOption(string optionName) =>
        _project.Options.GetValueOrDefault(optionName)
        ?? throw new ArgumentException($"PI 中不存在 option：{optionName}。", nameof(optionName));

    private static TaskConfiguration ReplaceExplicit(TaskConfiguration config, string optionName, JsonElement? value)
    {
        var values = config.ExplicitOptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (value is { } intent) {
            values[optionName] = intent;
        } else {
            values.Remove(optionName);
        }
        return config with { ExplicitOptions = values };
    }
}
