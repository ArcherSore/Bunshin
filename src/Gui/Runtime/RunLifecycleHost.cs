using System.Collections.Concurrent;
using System.Windows.Threading;
using Gui.Infrastructure;
using Gui.Worker;
using Gui.Views;
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
    private readonly Func<Payload, bool> _present;
    private readonly Action _requestQuit;
    private readonly AppLogger _logger;
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private readonly ConcurrentQueue<LifecycleEvent> _events = new();
    private readonly Dictionary<QueryId, (CancellationTokenSource Cancellation, Task Completion)> _queries = [];
    private readonly Dictionary<long, ITimer> _timers = [];
    private bool _disposed;

    private readonly Dictionary<RunKey, TaskCompletionSource> _posted = [];
    private readonly Queue<CompleteScheduled> _completed = new();
    internal RunOrchestration Orchestration { get; private set; } = new(new());
    private RunLifecycleState State => Orchestration.Lifecycle;
    internal event Action<RunSnapshot>? ScheduledCompleted;
    internal event Func<ScheduledSettlement, bool, Task>? CleanupPending;
    internal event Action? Changed;

    internal RunLifecycleHost(WorkerCoordinator worker, Dispatcher dispatcher, Func<string> configurationName,
        Func<Payload, bool> present, Action requestQuit, AppLogger logger, TimeProvider? clock = null)
    {
        _worker = worker;
        _dispatcher = dispatcher;
        _configurationName = configurationName;
        _present = present;
        _requestQuit = requestQuit;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _origin = _clock.GetTimestamp();
        worker.SubscribeStartEvidence(Observe);
    }

    private void Observe(object? sender, WorkerObservationBatch batch) => Enqueue(new LifecycleEvent.Observed(batch));

    internal void EnvironmentRecovered() => Enqueue(new LifecycleEvent.EnvironmentRecovered());

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

    internal RunOrchestration ReadOrchestration()
    {
        CommitEvidence();
        return Orchestration;
    }

    internal void SetOperation(PendingOperation operation, bool scheduled)
    {
        Orchestration = scheduled ? Orchestration with { ScheduledOperation = operation }
            : Orchestration with { WindowOperation = operation };
        PostDeliveries();
    }

    internal void SetCleanup(RunKey key, CleanupStage stage, Exception? failure = null)
    {
        if (Orchestration.Scheduled is { } own && own.Key == key)
            Orchestration = Orchestration with { Scheduled = own with { Cleanup = stage, Failure = failure } };
    }

    internal Task DeliverScheduledCleanupAsync(bool gateHeld = false)
    {
        if (_disposed || State.Closing != null || Orchestration.Scheduled is not { NeedsCleanup: true } own
            || Orchestration.PendingConclusions.Any(d => d.Item.Key == own.Key)) return Task.CompletedTask;
        if (!gateHeld && (own.Cleanup != CleanupStage.Pending
            || State.Current is not { Fresh: true, WorkerSnapshot: { } worker }
            || worker.WorkerInstanceId != own.Key.WorkerInstanceId)) return Task.CompletedTask;
        return CleanupPending?.Invoke(own, gateHeld) ?? Task.CompletedTask;
    }

    // A modal presenter pumps the Dispatcher. Signal entry, so a replacement can proceed
    // inside that frame and never depends on a user's receipt.
    internal async Task IssuePendingPresentationsAsync()
    {
        CommitEvidence();
        while (State.Closing == null) {
            var waiting = Orchestration.PendingConclusions.Select(d => d.Item.Key).Distinct()
                .Where(_posted.ContainsKey).Select(key => _posted[key].Task).ToArray();
            if (waiting.Length == 0) return;
            await Task.WhenAll(waiting);
            CommitEvidence();
        }
    }

    // Gate reads commit the quit reservation without re-entering the application exit routine.
    // The worker callback (or presentation receipt) invokes it on its normal dispatcher turn.
    private void Drain()
    {
        CommitEvidence();
        while (!_disposed && State.ReadyDeliveries(HoldManualErrors).FirstOrDefault() is { Item: RequestQuit quit })
            Reduce(new LifecycleEvent.DispatchPending(quit.Key));
        _ = DeliverScheduledCleanupAsync();
    }

    private void CommitEvidence()
    {
        _dispatcher.VerifyAccess();
        while (!_disposed && _events.TryDequeue(out var input)) Reduce(input);
        PostDeliveries();
    }

    private bool HoldManualErrors => Orchestration.WindowOperation is PendingOperation.StartingRun or PendingOperation.PreparingEnvironment;

    private void PostDeliveries()
    {
        if (_disposed) return;
        foreach (var item in State.ReadyDeliveries(HoldManualErrors)) {
            var key = item.Item.Key;
            if (item.Item is RequestQuit || _posted.ContainsKey(key)) continue;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _posted.Add(key, started);
            _dispatcher.BeginInvoke(() => {
                Drain();
                _posted.Remove(key);
                started.SetResult();
                if (!_disposed) Reduce(new LifecycleEvent.DispatchPending(key));
                PostDeliveries();
            });
        }
    }

    private void Reduce(LifecycleEvent input)
    {
        if (input is LifecycleEvent.Observed { Batch.ObservedSnapshot.ActiveRun: not null } observed)
            input = observed with { ConfigurationName = _configurationName() };
        if (input is LifecycleEvent.RetryDue due) CancelTimer(due.Token);
        var transition = RunLifecycleReducer.Reduce(State, input with { At = _clock.GetElapsedTime(_origin) },
            dispatchDelivery: input is LifecycleEvent.DispatchPending, holdManualErrors: HoldManualErrors);
        Orchestration = Orchestration with { Lifecycle = transition.State };
        if (input is LifecycleEvent.SubmissionStarted { Intent.Source: RunSource.Scheduled } submission)
            Orchestration = Orchestration with { Scheduled = new(submission.Key, submission.Intent.ConfigurationName) };
        foreach (var completion in transition.Effects.OfType<CompleteScheduled>()) {
            if (Orchestration.Scheduled is { } own && own.Key == completion.Run)
                Orchestration = Orchestration with { Scheduled = own with { Completion = completion.Terminal.State,
                    Cleanup = completion.Terminal.State == RunState.Succeeded ? CleanupStage.Pending : CleanupStage.None } };
            _completed.Enqueue(completion);
        }
        if (State.ExitCommitted) {
            Orchestration = new(State);
            _completed.Clear();
        }
        PostDeliveries();
        while (State.Closing == null && _completed.TryDequeue(out var completed)) {
            ScheduledCompleted?.Invoke(completed.Terminal);
        }
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
                        if (delivery.Item is RequestQuit) _requestQuit();
                    else presented = _present(delivery.Item);
                    } catch (Exception error) {
                        _logger.Error($"交付 {delivery.Item.GetType().Name} 失败：Run {delivery.Item.Key.RunId:D}。", error);
                    } finally {
                        Apply(new LifecycleEvent.Delivered(delivery.Id, presented));
                    }
                    break;
            }
        }
        Changed?.Invoke();
    }

    // Reuse an in-flight ordered read and its original deadline. During backoff, issue one now.
    internal async Task ConfirmPendingStartAsync()
    {
        CommitEvidence();
        if (Orchestration.PendingStart(_worker.ReadPublishedSnapshot()) is not { } key) return;
        Apply(new LifecycleEvent.ConfirmNow(key));
        if (State.Runs.GetValueOrDefault(key)?.Confirmation.InFlight is { } id
            && _queries.TryGetValue(id, out var query)) {
            await query.Completion;
            Drain();
        }
    }

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
