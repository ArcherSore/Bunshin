using System.Collections.Concurrent;
using System.Windows.Threading;
using Gui.Infrastructure;
using Gui.Worker;
using ProjectModel;
using Protocol;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Runtime;

// The dispatcher is the sole reader. Worker callbacks only enqueue, including when the
// coordinator publishes a write boundary synchronously while holding its connection lock.
internal sealed class RunLifecycleHost : IDisposable
{
    private readonly WorkerCoordinator _worker;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string> _configurationName;
    private readonly Func<Payload, bool> _deliver;
    private readonly AppLogger _logger;
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private readonly ConcurrentQueue<LifecycleEvent> _events = new();
    private readonly Dictionary<QueryId, (CancellationTokenSource Cancellation, Task Completion)> _queries = [];
    private readonly Dictionary<long, ITimer> _timers = [];
    private bool _disposed;

    internal RunLifecycleState State { get; private set; } = new();
    internal event Action? Changed;

    internal RunLifecycleHost(WorkerCoordinator worker, Dispatcher dispatcher, Func<string> configurationName,
        Func<Payload, bool> deliver, AppLogger logger, TimeProvider? clock = null)
    {
        _worker = worker;
        _dispatcher = dispatcher;
        _configurationName = configurationName;
        _deliver = deliver;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _origin = _clock.GetTimestamp();
        worker.SubscribeStartEvidence(Observe);
    }

    private void Observe(object? sender, WorkerObservationBatch batch) => Enqueue(new LifecycleEvent.Observed(batch));

    private void Enqueue(LifecycleEvent input)
    {
        _events.Enqueue(input);
        _dispatcher.BeginInvoke(Drain);
    }

    internal void Apply(LifecycleEvent input)
    {
        _dispatcher.VerifyAccess();
        _events.Enqueue(input);
        Drain();
    }

    internal void Drain()
    {
        _dispatcher.VerifyAccess();
        while (!_disposed && _events.TryDequeue(out var input)) {
            if (input is LifecycleEvent.Observed { Batch.ObservedSnapshot.ActiveRun: not null } observed)
                input = observed with { ConfigurationName = _configurationName() };
            if (input is LifecycleEvent.RetryDue due) CancelTimer(due.Token);
            var transition = RunLifecycleReducer.Reduce(State, input with { At = _clock.GetElapsedTime(_origin) },
                dispatchDelivery: _events.IsEmpty);
            State = transition.State;
            foreach (var effect in transition.Effects) {
                switch (effect) {
                    case QueryRun query: _ = QueryAsync(query); break;
                    case CancelQuery cancel:
                        if (_queries.TryGetValue(cancel.Id, out var request)) request.Cancellation.Cancel();
                        break;
                    case ArmRetry retry:
                        var delay = retry.Due - _clock.GetElapsedTime(_origin);
                        _timers.Add(retry.Token, _clock.CreateTimer(_ => Enqueue(new LifecycleEvent.RetryDue(retry.Key, retry.Token)),
                            null, delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan));
                        break;
                    case CancelRetry cancel: CancelTimer(cancel.Token); break;
                    case SubmissionConclusion result:
                        _logger.Info($"启动确认 {result.Conclusion}：runId={result.Submission.Run.RunId:D}。");
                        break;
                    case Deliver delivery:
                        var presented = false;
                        try {
                            presented = _deliver(delivery.Item);
                        } catch (Exception error) {
                            _logger.Error($"交付 {delivery.Item.GetType().Name} 失败：Run {delivery.Item.Key.RunId:D}。", error);
                        } finally {
                            _events.Enqueue(new LifecycleEvent.Delivered(delivery.Id, presented));
                        }
                        break;
                }
            }
            Changed?.Invoke();
        }
    }

    internal RunKey? PendingStart(WorkerCoordinatorSnapshot view) =>
        State.Runs.Values.FirstOrDefault(r => r.Owner != null && !r.Settled
            && (view.WorkerSnapshot == null || view.WorkerSnapshot.WorkerInstanceId == r.Key.WorkerInstanceId)
            && !(view.Fresh && view.WorkerSnapshot is { } snapshot
                && (Matches(snapshot.ActiveRun, r) || Matches(snapshot.LastRun, r))))?.Key;

    private static bool Matches(RunSnapshot? snapshot, TrackedRun run) =>
        snapshot?.RunId == run.Key.RunId && snapshot.PlanDigest == run.Digest;

    // Reuse an in-flight ordered read and its original deadline. During backoff, issue one now.
    internal async Task ConfirmPendingStartAsync()
    {
        Drain();
        if (PendingStart(_worker.ReadPublishedSnapshot()) is not { } key) return;
        Apply(new LifecycleEvent.ConfirmNow(key));
        if (State.Runs.GetValueOrDefault(key)?.Confirmation.InFlight is { } id
            && _queries.TryGetValue(id, out var query)) {
            await query.Completion;
            Drain();
        }
    }

    internal bool WaitingForQuit(RunKey key) => State.Quits.ContainsKey(key)
        || State.Deliveries.Any(d => d.Item is RequestQuit quit && quit.Key == key);

    internal async Task SubmitAsync(RunIntent intent, RunKey key, RunStartAttempt attempt)
    {
        if (_disposed || State.Closing != null || State.ExitCommitted) throw new OperationCanceledException("应用正在退出。");
        var submission = new Submission(Guid.NewGuid(), intent.Id, key, attempt.PlanDigest);
        Apply(new LifecycleEvent.SubmissionStarted(intent, key, submission, attempt));
        try {
            var reply = await _worker.SendStartAsync(submission, attempt);
            if (reply.StartReply is StartReplyKind.Accepted or StartReplyKind.AlreadyAccepted) {
                _logger.Info($"run.start {reply.ReplyCode}：runId={key.RunId:D}；planDigest={attempt.PlanDigest}。 ");
            }
        } catch (Exception error) {
            // SendStartAsync publishes the original failure and body boundary before throwing.
            _logger.Warn($"run.start 调用未完成：runId={key.RunId:D}。", error);
        }
        Drain();
    }

    private async Task QueryAsync(QueryRun query)
    {
        using var request = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queries.Add(query.Id, (request, completion.Task));
        try {
            await _worker.QueryRunAsync(query.Key, query.Id, request.Token);
        } catch (Exception error) {
            // QueryRunAsync also publishes failures before it completes.
            if (!request.IsCancellationRequested) _logger.Debug($"定向查询未完成：runId={query.Key.RunId:D}，{error.Message}");
        } finally {
            _queries.Remove(query.Id);
            completion.SetResult();
        }
    }

    private void CancelTimer(long token)
    {
        if (_timers.Remove(token, out var timer)) timer.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        _worker.ObservationReceived -= Observe;
        foreach (var timer in _timers.Values) timer.Dispose();
        foreach (var query in _queries.Values.ToArray()) query.Cancellation.Cancel();
    }
}
