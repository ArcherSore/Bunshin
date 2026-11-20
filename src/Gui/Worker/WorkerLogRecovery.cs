using Gui.Infrastructure;
using Protocol;

namespace Gui.Worker;

internal sealed class WorkerLogRecovery(
    object coordinatorGate, AppLogger logger, Func<Guid, bool> isConnectedInstanceLocked,
    Func<LogGetSinceRequest, CancellationToken, Task<LogGetSinceResponse>> readLogsAsync,
    Action<WorkerLogEntry> publishLog)
{
    private static readonly TimeSpan LogRecoveryRetryDelay = TimeSpan.FromSeconds(1);
    private readonly WorkerLogSequenceTracker _logSequence = new();
    private Task? _logRecoveryTask;
    private int _logRecoveryGeneration;

    // Connection changes take this before the coordinator gate so they wait for in-flight LogReceived callbacks.
    internal object DispatchGate { get; } = new();

    internal int ConnectionAdmittedLocked(Guid workerInstanceId)
    {
        _logSequence.BeginWorkerInstance(workerInstanceId);
        InvalidateConnectionLocked();
        return _logRecoveryGeneration;
    }

    internal void InvalidateConnectionLocked()
    {
        _logRecoveryGeneration++;
        _logRecoveryTask = null;
    }

    internal void ApplyLiveLog(
        Guid workerInstanceId, int logRecoveryGeneration, WorkerLogEntry entry, CancellationToken cancellationToken)
    {
        var disposition = ApplyObservedLog(workerInstanceId, logRecoveryGeneration, entry);
        if (disposition == WorkerLogSequenceDisposition.Gap) {
            _ = EnsureLogRecoveryAsync(workerInstanceId, logRecoveryGeneration, entry.Sequence, cancellationToken);
        }
    }

    internal Task EnsureLogRecoveryAsync(
        Guid workerInstanceId, int logRecoveryGeneration, long targetSequence, CancellationToken cancellationToken)
    {
        lock (coordinatorGate) {
            if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)) {
                return Task.CompletedTask;
            }
            _logSequence.ObserveTarget(targetSequence);
            if (_logRecoveryTask is { IsCompleted: false }) {
                return _logRecoveryTask;
            }
            if (_logSequence.LastContiguousSequence >= _logSequence.HighestObservedSequence) {
                return Task.CompletedTask;
            }

            _logRecoveryTask = Task.Run(
                () => RunLogRecoveryAsync(workerInstanceId, logRecoveryGeneration, cancellationToken),
                CancellationToken.None);
            return _logRecoveryTask;
        }
    }

    private async Task RunLogRecoveryAsync(Guid workerInstanceId, int logRecoveryGeneration, CancellationToken cancellationToken)
    {
        var allowRestart = true;
        try {
            while (true) {
                try {
                    await RecoverLogsAsync(workerInstanceId, logRecoveryGeneration, cancellationToken);
                    return;
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    allowRestart = false;
                    return;
                } catch (Exception exception) {
                    logger.Warn($"恢复 Worker 日志失败，将重试：{exception.GetBaseException().Message}");
                }

                lock (coordinatorGate) {
                    if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)
                        || _logSequence.LastContiguousSequence >= _logSequence.HighestObservedSequence) {
                        return;
                    }
                }
                await Task.Delay(LogRecoveryRetryDelay, cancellationToken);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            allowRestart = false;
        } finally {
            var restart = false;
            lock (coordinatorGate) {
                if (IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)) {
                    _logRecoveryTask = null;
                    restart = allowRestart
                              && _logSequence.LastContiguousSequence < _logSequence.HighestObservedSequence;
                }
            }
            if (restart) {
                _ = EnsureLogRecoveryAsync(
                    workerInstanceId, logRecoveryGeneration, targetSequence: 0, cancellationToken);
            }
        }
    }

    private async Task RecoverLogsAsync(Guid workerInstanceId, int logRecoveryGeneration, CancellationToken cancellationToken)
    {
        while (true) {
            long afterSequence;
            lock (coordinatorGate) {
                if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)
                    || _logSequence.LastContiguousSequence >= _logSequence.HighestObservedSequence) {
                    return;
                }
                afterSequence = _logSequence.LastContiguousSequence;
            }

            var page = await readLogsAsync(new LogGetSinceRequest(afterSequence, 500), cancellationToken);
            string? gapWarning = null;
            lock (coordinatorGate) {
                if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)) {
                    return;
                }
                _logSequence.ObserveTarget(page.LastLogSequence);
                if (page.Gap) {
                    _logSequence.SkipToFirstAvailable(page.FirstAvailableSequence);
                    gapWarning = $"Worker 日志存在断档：{page.MissingFromSequence}-{page.MissingToSequence}。 ";
                }
            }
            if (gapWarning is not null) {
                logger.Warn(gapWarning);
            }

            foreach (var entry in page.Entries) {
                ApplyRecoveredLog(workerInstanceId, logRecoveryGeneration, entry);
            }

            lock (coordinatorGate) {
                if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)) {
                    return;
                }
                if (_logSequence.LastContiguousSequence == afterSequence
                    && _logSequence.LastContiguousSequence < _logSequence.HighestObservedSequence) {
                    throw new ProtocolException("log.getSince 未推进 Log Transport Cursor。 ");
                }
            }
        }
    }

    private void ApplyRecoveredLog(Guid workerInstanceId, int logRecoveryGeneration, WorkerLogEntry entry)
    {
        var disposition = ApplyObservedLog(workerInstanceId, logRecoveryGeneration, entry);
        if (disposition == WorkerLogSequenceDisposition.Gap) {
            throw new ProtocolException($"log.getSince 返回非连续 sequence：{entry.Sequence}。 ");
        }
    }

    private WorkerLogSequenceDisposition? ApplyObservedLog(Guid workerInstanceId, int logRecoveryGeneration, WorkerLogEntry entry)
    {
        WorkerLogSequenceDisposition disposition;
        lock (DispatchGate) {
            lock (coordinatorGate) {
                if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)) {
                    return null;
                }
                disposition = _logSequence.Observe(entry.Sequence);
            }
            if (disposition == WorkerLogSequenceDisposition.Contiguous) {
                publishLog(entry);
            }
        }
        return disposition;
    }

    private bool IsActiveLogConnectionLocked(Guid workerInstanceId, int logRecoveryGeneration) =>
        _logRecoveryGeneration == logRecoveryGeneration && isConnectedInstanceLocked(workerInstanceId)
        && _logSequence.WorkerInstanceId == workerInstanceId;
}
