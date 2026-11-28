using System.Text.Json;
using System.Text.Json.Serialization;
using Gui.Infrastructure;
using Protocol;

namespace Gui.Views;

// A configuration's 运行记录: how its latest Run ended, as it was then. It is stored as it is, so it and its items keep
// no parameters, commands, results or error details.
internal sealed record RunRecord(Guid RunId, RunState State, DateTime? StartedAtUtc, DateTime? EndedAtUtc,
    IReadOnlyList<PlanItemProgressView> Items)
{
    internal RunProgressView View => new(false, State, Items, null, StartedAtUtc, EndedAtUtc);

    internal static bool Ended(RunSnapshot run) => run.State is RunState.Succeeded or RunState.Failed
        or RunState.Cancelled;

    internal static RunRecord From(RunSnapshot run) =>
        new(run.RunId, run.State, run.StartedAtUtc, run.EndedAtUtc, RunProgress.Create(run, false).Items);
}

// Owns which configuration each Run belongs to and each configuration's 运行记录, and keeps both on disk. A Run is
// claimed before its start is submitted; evidence that it was admitted retires the configuration's older record and
// claims, and its ended result becomes the record. The Worker's Last Run is not one: it is global and goes with the
// Worker. A file that cannot be read or written is only logged, so it never stands in the way of a Run.
internal sealed class RunRecords
{
    private const int FileVersion = 1;
    private static readonly JsonSerializerOptions FileOptions = new() {
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };

    private sealed record Claimed(Guid RunId, Guid ConfigurationId);
    private sealed record Stored(int Version, Dictionary<Guid, RunRecord> Records, List<Claimed> Claims);

    private readonly string _path;
    private readonly AppLogger _logger;
    // Runs without an ended result yet, in the order they were last submitted.
    private readonly List<Claimed> _claims = [];
    private readonly Dictionary<Guid, RunRecord> _records = [];

    internal RunRecords(string stateDirectory, AppLogger logger)
    {
        _path = Path.Combine(stateDirectory, "run-records.json");
        _logger = logger;
        Load();
    }

    internal RunRecord? For(Guid configurationId) => _records.GetValueOrDefault(configurationId);

    // A retry with the same Run ID claims it again, so it counts from its latest submission.
    internal void Claim(Guid runId, Guid configurationId)
    {
        _claims.RemoveAll(claim => claim.RunId == runId);
        _claims.Add(new(runId, configurationId));
        Save();
    }

    // Evidence that the Run was admitted, with its snapshot once it has ended. Evidence for an unclaimed Run changes
    // nothing, and that includes any Run older than the configuration's latest admitted one.
    internal void Observe(Guid runId, RunSnapshot? ended = null)
    {
        var index = _claims.FindIndex(claim => claim.RunId == runId);
        if (index < 0) {
            return;
        }
        var claim = _claims[index];
        var changed = _records.Remove(claim.ConfigurationId);
        foreach (var older in _claims.Take(index).Where(older => older.ConfigurationId == claim.ConfigurationId)
            .ToArray()) {
            changed |= _claims.Remove(older);
        }
        if (ended is not null && RunRecord.Ended(ended)) {
            _records[claim.ConfigurationId] = RunRecord.From(ended);
            changed |= _claims.Remove(claim);
        }
        // The same evidence arrives with every observation; only a change is written.
        if (changed) {
            Save();
        }
    }

    internal void Forget(Guid configurationId)
    {
        var removed = _records.Remove(configurationId);
        if (_claims.RemoveAll(claim => claim.ConfigurationId == configurationId) > 0 || removed) {
            Save();
        }
    }

    private void Load()
    {
        try {
            if (!File.Exists(_path)) {
                return;
            }
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(_path), FileOptions);
            // Nullable annotations do not reach into collections.
            if (stored is not { Version: FileVersion } || stored.Claims.Any(claim => claim is null)
                || stored.Records.Values.Any(record => record is null || record.Items.Any(item => item is null))) {
                throw new InvalidDataException("运行记录的版本或内容无效。");
            }
            _claims.AddRange(stored.Claims);
            foreach (var (configurationId, record) in stored.Records) {
                _records[configurationId] = record;
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException) {
            _logger.Warn("读取运行记录失败，已忽略原有记录。", exception);
        }
    }

    private void Save()
    {
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temporary,
                JsonSerializer.SerializeToUtf8Bytes(new Stored(FileVersion, _records, _claims), FileOptions));
            File.Move(temporary, _path, overwrite: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            _logger.Warn("保存运行记录失败。", exception);
            try {
                File.Delete(temporary);
            } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) {
                _logger.Warn("删除运行记录临时文件失败。", cleanup);
            }
        }
    }
}
