using System.ComponentModel;
using Gui.ChildSession;
using Protocol;
using Gui.Worker;

namespace Gui.Infrastructure;

// Ends the Runtime Environment through RuntimeEnvironment alone and observes simulated WTS, the pipe and the
// coordinator. Every caller gets the same end: stop the Active Run, log off, confirm that the Child Session and the
// Worker are gone, and only then discard the Admission.
internal static partial class SelfTestRunner
{
    // Long enough that a loaded machine never cuts a normal stop or exit short, yet below OnPoolLimit so a check that
    // waits it out still fails visibly. Checks of the expiry path itself pass ExpiringWaitLimit.
    private static readonly TimeSpan EnvironmentWaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExpiringWaitLimit = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OnPoolLimit = TimeSpan.FromSeconds(30);

    // U1, U9: the Active Run is stopped and has ended before logoff, and the Admission is discarded only after the
    // Worker has exited. The end is in progress from the stop request through the last step.
    private static void VerifyEndStopsRunFirst(AppLogger logger, string directory)
    {
        RuntimeEnvironment? environment = null;
        bool? endingAtStop = null;
        WithRunningFakeWorker(logger, Path.Combine(directory, "stop-first"), async (worker, token) => {
            var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(worker.Pipe, ProtocolOperations.RunStop, token);
            endingAtStop = environment?.Ending;
            await ReplyToFakeWorkerStopAsync(worker, stop, token);
        }, coordinator => {
            AwaitRunningWorker(coordinator);
            var logoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (bool RunEnded, bool Ending, bool Discarded)? atLogoff = null;
            var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
            wts.DuringLogoff = _ => {
                atLogoff = (coordinator.Snapshot.WorkerSnapshot?.ActiveRun is null, environment!.Ending,
                    AdmissionDiscarded(coordinator));
                logoff.TrySetResult();
            };
            using var session = wts.CreateManager(logger);
            int? waitedPid = null;
            // The wait only completes after logoff, so the end succeeds only if it waits after logging off.
            environment = CreateEnvironment(logger, session, coordinator, (pid, _) => {
                waitedPid = pid;
                return logoff.Task;
            }, TimeSpan.FromSeconds(10));
            var changes = 0;
            environment.StateChanged += (_, _) => Interlocked.Increment(ref changes);
            bool? endingAtConfirm = null;
            var ended = OnPool(() => environment.EndAsync(() => {
                endingAtConfirm = environment.Ending;
                return true;
            }));
            Require(ended == EnvironmentEndResult.Ended && atLogoff == (true, true, false)
                && waitedPid == Environment.ProcessId && AdmissionDiscarded(coordinator),
                "结束应先停止 Run 并等待其结束，再注销、等待 Worker 退出，最后丢弃 Admission。");
            Require(endingAtConfirm == false && endingAtStop == true && !environment.Ending && changes == 2
                && !environment.EndUnconfirmed, "结束期间应一直处于正在结束，结束后清除并各通知一次。");
        });
    }

    private static async Task ReplyToFakeWorkerStopAsync(FakeWorker worker, WireEnvelope request,
        CancellationToken token, string disposition = "stop_requested")
    {
        await worker.Pipe.WriteAsync(WireEnvelope.Response(request.Operation, request.RequestId!.Value,
            new RunStopResponse(disposition)), token);
        var snapshot = worker.Coordinator.Snapshot.WorkerSnapshot!;
        var cancelled = snapshot with {
            StateRevision = snapshot.StateRevision + 1, RunState = RunState.Idle, ActiveRun = null,
            LastRun = worker.ActiveRun with { State = RunState.Cancelled }
        };
        await worker.Pipe.WriteAsync(StateEvent(cancelled), token);
    }

