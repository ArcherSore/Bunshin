using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ProjectModel;

public sealed record ConfigurationSet
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string? ProjectName { get; init; }
    public Guid ActiveConfigurationId { get; init; }
    public required IReadOnlyList<TaskConfiguration> Configurations { get; init; }
}

public sealed record TaskConfiguration
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> SelectedTasks { get; init; } = [];
    public IReadOnlyList<string> SelectedPretasks { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> ExplicitOptions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public sealed class UnsupportedConfigurationVersionException(int version) : NotSupportedException(
    $"任务配置使用较新的格式（版本 {version}），原文件已保留。请更新 Bunshin 后重新加载。")
{
}

internal sealed class ConfigurationSetStore
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;
    private readonly Action<string>? _log;
    private bool _protectOriginal;
    private string RecentBackup => _path + ".bak";
    internal string? LoadWarning { get; private set; }
    internal Exception? LoadException { get; private set; }
    private string? _pendingChanges;
    internal bool Unreadable { get; private set; }
    internal bool WasMissing { get; private set; }

    internal ConfigurationSetStore(string path, Action<string>? log = null)
    {
        _path = Path.GetFullPath(path);
        _log = log;
    }

    internal ConfigurationSet Load(ProjectDefinition project, ProjectDefinition? previousProject)
    {
        ConfigurationSet config;
        var projectName = project.Provenance.Name;
        var changes = new List<string>();
        string? source = null;
        string? declaredProject = null;
        try {
            using var stream = File.OpenRead(_path);
            using var document = JsonDocument.Parse(stream, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ProjectName", out var name)
                && name.ValueKind == JsonValueKind.String) {
                declaredProject = name.GetString();
            }
            config = ReadPrimary(root, changes);
        } catch (Exception exception) when (IsReadFailure(exception)) {
            if (declaredProject is not null && declaredProject != projectName) {
                throw WrongProject();
            }
            var missing = exception is FileNotFoundException or DirectoryNotFoundException;
            changes.Clear();
            var backup = ReadBackup(RecentBackup, projectName, out var firstMissing);
            source = RecentBackup;
            if (backup is null) {
                source += ".2";
                backup = ReadBackup(source, projectName, out var secondMissing);
                if (backup is null && missing && firstMissing && secondMissing) {
                    WasMissing = true;
                    _log?.Invoke("任务配置文件不存在，使用默认配置。");
                    return CreateEmpty() with { ProjectName = projectName };
                }
            }
            _protectOriginal = !missing;
            Unreadable = true;
            if (backup is null) {
                LoadException = exception is JsonException or ArgumentException
                    ? new InvalidDataException("任务配置的 JSON 结构无效。") : exception;
                var primary = missing ? "任务配置文件缺失" : "无法读取任务配置，原文件已保留";
                LoadWarning = $"{primary}。未找到本项目可用的备份，已显示临时空配置。请检查配置目录后重新加载。";
                return CreateEmpty() with { ProjectName = projectName };
            }
            config = backup;
        }
        if ((config.ProjectName ?? ProjectProfile.LegacyConfigurationProject) != projectName) {
            throw WrongProject();
        }
        var tasks = project.Tasks.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var pretasks = project.Pretasks.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var removed = new List<string>();
        string[] KeepDefined(IReadOnlyList<string> selected, HashSet<string> defined, string kind, string owner)
        {
            foreach (var name in selected.Where(name => !defined.Contains(name))) {
                removed.Add($"{owner} 的{kind} {name} 已从项目定义中移除");
            }
            return selected.Where(defined.Contains).ToArray();
        }
        config = config with {
            ProjectName = project.Provenance.Name,
            Configurations = config.Configurations.Select(item => item with {
                SelectedTasks = KeepDefined(item.SelectedTasks, tasks, "任务", item.Name),
                SelectedPretasks = KeepDefined(item.SelectedPretasks, pretasks, "前置任务", item.Name),
                ExplicitOptions = ExplicitOptionIntent.Reconcile(item, project, source is null ? previousProject : null,
                    removed)
            }).ToArray()
        };
        changes.AddRange(removed);
        if (changes.Count != 0 || source is not null) {
            if (changes.Count != 0) {
                _pendingChanges = $"已更新任务配置文件 [reason={string.Join("；", changes)}]";
            }
            var recovered = source is null ? null : $"已从本项目备份 {Path.GetFileName(source)} 恢复任务配置";
            try {
                Save(config, refreshBackup: source != RecentBackup || changes.Count != 0);
                if (recovered is not null) {
                    LoadWarning = $"{recovered}，已写回配置文件。{LoadWarning}";
                }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                LoadException = exception;
                LoadWarning = (recovered ?? "任务配置已加载") + "，更新结果未保存。请检查文件占用、目录写入权限和当前 Windows 用户账户后重新加载。";
            }
        } else {
            UpdateRecentBackup(config);
        }
        return config;
    }

    private static bool IsReadFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or JsonException or InvalidDataException or ArgumentException or CryptographicException;

    private static InvalidDataException WrongProject() =>
        new("任务配置属于其他项目。请恢复对应的资源包后重试，原配置已保留。");

    private static ConfigurationSet ReadPrimary(JsonElement root, List<string> changes)
    {
        ConfigurationSet config;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema)) {
            throw new InvalidDataException("任务配置缺少合法的 SchemaVersion。");
        }
        if (schema > ConfigurationSet.CurrentSchemaVersion) {
            throw new UnsupportedConfigurationVersionException(schema);
        }
        if (schema != ConfigurationSet.CurrentSchemaVersion) {
            changes.Add($"SchemaVersion {schema} -> {ConfigurationSet.CurrentSchemaVersion}");
        }
        if (schema == 1) {
            var legacy = root.Deserialize<LegacyConfig>(JsonOptions)
                ?? throw new InvalidDataException("任务配置为空。");
            var migrated = new TaskConfiguration {
                Id = Guid.NewGuid(), Name = "配置 1",
                SelectedTasks = legacy.SelectedTasks, ExplicitOptions = legacy.ExplicitOptions
            };
            config = new ConfigurationSet { ActiveConfigurationId = migrated.Id, Configurations = [migrated] };
        } else if (schema is 2 || schema == ConfigurationSet.CurrentSchemaVersion) {
            var active = root.TryGetProperty("ActiveConfigurationId", out var pointer)
                && pointer.ValueKind == JsonValueKind.String && pointer.TryGetGuid(out var id) ? id : Guid.Empty;
            var node = JsonNode.Parse(root.GetRawText())!;
            node["ActiveConfigurationId"] = active;
            node["SchemaVersion"] = ConfigurationSet.CurrentSchemaVersion;
            config = node.Deserialize<ConfigurationSet>(JsonOptions)
                ?? throw new InvalidDataException("任务配置为空。");
            Validate(config, requireActive: false);
            if (config.Configurations.Count == 0) {
                config = CreateEmpty() with { ProjectName = config.ProjectName };
                changes.Add("配置列表为空");
            } else if (!config.Configurations.Any(item => item.Id == active)) {
                config = config with { ActiveConfigurationId = config.Configurations[0].Id };
                changes.Add("激活配置不存在");
            }
        } else {
            throw new InvalidDataException($"不支持 任务配置SchemaVersion {schema}。");
        }
        Validate(config);
        return config;
    }

    // Snapshots are always written in the current schema with an explicit project identity and valid active ID.
    private static ConfigurationSet? ReadBackup(string path, string projectName, out bool missing)
    {
        missing = false;
        try {
            var data = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            using var document = JsonDocument.Parse(data, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema)
                || schema != ConfigurationSet.CurrentSchemaVersion) {
                return null;
            }
            var config = root.Deserialize<ConfigurationSet>(JsonOptions);
            if (config?.ProjectName != projectName) {
                return null;
            }
            Validate(config);
            return config;
        } catch (Exception exception) when (IsReadFailure(exception)) {
            missing = exception is FileNotFoundException or DirectoryNotFoundException;
            return null;
        }
    }

    private static ConfigurationSet CreateEmpty()
    {
        var empty = new TaskConfiguration { Id = Guid.NewGuid(), Name = "配置 1" };
        return new ConfigurationSet { ActiveConfigurationId = empty.Id, Configurations = [empty] };
    }

    internal void Save(ConfigurationSet config, bool refreshBackup = true)
    {
        Validate(config);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var data = JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
        if (_protectOriginal) {
            PreserveAndReplaceUnreadable(data);
        } else {
            WriteAtomic(_path, data, null);
        }
        _protectOriginal = false;
        Unreadable = false;
        LoadWarning = null;
        LoadException = null;
        if (_pendingChanges is not null) {
            _log?.Invoke(_pendingChanges);
            _pendingChanges = null;
        }
        if (refreshBackup) {
            UpdateRecentBackup(config, data);
        }
    }

    // With no new save, seed only a missing or unusable snapshot of an existing primary.
    private void UpdateRecentBackup(ConfigurationSet config, byte[]? savedData = null)
    {
        try {
            var hasRecent = ReadBackup(RecentBackup, config.ProjectName!, out _) is not null;
            if (hasRecent && savedData is null) {
                return;
            }
            // A later PI may reclassify any previously plain input as a password.
            var data = savedData ?? JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
            var protectedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            WriteAtomic(RecentBackup, protectedData, hasRecent ? RecentBackup + ".2" : null);
        } catch (Exception exception) when (IsReadFailure(exception)) {
            LoadException = exception;
            LoadWarning = "任务配置已保存，但最近备份未更新。请检查配置目录权限和 Windows 用户账户后再次保存。";
            _log?.Invoke(LoadWarning);
        }
    }

    private void PreserveAndReplaceUnreadable(byte[] data)
    {
        var backup = Path.Combine(Path.GetDirectoryName(_path)!,
            $"{Path.GetFileNameWithoutExtension(_path)}.invalid-{Guid.NewGuid():N}.dpapi");
        // Keep the bytes stable until replacement. The original may contain a newly designated password.
        using var original = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var bytes = new byte[checked((int)original.Length)];
        original.ReadExactly(bytes);
        try {
            WriteAtomic(backup, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser), null);
        } catch (CryptographicException exception) {
            throw new IOException("无法加密保存损坏的原配置。请检查当前 Windows 用户账户后重试。", exception);
        } finally {
            CryptographicOperations.ZeroMemory(bytes);
        }
        try {
            WriteAtomic(_path, data, null);
        } catch {
            File.Delete(backup);
            throw;
        }
        _log?.Invoke($"已备份无法读取的任务配置 [backup={Path.GetFileName(backup)}]");
    }

    private static void WriteAtomic(string path, byte[] data, string? previous)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            if (previous is null) {
                File.Move(temp, path, overwrite: true);
            } else {
                File.Replace(temp, path, previous);
            }
        } finally {
            if (File.Exists(temp)) {
                File.Delete(temp);
            }
        }
    }

    private sealed record LegacyConfig
    {
        public int SchemaVersion { get; init; }
        public IReadOnlyList<string> SelectedTasks { get; init; } = [];
        public IReadOnlyDictionary<string, JsonElement> ExplicitOptions { get; init; } =
            new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    private static void Validate(ConfigurationSet config, bool requireActive = true)
    {
        if (config.SchemaVersion != ConfigurationSet.CurrentSchemaVersion || config.Configurations is null) {
            throw new InvalidDataException("任务配置必须包含有效的 V3 配置列表。");
        }
        var ids = new HashSet<Guid>();
        foreach (var item in config.Configurations) {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Name)) {
                throw new InvalidDataException("配置 Id 必须非空且唯一，名称不能为空。");
            }
            if (!IsNameList(item.SelectedTasks) || item.ExplicitOptions is null) {
                throw new InvalidDataException("SelectedTasks 必须为不重复的 task name 列表。");
            }
            if (!IsNameList(item.SelectedPretasks)) {
                throw new InvalidDataException("SelectedPretasks 必须为不重复的 pretask name 列表。");
            }
            foreach (var (name, value) in item.ExplicitOptions) {
                if (string.IsNullOrWhiteSpace(name) || value.ValueKind != JsonValueKind.Object) {
                    throw new InvalidDataException("ExplicitOptions key 必须非空，value 必须为 object。");
                }
                ExplicitOptionIntent.ValidateStoredShape(name, value);
            }
        }
        if (requireActive && !ids.Contains(config.ActiveConfigurationId)) {
            throw new InvalidDataException("ActiveConfigurationId 不存在。");
        }
    }

    private static bool IsNameList(IReadOnlyList<string>? names) => names is not null
        && !names.Any(string.IsNullOrWhiteSpace) && names.Count == names.Distinct(StringComparer.Ordinal).Count();
}
