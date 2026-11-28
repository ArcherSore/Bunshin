using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Gui.ChildSession;
using Gui.Models;
using Gui.Views;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    // A 运行记录 outlives the window: reopened from the same application directory, each configuration shows its own
    // record as it was and 继续运行 still starts from it.
    private static void VerifyRunRecordPersistence(AppLogger logger, string directory)
    {
        const PlanItemState succeeded = PlanItemState.Succeeded, failed = PlanItemState.Failed;
        Guid daily = default, other = default;
        string dailyShown = "", otherShown = "";
        WithRuntimeControlWindow(logger, directory, window => {
            var project = ProjectOf(window);
            daily = project.ActiveConfigurationId;
            var run = ResultRun(RunState.Failed, ("Done", succeeded, 18), ("RealTask", failed, 60),
                ("Later", PlanItemState.Cancelled, null));
            ObserveRuns(window, WithSecrets(run with {
                Items = run.Items.Select(item => item with { TaskLabel = $"当时的{item.TaskName}" }).ToArray()
            }));
            dailyShown = ShownRunRecord(window);
            var stored = File.ReadAllText(Path.Combine(directory, "project", "config", "run-records.json"));
            Require(stored.Contains("RealTask") && !stored.Contains("secret", StringComparison.OrdinalIgnoreCase),
                $"运行记录不应保存参数值、pipeline override、前置程序命令、结果和错误详情：{stored}");

            other = project.DuplicateConfiguration(daily);
            ShowConfiguration(window, other);
            // The same name as a task is a different item when it is a pretask.
            var mixed = ResultRun(RunState.Failed, ("RealTask", failed, 5), ("RealTask", succeeded, 12));
            ObserveRuns(window, mixed with {
                Items = [mixed.Items[0] with { Kind = PlanItemKind.Pretask }, mixed.Items[1]]
            });
            otherShown = ShownRunRecord(window);
        });

        var preparations = new List<Guid>();
        var probe = new PresentationProbe();
        WithRuntimeControlWindow(logger, directory, window => {
            var continueRun = Named<System.Windows.Controls.Button>(window, "ContinueRunButton");
            ShowConfiguration(window, other);
            Require(ShownRunRecord(window) == otherShown && !continueRun.IsVisible,
                $"重开后应原样显示记录并按种类匹配起点：{ShownRunRecord(window)}，应为 {otherShown}。");
            ShowConfiguration(window, daily);
            Require(ShownRunRecord(window) == dailyShown,
                $"重开后应原样显示记录：{ShownRunRecord(window)}，应为 {dailyShown}。");
            Require(continueRun.IsVisible && continueRun.IsEnabled
                && Equals(continueRun.ToolTip, "从「当时的RealTask」开始"),
                $"重开后应可继续运行：{continueRun.ToolTip}。");
            Click(window, "ContinueRunButton");
            PumpUntil(() => preparations.Count == 1 && window.CurrentOperation is PendingOperation.None,
                "重开后继续运行应先准备运行环境。");
            Require(preparations[0] == daily && ShownRunRecord(window) == dailyShown, "准备运行环境不应清除记录。");
        }, presentation: probe.Presentation, prepareEnvironment: (project, _, _) => {
            preparations.Add(project.ActiveConfigurationId);
            throw new InvalidOperationException("模拟准备失败");
        });
    }

    // Everything a Run snapshot carries besides names, states and times.
    private static RunSnapshot WithSecrets(RunSnapshot run)
    {
        JsonElement Secret(string where) => ProtocolJson.ToElement(new Dictionary<string, string> {
            [$"secret-{where}-key"] = $"secret-{where}"
        });
        var error = new StructuredReason("secret-code", "secret-message", Secret("details"));
        return run with {
            PlanDigest = "secret-digest", Result = Secret("result"), Error = error,
            Plan = run.Plan with {
                ResolvedGlobalOptions = Secret("global"),
                Items = run.Plan.Items.Select(item => item with {
                    Entry = "secret-entry", ResolvedOptions = Secret("resolved"), PipelineOverride = Secret("override"),
                    Program = new PretaskCommand("secret.exe", ["--secret-argument"])
                }).ToArray()
            },
            Items = run.Items.Select(item => item with {
                Entry = "secret-entry", ResolvedOptions = Secret("resolved"), PipelineOverride = Secret("override"),
                Reason = "secret-reason", Result = Secret("result"), Error = error
            }).ToArray()
        };
    }

    // A record file that cannot be read or holds something else is ignored and logged, and one that cannot be written
    // is logged while the window still shows the result; neither stops anything.
    private static void VerifyRunRecordFileFailures(AppLogger logger, string directory)
    {
        var config = Path.Combine(directory, "project", "config");
        var records = Path.Combine(config, "run-records.json");
        var warnings = 0;
        void Count(object? sender, LogEntry entry)
        {
            if (entry.Level == LogLevel.Warn && entry.Message.Contains("运行记录")) {
                Interlocked.Increment(ref warnings);
            }
        }
        RunSnapshot Succeeded(string name) => ResultRun(RunState.Succeeded, (name, PlanItemState.Succeeded, 5));
        // Opens the window on a file it must ignore, then shows that a later Run is recorded again.
        void RequireIgnored(string problem, FileShare? held = null)
        {
            warnings = 0;
            var file = held is { } share ? File.Open(records, FileMode.Open, FileAccess.Read, share) : null;
            try {
                WithRuntimeControlWindow(logger, directory, window => {
                    Require(ShownRunRecord(window).Length == 0 && warnings > 0, $"{problem}应忽略并写日志。");
                    file?.Dispose();
                    ObserveRuns(window, Succeeded("After"));
                    Require(ShownRunRecord(window).Contains("After"), $"{problem}之后新的结果应照常显示。");
                });
            } finally {
                file?.Dispose();
            }
            WithRuntimeControlWindow(logger, directory, window =>
                Require(ShownRunRecord(window).Contains("After"), $"{problem}之后新的结果应照常保存。"));
        }

        logger.EntryWritten += Count;
        try {
            WithRuntimeControlWindow(logger, directory, window => ObserveRuns(window, Succeeded("Before")));
            var valid = File.ReadAllText(records);
            foreach (var (problem, content) in new[] {
                ("记录文件不是 JSON ", "{ broken"),
                ("记录文件版本不认识", valid.Replace("\"Version\":1", "\"Version\":2")),
                ("记录文件缺少内容", "{\"Version\":1}"),
                ("记录文件含空项", valid.Replace("\"Items\":[", "\"Items\":[null,"))
            }) {
                Require(content != valid, $"测试前提：{problem}应改变了文件。");
                File.WriteAllText(records, content);
                RequireIgnored(problem);
            }
            File.WriteAllText(records, valid);
            RequireIgnored("记录文件无法读取", FileShare.None);

            // A directory in the file's place makes every write fail.
            File.Delete(records);
            Directory.CreateDirectory(records);
            warnings = 0;
            WithRuntimeControlWindow(logger, directory, window => {
                ObserveRuns(window, Succeeded("Unsaved"));
                Require(ShownRunRecord(window).Contains("Unsaved") && warnings > 0,
                    "记录无法写入时应写日志，本次进程内仍显示结果。");
                Require(Directory.GetFiles(config, "run-records.json*").Length == 0, "写入失败不应留下临时文件。");
            });
        } finally {
            logger.EntryWritten -= Count;
        }
    }

    // Which Run may still write a record survives a reopening: a start that was never admitted keeps the old record,
    // an admitted Run retires it and every earlier Run, and a deleted configuration leaves nothing behind.
    private static void VerifyRunRecordOwnershipAfterReopen(AppLogger logger, string directory)
    {
        Guid daily = default, other = default;
        var records = Path.Combine(directory, "project", "config", "run-records.json");
        RunSnapshot Failed(string name) => ResultRun(RunState.Failed, (name, PlanItemState.Failed, 5));
        RunSnapshot kept = Failed("Kept"), earlier = Failed("Earlier"), later = Failed("Later");
        RunSnapshot others = Failed("Others"), pending = Failed("Pending");
        WithRuntimeControlWindow(logger, directory, window => {
            var project = ProjectOf(window);
            daily = project.ActiveConfigurationId;
            ObserveRuns(window, kept);
            ClaimRun(window, earlier.RunId);
            ClaimRun(window, later.RunId);
            other = project.DuplicateConfiguration(daily);
            ShowConfiguration(window, other);
            ObserveRuns(window, others);
            ClaimRun(window, pending.RunId);
        });
        WithRuntimeControlWindow(logger, directory, window => {
            ShowConfiguration(window, daily);
            Require(ShownRunRecord(window).Contains("Kept"), "启动没有被接纳时，重开后旧记录应保留。");
            ObserveRuns(window, later with { State = RunState.Running, EndedAtUtc = null }, claim: false);
            ObserveRuns(window, null);
            Require(ShownRunRecord(window).Length == 0, "重开后接纳新的 Run，旧记录应作废。");
        });
        WithRuntimeControlWindow(logger, directory, window => {
            ShowConfiguration(window, daily);
            ObserveRuns(window, null);
            Require(ShownRunRecord(window).Length == 0, "作废的旧记录重开后不应恢复。");
            ObserveRuns(window, earlier, claim: false);
            Require(ShownRunRecord(window).Length == 0, "重开后，更早 Run 的迟到快照不得复活记录。");
            ObserveRuns(window, later, claim: false);
            ObserveRuns(window, earlier, claim: false);
            Require(ShownRunRecord(window).Contains("Later"), "重开后，更早 Run 的迟到快照不得覆盖记录。");

            ShowConfiguration(window, other);
            Require(ShownRunRecord(window).Contains("Others") && File.ReadAllText(records).Contains(other.ToString())
                && File.ReadAllText(records).Contains(pending.RunId.ToString()),
                "删除前应保存该配置的记录和对应关系。");
            ShowConfiguration(window, daily);
            window.TaskPlanEditor.DeleteConfiguration(other);
            Require(ProjectOf(window).Configurations.All(item => item.Id != other), "测试前提：配置应已删除。");
        });
        WithRuntimeControlWindow(logger, directory, window => {
            var stored = File.ReadAllText(records);
            Require(!stored.Contains(other.ToString()) && !stored.Contains(pending.RunId.ToString())
                && !stored.Contains(others.RunId.ToString()), $"删除配置后不应再保存它的记录和对应关系：{stored}");
            ObserveRuns(window, pending, claim: false);
            Require(ShownRunRecord(window).Contains("Later"), "删除的配置的 Run 结束后不应记到其他配置。");
        });
    }

    // The claim is on disk before the start request arrives, so a window reopened while the Run is still going
    // records its result under the configuration that started it; a Run nobody claimed is not recorded.
    private static void VerifyRunRecordReconnect(AppLogger logger, string directory)
    {
        var windowDirectory = Path.Combine(directory, "window");
        var fixture = CreateProjectFixture(windowDirectory);
        var digest = ProjectPlanModule.Open(fixture, Path.Combine(fixture, "plan.json")).RuntimeProfileDigest;
        var records = Path.Combine(fixture, "config", "run-records.json");
        Guid daily = default, other = default;
        RunSnapshot? ended = null;
        var storedOnArrival = "";
        WithFakeWorker(logger, Path.Combine(directory, "worker"), (coordinator, record, pipeName) => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = Task.Run(async () => {
                await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(
                    pipeName, record, 0, timeout.Token);
                var current = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0) with { StateRevision = 1 };
                var request = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                    pipe, ProtocolOperations.RunStart, timeout.Token, current);
                storedOnArrival = File.Exists(records) ? File.ReadAllText(records) : "";
                ended = SucceededRun(ProtocolJson.Deserialize<RunStartRequest>(request.Data));
                await pipe.WriteAsync(WireEnvelope.Response(request.Operation, request.RequestId!.Value,
                    new RunStartResponse("accepted")), timeout.Token);
                await pipe.WriteAsync(StateEvent(current with {
                    StateRevision = 2, RunState = RunState.Running, ActiveRun = StillRunning(ended)
                }), timeout.Token);
                await closed.Task.WaitAsync(timeout.Token);
            });
            using var session = new SimulatedWts().CreateManager(logger);
            try {
                WithRuntimeControlWindow(logger, windowDirectory, window => {
                    var project = ProjectOf(window);
                    daily = project.ActiveConfigurationId;
                    other = project.DuplicateConfiguration(daily);
                    ShowConfiguration(window, daily);
                    ShowConnectedDesktop(window, session, record.ChildSessionId);
                    var start = Named<System.Windows.Controls.Button>(window, "StartPlanButton");
                    PumpUntil(() => start.IsVisible && start.IsEnabled, "Worker 就绪后应可运行。");
                    Click(window, "StartPlanButton");
                    PumpUntil(() => ShownRunRecord(window).StartsWith("本次运行", StringComparison.Ordinal),
                        "接纳后应显示本次运行。");
                }, session: session, coordinator: coordinator);
            } finally {
                closed.TrySetResult();
                worker.GetAwaiter().GetResult();
            }
        }, runtimeProfileDigest: digest);
        Require(storedOnArrival.Contains(ended!.RunId.ToString()) && storedOnArrival.Contains(daily.ToString()),
            $"启动请求发出前应已保存 Run 与配置的对应关系：{storedOnArrival}");

        // The reopened window reconnects while the Run is still going.
        WithFakeWorker(logger, Path.Combine(directory, "reconnected"), (coordinator, record, pipeName) => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var unclaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = Task.Run(async () => {
                await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(
                    pipeName, record, 0, timeout.Token, activeRun: StillRunning(ended));
                var current = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
                await finish.Task.WaitAsync(timeout.Token);
                await pipe.WriteAsync(StateEvent(current with { StateRevision = 2, LastRun = ended }), timeout.Token);
                await unclaimed.Task.WaitAsync(timeout.Token);
                await pipe.WriteAsync(StateEvent(current with {
                    StateRevision = 3, LastRun = ResultRun(RunState.Failed, ("Stranger", PlanItemState.Failed, 5))
                }), timeout.Token);
                await closed.Task.WaitAsync(timeout.Token);
            });
            using var session = new SimulatedWts().CreateManager(logger);
            try {
                WithRuntimeControlWindow(logger, windowDirectory, window => {
                    ShowConfiguration(window, other);
                    ShowConnectedDesktop(window, session, record.ChildSessionId);
                    PumpUntil(() => ShownRunRecord(window).StartsWith("本次运行", StringComparison.Ordinal),
                        "重连后应显示仍在运行的 Run。");
                    finish.SetResult();
                    PumpUntil(() => ShownRunRecord(window).Length == 0, "结果不应记到当前查看的其他配置。");
                    ShowConfiguration(window, daily);
                    var shown = ShownRunRecord(window);
                    Require(shown.StartsWith("上次运行", StringComparison.Ordinal) && shown.Contains("1 / 1 项成功"),
                        $"重开并重连后，结果应记到启动它的配置：{shown}。");
                    unclaimed.SetResult();
                    PumpUntil(() => ObservedWorker(window)?.StateRevision == 3, "窗口应收到无对应关系的 Run。");
                    Require(ShownRunRecord(window) == shown, "没有对应关系的 Run 不应记录到当前配置。");
                    ShowConfiguration(window, other);
                    Require(ShownRunRecord(window).Length == 0, "没有对应关系的 Run 不应记录。");
                }, session: session, coordinator: coordinator);
            } finally {
                closed.TrySetResult();
                worker.GetAwaiter().GetResult();
            }
        }, runtimeProfileDigest: digest);

        static RunSnapshot StillRunning(RunSnapshot ended) => WorkerCoordinatorSelfTest.CreateActiveRun(
            new(ended.RunId, ended.Plan, ended.PlanDigest));
    }

    private static WorkerSnapshot? ObservedWorker(MainWindow window) =>
        ((WorkerCoordinatorSnapshot)typeof(MainWindow)
            .GetField("_workerSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!)
        .WorkerSnapshot;

    // Connected only once the window has started, so its startup never tries to restore a real desktop.
    private static void ShowConnectedDesktop(MainWindow window, ChildSessionManager session, uint childSessionId)
    {
        ConnectTestDesktop(session, childSessionId);
        SetOnboardingField(window, "_sessionSnapshot", session.Snapshot);
        InvokeOnboarding(window, "RefreshRuntimeControls");
    }

    // The end time names its day by the window's clock, and follows that clock past midnight while it stays on screen.
    private static void VerifyRunResultDate(AppLogger logger, string directory)
    {
        var clock = new ScheduleTestClock(); // 2026-11-16 07:59:42 in its time zone.
        WithRuntimeControlWindow(logger, directory, window => {
            var time = Named<TextBlock>(window, "RunResultTime");
            void Observe(DateTime endedLocal)
            {
                var ended = TimeZoneInfo.ConvertTimeToUtc(endedLocal, clock.LocalTimeZone);
                ObserveRuns(window, ResultRun(RunState.Succeeded, ("RealTask", PlanItemState.Succeeded, 78)) with {
                    StartedAtUtc = ended.AddSeconds(-78), EndedAtUtc = ended
                });
            }
            foreach (var (ended, expected) in new[] {
                (new DateTime(2026, 11, 16, 0, 0, 0), "00:00"),
                (new DateTime(2026, 11, 15, 23, 59, 0), "昨天 23:59"),
                (new DateTime(2026, 11, 15, 0, 0, 0), "昨天 00:00"),
                (new DateTime(2026, 11, 14, 23, 59, 0), "11-14 23:59"),
                (new DateTime(2026, 11, 16, 7, 58, 0), "07:58")
            }) {
                Observe(ended);
                Require(time.Text == $"{expected} · 用时 1:18", $"{ended:MM-dd HH:mm} 结束应显示 {expected}：{time.Text}。");
            }
            clock.Advance(TimeSpan.FromHours(17)); // 11-17 00:59:42.
            PumpUntil(() => time.Text == "昨天 07:58 · 用时 1:18", "面板开着跨过午夜后应改为昨天。");
            clock.Advance(TimeSpan.FromDays(1));
            PumpUntil(() => time.Text == "11-16 07:58 · 用时 1:18", "再过一天后应显示日期。");
        }, scheduleClock: clock, scheduleTimer: new() { Interval = TimeSpan.FromMilliseconds(20) });
    }

    private static void ShowConfiguration(MainWindow window, Guid id)
    {
        ProjectOf(window).ActivateConfiguration(id);
        InvokeEditor(window, "RenderTaskPlan");
        InvokeOnboarding(window, "RefreshRuntimeControls");
        window.UpdateLayout();
    }

    // Everything the result section says, with every item listed; empty while the section is hidden.
    private static string ShownRunRecord(MainWindow window)
    {
        var section = Named<FrameworkElement>(window, "RunResultSection");
        if (!section.IsVisible) {
            return "";
        }
        if (!Named<ScrollViewer>(window, "RunResultAllScroll").IsVisible) {
            ClickOnboarding(window, "RunResultAllToggle");
        }
        window.UpdateLayout();
        return string.Join("|", SettingsVisualDescendants(section).OfType<TextBlock>()
            .Where(text => text.IsVisible && text.Text.Any(char.IsLetterOrDigit)).Select(text => text.Text));
    }
}
