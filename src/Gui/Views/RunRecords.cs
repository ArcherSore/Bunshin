using Protocol;

namespace Gui.Views;

// A configuration's 运行记录: how its latest Run ended, as it was then. It keeps no parameters.
internal sealed record RunRecord(Guid RunId, RunState State, DateTime? StartedAtUtc, DateTime? EndedAtUtc,
    IReadOnlyList<PlanItemProgressView> Items)
{
    internal RunProgressView View => new(false, State, Items, null, StartedAtUtc, EndedAtUtc);

    internal static bool Ended(RunSnapshot run) => run.State is RunState.Succeeded or RunState.Failed
        or RunState.Cancelled;

    internal static RunRecord From(RunSnapshot run) =>
        new(run.RunId, run.State, run.StartedAtUtc, run.EndedAtUtc, RunProgress.Create(run, false).Items);
}

// Owns which configuration each Run belongs to and each configuration's 运行记录. A Run is claimed before its start is
// submitted; the first evidence that it was admitted retires the configuration's older record and claims, and its
// ended result becomes the record. The Worker's Last Run is not one: it is global and goes with the Worker.
internal sealed class RunRecords
{
    private readonly Dictionary<Guid, (Guid Configuration, long Order)> _claims = [];
    private readonly Dictionary<Guid, (Guid RunId, long Order)> _admitted = [];
    private readonly Dictionary<Guid, RunRecord> _records = [];
    private long _order;

    internal RunRecord? For(Guid configurationId) => _records.GetValueOrDefault(configurationId);

    // A retry with the same Run ID claims it again, so it counts from its latest submission.
    internal void Claim(Guid runId, Guid configurationId) => _claims[runId] = (configurationId, ++_order);

    // Evidence that the Run was admitted, with its snapshot once it has ended. Evidence for an unclaimed Run, or for
    // one older than the configuration's latest admitted Run, changes nothing.
    internal void Observe(Guid runId, RunSnapshot? ended = null)
    {
        if (!_claims.TryGetValue(runId, out var claim)) {
            return;
        }
        var configuration = claim.Configuration;
        if (_admitted.GetValueOrDefault(configuration) is var latest && latest.RunId != runId) {
            if (latest.Order > claim.Order) {
                _claims.Remove(runId);
                return;
            }
            _admitted[configuration] = (runId, claim.Order);
            _records.Remove(configuration);
            foreach (var older in _claims.Where(pair => pair.Value.Configuration == configuration
                && pair.Value.Order < claim.Order).Select(pair => pair.Key).ToArray()) {
                _claims.Remove(older);
            }
        }
        if (ended is not null && RunRecord.Ended(ended)) {
            _records[configuration] = RunRecord.From(ended);
            _claims.Remove(runId);
        }
    }

    internal void Forget(Guid configurationId)
    {
        _records.Remove(configurationId);
        _admitted.Remove(configurationId);
        foreach (var runId in _claims.Where(pair => pair.Value.Configuration == configurationId)
            .Select(pair => pair.Key).ToArray()) {
            _claims.Remove(runId);
        }
    }
}