    // U2: a stop that the Worker rejects, never answers or cannot answer because it disconnected does not block the
    // end; logging off ends the Run.
    private static void VerifyEndAfterUnconfirmedStop(AppLogger logger, string directory)
    {
        foreach (var outcome in new[] { "rejected", "unanswered", "disconnected" }) {
            WithRunningFakeWorker(logger, Path.Combine(directory, $"stop-{outcome}"), async (worker, token) => {
                var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                    worker.Pipe, ProtocolOperations.RunStop, token);
                if (outcome == "rejected") {
                    await worker.Pipe.WriteAsync(WireEnvelope.Failure(ProtocolOperations.RunStop,
                        stop.RequestId!.Value, "operation_not_allowed", "scripted stop rejection"), token);
                } else if (outcome == "disconnected") {
                    await worker.Pipe.DisposeAsync();
                }
            }, coordinator => {
                AwaitRunningWorker(coordinator);
                var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
                using var session = wts.CreateManager(logger);
                var ended = OnPool(() => CreateEnvironment(logger, session, coordinator,
                    waitLimit: ExpiringWaitLimit).EndAsync(() => true));
                Require(ended == EnvironmentEndResult.Ended && wts.LogoffAttempts.SequenceEqual([7u])
                    && AdmissionDiscarded(coordinator), $"{outcome}：Run 停止未确认时仍应注销分身并完成结束。");
            });
        }
    }

    // U3: a Faulted Worker whose stop is unconfirmed is not asked again; logging off ends its Run.
    private static void VerifyEndWithFaultedWorker(AppLogger logger, string directory)
    {
        var requests = new List<string>();
        using var listening = new CancellationTokenSource();
        WithRunningFakeWorker(logger, Path.Combine(directory, "faulted"), async (worker, token) => {
            WorkerSnapshot? snapshot;
            while ((snapshot = worker.Coordinator.Snapshot.WorkerSnapshot) is null) {
                await Task.Delay(20, token);
            }
            var faulted = snapshot with {
                StateRevision = 2, WorkerState = WorkerState.Faulted,
                WorkerReason = new StructuredReason("StopTimeout", "scripted stop timeout"),
                RunState = RunState.Stopping, ActiveRun = worker.ActiveRun with { State = RunState.Stopping }
            };
            await worker.Pipe.WriteAsync(StateEvent(faulted), token);
            await RecordRequestsAsync(worker.Pipe, requests, token, listening.Token);
        }, coordinator => {
            try {
                AwaitWorker(coordinator, snapshot => snapshot.WorkerSnapshot?.WorkerState == WorkerState.Faulted,
                    "假 Worker 未报告 Faulted。");
                var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
                using var session = wts.CreateManager(logger);
                var ended = OnPool(() => CreateEnvironment(logger, session, coordinator).EndAsync(() => true));
                Require(ended == EnvironmentEndResult.Ended && wts.LogoffAttempts.SequenceEqual([7u])
                    && AdmissionDiscarded(coordinator), "Worker Faulted 时应直接注销分身并完成结束。");
            } finally {
                listening.Cancel();
            }
        });
        Require(requests.Count == 0, "Worker Faulted 时不应再请求停止 Run。");
    }

    // U4: an end is confirmed only once the Worker has exited within the limit. Otherwise the Admission is kept and
    // the end is reported unconfirmed; ending again continues from what remains and clears that report.
    private static void VerifyEndWaitsForWorker(AppLogger logger, string directory)
    {
        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using var session = wts.CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "worker-exit"), (coordinator, _, _) => {
            var exited = false;
            var waitEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var environment = CreateEnvironment(logger, session, coordinator, async (_, token) => {
                try {
                    await Task.Delay(exited ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, token);
                } finally {
                    waitEnded.TrySetResult(token.IsCancellationRequested);
                }
            }, ExpiringWaitLimit);
            var first = OnPool(() => environment.EndAsync(() => true));
            Require(first is { Outcome: EnvironmentEndOutcome.Unconfirmed, Error.Message: "无法确认 Worker 已退出。" }
                && coordinator.TrackedWorkerPid is not null && environment.EndUnconfirmed
                && waitEnded.Task.Wait(WaitLimitMilliseconds) && waitEnded.Task.Result,
                "Worker 未在上限内退出时，结束应未确认、保留 Admission 并停止等待。");

            exited = true;
            var asked = false;
            var second = OnPool(() => environment.EndAsync(() => asked = true));
            Require(second == EnvironmentEndResult.Ended && asked && AdmissionDiscarded(coordinator)
                && !environment.EndUnconfirmed && wts.LogoffAttempts.SequenceEqual([7u]),
                "再次结束应确认 Worker 退出后丢弃 Admission，并清除未确认。");
        });
    }

    private static void VerifyStartEvidenceEnvironmentExit(AppLogger logger, string directory)
    {
        using var watchdog = new CancellationTokenSource(OnPoolLimit);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7], DuringLogoff = _ => confirmed.SetResult() };
        using var session = wts.CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "confirmed-pid"), (coordinator, record, name) => {
            var evidence = new System.Collections.Concurrent.ConcurrentQueue<WorkerObservationBatch>();
            int? pidAtExit = null;
            bool removed = false;
            coordinator.ObservationReceived += (_, batch) => {
                evidence.Enqueue(batch);
                if (batch.Kind == WorkerEvidenceKind.ProcessExited) {
                    pidAtExit = coordinator.TrackedWorkerPid;
                } else if (batch.Kind == WorkerEvidenceKind.AdmissionRemoved) {
                    removed = coordinator.TrackedAdmission is null;
                }
            };
            var peer = OnPool(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(name, record, 0, watchdog.Token));
            AwaitWorker(coordinator, state => state.Fresh, "Worker 未完成首次同步。");
            peer.Close();
            AwaitWorker(coordinator, state => state.Observation == WorkerObservation.IpcDisconnected, "存活 PID 断管后应等待重连。");
            var disconnected = evidence.Single(b => b.Kind == WorkerEvidenceKind.Disconnected);
            Require(disconnected.Error is null && !evidence.Any(b => b.Kind == WorkerEvidenceKind.ProcessExited),
                "EOF 时 PID 仍在，不能据此发布进程退出。");

            var environment = CreateEnvironment(logger, session, coordinator, (pid, token) => {
                Require(pid == record.WorkerPid, "环境等待的应是原 Admission 的 PID。");
                return confirmed.Task.WaitAsync(token);
            }, OnPoolLimit);
            var result = OnPool(() => environment.EndAsync(() => true));
            var retired = evidence.Where(b => b.Kind is WorkerEvidenceKind.Disconnected
                or WorkerEvidenceKind.ProcessExited or WorkerEvidenceKind.AdmissionRemoved).ToArray();
            Require(result == EnvironmentEndResult.Ended && removed && AdmissionDiscarded(coordinator) && pidAtExit == record.WorkerPid
                && retired.Select(b => b.Kind).SequenceEqual([WorkerEvidenceKind.Disconnected,
                    WorkerEvidenceKind.ProcessExited, WorkerEvidenceKind.AdmissionRemoved])
                && retired.All(b => b.Connection == disconnected.Connection)
                && disconnected.Connection?.WorkerInstanceId == record.WorkerInstanceId,
                "环境对原 PID 的退出确认必须在该 Admission 摘除前送达一次。");
        }, workerListed: () => true);
    }

    // U5, U10: a failed logoff, a Child Session that survives logoff and a WTS query that fails all leave the end
    // unconfirmed with the Admission kept. A Child Session that cannot be queried is not asked about.
    private static void VerifyEndUnconfirmed(AppLogger logger, string directory)
    {
        foreach (var (name, createSession, message, asks) in new (string, Func<ChildSessionManager>, string, bool)[] {
            ("logoff-failure", () => new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7], LogoffError = 5 }
                .CreateManager(logger), "注销桌面分身失败。", true),
            ("query-failure", () => new ChildSessionManager(logger,
                () => throw new Win32Exception(5, "self-test WTS failure"), () => [0u, 1u], _ => null,
                new ChildSessionCredentialStore(logger, new MemoryCredentialVault())), "无法确认桌面分身状态。", false)
        }) {
            using var session = createSession();
            WithFakeWorker(logger, Path.Combine(directory, name), (coordinator, _, _) => {
                var asked = false;
                var environment = CreateEnvironment(logger, session, coordinator);
                var ended = OnPool(() => environment.EndAsync(() => asked = true));
                Require(ended is { Outcome: EnvironmentEndOutcome.Unconfirmed } && ended.Error?.Message == message
                    && asked == asks && coordinator.TrackedWorkerPid is not null && environment.EndUnconfirmed
                    && !environment.Ending, $"{name}：结束应未确认、说明原因并保留 Admission。");
            });
        }

        var clock = new ScheduleTestClock();
        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7], SessionSurvivesLogoff = true };
        using var surviving = wts.CreateManager(logger, clock: clock);
        WithFakeWorker(logger, Path.Combine(directory, "session-survives"), (coordinator, record, _) => {
            var environment = CreateEnvironment(logger, surviving, coordinator);
            var end = environment.EndAsync();
            PumpUntil(() => clock.PendingTimers >= 2 || end.IsCompleted, "应开始等待注销确认。");
            clock.Advance(TimeSpan.FromSeconds(59));
            Require(!end.IsCompleted && coordinator.TrackedAdmission == record, "注销 59 秒时仍应等待并保留 Admission。");
            clock.Advance(TimeSpan.FromSeconds(1));
            PumpUntil(() => end.IsCompleted, "注销满 60 秒应交回闸门。");
            Require(end.GetAwaiter().GetResult().Outcome == EnvironmentEndOutcome.Unconfirmed
                && environment.EndUnconfirmed && !environment.Ending && coordinator.TrackedAdmission == record,
                "注销超时应报告结束未确认，并保留原 Admission。");

            var retry = environment.RequestEnd(() => true)!;
            PumpUntil(() => clock.PendingTimers >= 2 || retry.IsCompleted, "应能再次结束桌面分身。");
            wts.Sessions.Remove(7);
            clock.Advance(TimeSpan.FromSeconds(1));
            PumpUntil(() => retry.IsCompleted, "Windows 已不再列出原 Session 时应确认结束。");
            Require(retry.GetAwaiter().GetResult() == EnvironmentEndResult.Ended && !environment.EndUnconfirmed
                && AdmissionDiscarded(coordinator) && wts.LogoffAttempts.SequenceEqual([7u, 7u]),
                "再次结束应只注销原分身，确认消失后丢弃 Admission。");
        });
    }

    // U7, U8: the user is asked before queueing when a Runtime Environment may remain;
    // declining changes nothing. An Admission without a Child Session still needs ending.
    private static void VerifyEndConfirmation(AppLogger logger, string directory)
    {
        var none = new SimulatedWts();
        using (var session = none.CreateManager(logger)) {
            var coordinator = new WorkerCoordinator(logger, Path.Combine(directory, "none", "state"), "unused.exe",
                $"Gui.RuntimeEnvironment.SelfTest.{Guid.NewGuid():N}", usePipeAcl: false);
            try {
                var asked = false;
                var ended = OnPool(() => CreateEnvironment(logger, session, coordinator).EndAsync(() => asked = true));
                Require(ended == EnvironmentEndResult.Ended && !asked && none.LogoffAttempts.Count == 0,
                    "没有运行环境时结束不应询问或注销。");
            } finally {
                Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
            }
        }

        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using (var existing = wts.CreateManager(logger)) {
            WithFakeWorker(logger, Path.Combine(directory, "declined"), (coordinator, _, _) => {
                var environment = CreateEnvironment(logger, existing, coordinator);
                var declined = OnPool(async () => {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var asked = false;
                    var held = environment.RunExclusiveAsync(() => release.Task);
                    var ending = environment.EndAsync(() => {
                        asked = true;
                        return false;
                    });
                    var declinedBeforeRelease = asked && ending.IsCompletedSuccessfully;
                    release.SetResult();
                    await held;
                    Require(declinedBeforeRelease, "其他操作持有闸门时也应立即询问，取消退出无需等待闸门。");
                    return await ending;
                });
                Require(declined == EnvironmentEndResult.Declined && wts.LogoffAttempts.Count == 0
                    && coordinator.TrackedWorkerPid is not null && !environment.EndUnconfirmed && !environment.Ending,
                    "拒绝结束时不应注销、丢弃 Admission 或报告未确认。");
            });
        }

        using var admissionOnly = new SimulatedWts().CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "admission-only"), (coordinator, _, _) => {
            var environment = CreateEnvironment(logger, admissionOnly, coordinator);
            var asked = 0;
            var ended = OnPool(() => environment.EndAsync(() => ++asked > 0));
            Require(ended == EnvironmentEndResult.Ended && asked == 1 && AdmissionDiscarded(coordinator),
                "只剩 Admission 时仍应询问并结束。");
            Require(OnPool(() => environment.EndAsync(() => ++asked > 0)) == EnvironmentEndResult.Ended && asked == 1,
                "结束确认后再次结束不应询问。");
        });
    }

    // #147: the automatic end logs off only the Child Session it verified. One replaced after that verification is
    // left running with the Admission kept; an unchanged one is logged off by its verified ID.
    private static void VerifyVerifiedEndSkipsReplacedSession(AppLogger logger, string directory)
    {
        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using var session = wts.CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "replaced"), (coordinator, _, _) => {
            var environment = CreateEnvironment(logger, session, coordinator);
            var verified = 0;
            var replaced = OnPool(() => environment.EndVerifiedWhileExclusiveAsync(() => {
                verified++;
                // Windows replaces the Child Session once the owner has been verified.
                wts.Sessions.Remove(7);
                wts.ReportedId = 12;
                wts.Sessions.Add(12);
                return 7;
            }));
            Require(replaced == EnvironmentEndResult.Declined && verified == 1 && wts.LogoffAttempts.Count == 0
                && coordinator.TrackedWorkerPid is not null && !environment.EndUnconfirmed && !environment.Ending
                && session.HasChildSession, "核验后分身被换时，自动收尾不应注销新分身或丢弃 Admission。");

            var ended = OnPool(() => environment.EndVerifiedWhileExclusiveAsync(() => 12));
            Require(ended == EnvironmentEndResult.Ended && wts.LogoffAttempts.SequenceEqual([12u])
                && wts.LogoffTargets.SequenceEqual([(uint?)12]) && AdmissionDiscarded(coordinator) && !session.HasChildSession,
                "核验的分身未变时，自动收尾应按核验的 ID 注销并完成结束。");
        });
    }

    // U6: the end waits for the in-flight operation and discards the Admission inside the gate, so a queued operation
    // sees it discarded; the caller handles the result before that operation runs.
    private static void VerifyEndHoldsGate(AppLogger logger, string directory)
    {
        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using var session = wts.CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "gate"), (coordinator, _, _) => {
            var environment = CreateEnvironment(logger, session, coordinator);
            var (ended, seen, discardedForNext) = OnPool(async () => {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var held = environment.RunExclusiveAsync(() => release.Task);
                var queued = false;
                (bool HeldDone, bool QueuedRan)? seen = null;
                var ending = environment.EndAsync(() => true, _ => {
                    seen = (release.Task.IsCompleted, queued);
                    return Task.CompletedTask;
                });
                var discarded = false;
                var next = environment.RunExclusiveAsync(() => {
                    queued = true;
                    discarded = AdmissionDiscarded(coordinator);
                    return Task.CompletedTask;
                });
                release.SetResult();
                await Task.WhenAll(held, next);
                return (await ending, seen, discarded);
            });
            Require(ended == EnvironmentEndResult.Ended && seen == (true, false) && discardedForNext,
                "结束应等待进行中的操作，在后续操作前处理结果，且后续操作看到 Admission 已丢弃。");
        });
    }

    // Repeated end clicks share one answer. An accepted exit supersedes an already confirmed, queued end.
    private static void VerifyRepeatedEndRequests(AppLogger logger, string directory)
    {
        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using var session = wts.CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "repeated-end"), (coordinator, _, _) => {
            var environment = CreateEnvironment(logger, session, coordinator);
            var endAsked = 0;
            var exitAsked = 0;
            var (declined, joined, exitDeclined, again) = OnPool(async () => {
                Task<EnvironmentEndResult>? exit = null;
                var joined = 0;
                var first = environment.RequestEnd(() => {
                    endAsked++;
                    // The tray stays clickable while the box is modal: two more ends, then an exit.
                    joined += (environment.RequestEnd(() => ++endAsked > 0) is null ? 1 : 0)
                        + (environment.RequestEnd(() => ++endAsked > 0) is null ? 1 : 0);
                    exit = environment.EndAsync(() => ++exitAsked < 0);
                    return false;
                })!;
                var declined = await first;
                var exitDeclined = await exit!;
                var again = await environment.RequestEnd(() => ++endAsked < 0)!;
                return (declined, joined, exitDeclined, again);
            });
            Require(declined == EnvironmentEndResult.Declined && joined == 2 && endAsked == 2
                && exitDeclined == EnvironmentEndResult.Declined && exitAsked == 1
                && again == EnvironmentEndResult.Declined && wts.LogoffAttempts.Count == 0,
                "确认框打开期间的重复点击应并入该请求，取消一次只问一次；之后的退出与新的点击各问一次。");

            var refused = OnPool(async () => {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var held = environment.RunExclusiveAsync(() => release.Task);
                var queued = environment.RequestEnd(() => ++endAsked > 0)!;
                var joined = environment.RequestEnd(() => ++endAsked > 0) is null;
                var exit = environment.EndAsync(() => ++exitAsked > 0, onAccepted: () => environment.Closing = true);
                release.SetResult();
                await held;
                var refused = false;
                try {
                    await queued;
                } catch (OperationCanceledException) {
                    refused = true;
                }
                Require(await exit == EnvironmentEndResult.Ended, "退出应取得闸门并完成结束。");
                return refused && joined;
            });
            Require(refused && endAsked == 3 && exitAsked == 2 && !environment.Ending && !environment.EndUnconfirmed
                && AdmissionDiscarded(coordinator) && wts.LogoffAttempts.SequenceEqual([7u]),
                "已确认的结束请求应合并排队期间的点击；退出开始后，只有退出可以注销分身。");
        });
    }

    private static void VerifyExitDuringEndConfirmation(AppLogger logger, string directory)
    {
        foreach (var failLogoff in new[] { false, true }) {
            var wts = new SimulatedWts { Sessions = [0, 1, 7], LogoffError = failLogoff ? 5 : null };
            using var session = wts.CreateManager(logger);
            WithFakeWorker(logger, Path.Combine(directory, $"exit-{failLogoff}"), (coordinator, _, _) => {
                var environment = CreateEnvironment(logger, session, coordinator);
                WithRuntimeControlWindow(logger, Path.Combine(directory, $"window-{failLogoff}"), window => {
                    wts.ReportedId = 7;
                    var app = (App)System.Windows.Application.Current;
                    var closed = false;
                    var shutdowns = 0;
                    window.Closed += (_, _) => closed = true;
                    var errors = 0;
                    var questions = 0;
                    bool Decline() { questions++; return false; }
                    void Exit(Func<bool>? confirm)
                    {
                        var exit = app.ExitAsync(confirm, environment, window, reportError: _ => {
                            errors++;
                            if (wts.QueryFails && errors == 1) Exit(Decline);
                        }, shutdown: () => shutdowns++);
                        PumpUntil(() => exit.IsCompleted, "退出请求应在当前确认框返回前完成。");
                        exit.GetAwaiter().GetResult();
                    }

                    wts.QueryFails = true;
                    Exit(Decline);
                    Require(errors == 1 && questions == 0 && !closed && !environment.Closing,
                        "WTS 查询失败的错误框打开期间再次请求退出，应只显示一个错误框。");
                    wts.QueryFails = false;

                    var end = environment.RequestEnd(() => {
                        Exit(() => {
                            questions++;
                            Exit(Decline);
                            Exit(null); // -q goes through the same application exit guard as the tray.
                            Require(closed == !failLogoff && shutdowns == (failLogoff ? 0 : 1) && errors == (failLogoff ? 2 : 1),
                                "用户尚未回答时，自动退出应已关闭主窗口或已报告注销失败。");
                            return true;
                        });
                        return true;
                    })!;
                    PumpUntil(() => end.IsCompleted, "两个确认框都应能返回。");
                    Require(end.GetAwaiter().GetResult() == EnvironmentEndResult.Declined && questions == 1
                        && closed == !failLogoff && shutdowns == (failLogoff ? 0 : 1) && errors == (failLogoff ? 2 : 1)
                        && wts.LogoffTargets.SequenceEqual([(uint?)7]) && wts.LogoffAttempts.SequenceEqual([7u])
                        && environment.Closing == !failLogoff && environment.EndUnconfirmed == failLogoff
                        && AdmissionDiscarded(coordinator) == !failLogoff,
                        "两个旧回答都不能再次结束环境，注销失败时应保留窗口和 Admission。");
                }, session: session, coordinator: coordinator, environment: environment);
            });
        }
    }

    // While the application is closing, operations are refused; the end that closes it still runs.
    private static void VerifyEnvironmentClosing(AppLogger logger, string directory)
    {
        using var session = new SimulatedWts().CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "closing"), (coordinator, _, _) => {
            var environment = CreateEnvironment(logger, session, coordinator);
            environment.Closing = true;
            var ran = false;
            Require(Refused(() => environment.RunExclusiveAsync(() => {
                ran = true;
                return Task.CompletedTask;
            })) && !ran, "退出中不应开始新的运行环境操作。");
            Require(OnPool(() => environment.EndAsync(() => true)) == EnvironmentEndResult.Ended,
                "退出中的结束不受退出中标志阻止。");
        });

        static bool Refused(Func<Task> action)
        {
            try {
                Task.Run(action).WaitAsync(OnPoolLimit).GetAwaiter().GetResult();
                return false;
            } catch (OperationCanceledException) {
                return true;
            }
        }
    }

    private static RuntimeEnvironment CreateEnvironment(AppLogger logger, ChildSessionManager session,
        WorkerCoordinator coordinator, Func<int, CancellationToken, Task>? waitForWorkerExit = null,
        TimeSpan? waitLimit = null) => new(logger, session, coordinator,
        waitForWorkerExit ?? ((_, _) => Task.CompletedTask), waitLimit ?? EnvironmentWaitLimit);

    // The owner a window under test uses; operations run at once unless a test holds them, and the fake Worker,
    // which is this process, counts as exited once the Child Session is logged off.
    private static RuntimeEnvironment WindowEnvironment(AppLogger logger, ChildSessionManager session,
        WorkerCoordinator coordinator, Func<Func<Task>, Task>? runOperation = null) =>
        new(logger, session, coordinator, (_, _) => Task.CompletedTask,
            EnvironmentWaitLimit, runOperation ?? (operation => operation()));

    // Ending before the fake Worker is admitted would discard the Admission it connects with.
    private static void AwaitRunningWorker(WorkerCoordinator coordinator) => AwaitWorker(coordinator,
        snapshot => snapshot is { Fresh: true, WorkerSnapshot.ActiveRun: not null }, "假 Worker 未报告运行中的 Run。");

    private static void AwaitWorker(WorkerCoordinator coordinator, Func<WorkerCoordinatorSnapshot, bool> condition,
        string failure) =>
        Require(SpinWait.SpinUntil(() => condition(coordinator.Snapshot), WaitLimitMilliseconds), failure);

    private static bool AdmissionDiscarded(WorkerCoordinator coordinator) =>
        coordinator.Snapshot.Observation == WorkerObservation.ChildSessionEnded && coordinator.TrackedWorkerPid is null;

    // Runs on the thread pool, away from the dispatcher the self-test blocks; a hang fails instead of stalling.
    private static T OnPool<T>(Func<Task<T>> action) =>
        Task.Run(action).WaitAsync(OnPoolLimit).GetAwaiter().GetResult();

    private static async Task RecordRequestsAsync(ProtocolConnection pipe, List<string> requests,
        CancellationToken cancellationToken, CancellationToken stopListening)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopListening);
        try {
            while (await pipe.ReadAsync(linked.Token) is { } envelope) {
                lock (requests) {
                    requests.Add(envelope.Operation);
                }
            }
        } catch (Exception exception) when (exception is OperationCanceledException or IOException) {
        }
    }
}
