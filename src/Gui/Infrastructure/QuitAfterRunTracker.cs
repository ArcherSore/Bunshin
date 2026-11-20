using Protocol;
using Gui.Worker;

namespace Gui.Infrastructure;

internal sealed class QuitAfterRunTracker
{
    private Guid? _runId;

    internal bool IsWaitingFor(Guid runId) => _runId == runId;
    internal void Await(Guid runId) => _runId = runId;
    internal void Abandon() => _runId = null;

    internal bool Observe(WorkerCoordinatorSnapshot observation)
    {
        if (observation.Fresh && observation.WorkerSnapshot?.LastRun is { } run && run.RunId == _runId
            && run.State is RunState.Succeeded or RunState.Failed or RunState.Cancelled) {
            _runId = null;
            return true;
        }
        return false;
    }
}
