using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Point = System.Windows.Point;
using Gui.ChildSession;
using Gui.Themes;
using ProjectModel;
using Protocol;
using Gui.Views;
using Gui.Worker;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    // Each Run state maps to the item states and counts the console shows; failures never count as success and
    // stopped or skipped items never count as processed.
    private static void VerifyRunProgressScenarios()
    {
        var empty = JsonDocument.Parse("{}").RootElement.Clone();
        var start = new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);
        RunSnapshot Run(RunState state, params (PlanItemState State, bool Started)[] items)
        {
            var planItems = items.Select((_, index) => new RunPlanItem(
                Guid.NewGuid(), $"Task{index}", $"任务 {index + 1}", "Entry", empty, empty)).ToArray();
            var plan = new RunPlan(1, start, new ProjectProvenance("self-test", "1", 1, "self-test"), "self-test",
                empty, planItems);
            var snapshots = planItems.Select((item, index) => new PlanItemSnapshot(item.PlanItemId, item.TaskName,
                item.TaskLabel, item.Entry, empty, empty, items[index].State,
                items[index].Started ? start.AddMinutes(index) : null, null, null, null, null)).ToArray();
            var current = Array.FindIndex(snapshots, item =>
                item.State is PlanItemState.Starting or PlanItemState.Running);
            return new RunSnapshot(Guid.NewGuid(), "digest", state, start, start, null, null,
                current < 0 ? null : snapshots[current].PlanItemId, current < 0 ? null : current, plan,
                snapshots, null, null);
        }
        var record = new WorkerAdmissionRecord(Guid.NewGuid(), "token", 1, Environment.ProcessId, "self-test",
            DateTime.UtcNow);
        var idle = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
        WorkerSnapshot Active(RunSnapshot run) => idle with { RunState = run.State, ActiveRun = run };
        WorkerSnapshot Last(RunSnapshot run) => idle with { LastRun = run };
        const PlanItemState pending = PlanItemState.Pending, running = PlanItemState.Running;
        const PlanItemState succeeded = PlanItemState.Succeeded, failed = PlanItemState.Failed;
        const PlanItemState cancelled = PlanItemState.Cancelled;
        (PlanItemKind, string)[] MaaTasks(params string[] names) =>
            names.Select(name => (PlanItemKind.MaaTask, name)).ToArray();
        var plan = MaaTasks("Task0", "Task1", "Task2");

        foreach (var (name, worker, tasks, states, current) in new (string, WorkerSnapshot,
            (PlanItemKind, string)[], string, int?)[] {
            ("运行中首项", Active(Run(RunState.Running, (PlanItemState.Starting, true), (pending, false),
                (pending, false))), plan, "Running,Pending,Pending", 0),
            ("运行中中间项", Active(Run(RunState.Running, (succeeded, true), (running, true), (pending, false))),
                plan, "Succeeded,Running,Pending", 1),
            ("失败后", Active(Run(RunState.Running, (failed, true), (cancelled, false), (cancelled, false))),
                plan, "Failed,NotRun,NotRun", null),
            ("用户停止", Last(Run(RunState.Cancelled, (succeeded, true), (cancelled, true), (cancelled, false))),
                plan, "Succeeded,Stopped,NotRun", null),
            ("首项启动前停止", Last(Run(RunState.Cancelled, (cancelled, false), (cancelled, false), (cancelled, false))),
                plan, "NotRun,NotRun,NotRun", null),
            ("因失败中止", Last(Run(RunState.Failed, (succeeded, true), (failed, true), (cancelled, false))),
                plan, "Succeeded,Failed,NotRun", null),
            ("全部成功", Last(Run(RunState.Succeeded, (succeeded, true), (succeeded, true), (succeeded, true))),
                plan, "Succeeded,Succeeded,Succeeded", null)
        }) {
            var progress = RunProgress.Derive(worker, tasks);
            Require(progress is not null && string.Join(",", progress.Items.Select(item => item.State)) == states
                && progress.CurrentIndex == current
                && progress.Processed == progress.Items.Count(item => item.State is PlanItemProgress.Succeeded
                    or PlanItemProgress.Failed)
                && progress.Succeeded == progress.Items.Count(item => item.State == PlanItemProgress.Succeeded),
                $"Run 进度场景“{name}”不符。");
        }

        var stopped = RunProgress.Derive(Last(Run(RunState.Cancelled, (succeeded, true), (cancelled, true),
            (cancelled, false))), plan)!;
        Require(stopped.Processed == 1 && stopped.Stopped == 1 && stopped.NotRun == 1, "已停止和未执行不计入已处理。");
        Require(RunProgress.Derive(Last(Run(RunState.Succeeded, (succeeded, true), (succeeded, true),
                (succeeded, true))), MaaTasks("Task0", "Task2")) is null && RunProgress.Derive(idle, plan) is null,
            "Last Run 计划与当前配置的任务序列不一致或没有 Run 时不显示进度。");

        // A continued Run covers the end of the plan; 继续运行 starts at its first unsuccessful item, never at the
        // plan's first item, where it would be the same as 运行.
        RunSnapshot Tail(RunState state, params (string Name, PlanItemState State, bool Started)[] items)
        {
            var run = Run(state, items.Select(item => (item.State, item.Started)).ToArray());
            return run with {
                Plan = run.Plan with {
                    Items = run.Plan.Items.Select((item, index) => item with { TaskName = items[index].Name }).ToArray()
                },
                Items = run.Items.Select((item, index) => item with { TaskName = items[index].Name }).ToArray()
            };
        }
        foreach (var (name, worker, from) in new (string, WorkerSnapshot, int?)[] {
            ("中间项失败", Last(Run(RunState.Failed, (succeeded, true), (failed, true), (cancelled, false))), 1),
            ("两项之间停止", Last(Run(RunState.Cancelled, (succeeded, true), (cancelled, false), (cancelled, false))), 1),
            ("继续运行后首项再失败", Last(Tail(RunState.Failed, ("Task1", failed, true), ("Task2", cancelled, false))), 1),
            ("继续运行后停在末项", Last(Tail(RunState.Cancelled, ("Task1", succeeded, true), ("Task2", cancelled, true))), 2),
            ("整份计划首项失败", Last(Run(RunState.Failed, (failed, true), (cancelled, false), (cancelled, false))), null),
            ("全部成功", Last(Run(RunState.Succeeded, (succeeded, true), (succeeded, true), (succeeded, true))), null),
            ("各项成功但整次停止", Last(Run(RunState.Cancelled, (succeeded, true), (succeeded, true), (succeeded, true))),
                null),
            ("运行中", Active(Run(RunState.Running, (succeeded, true), (failed, true), (running, true))), null)
        }) {
            var progress = RunProgress.Derive(worker, plan);
            Require(progress is not null && RunProgress.ContinueFrom(progress) == from,
                $"继续运行场景“{name}”的起点应为 {from?.ToString() ?? "无"}，实际为 "
                + $"{(progress is null ? "不显示记录" : RunProgress.ContinueFrom(progress)?.ToString() ?? "无")}。");
        }
        var continued = Last(Tail(RunState.Failed, ("Task1", failed, true), ("Task2", cancelled, false)));
        Require(RunProgress.Derive(continued, plan) is { Total: 2 }
            && RunProgress.Derive(continued, MaaTasks("Task1", "Task2")) is not null
            && RunProgress.Derive(continued, MaaTasks("Task1", "Task2", "Task3")) is null
            && RunProgress.Derive(continued, MaaTasks("Task0", "Task2")) is null
            && RunProgress.Derive(continued, MaaTasks("Task2", "Task1")) is null
            && RunProgress.Derive(continued, MaaTasks("Task2")) is null
            && RunProgress.Derive(continued, [(PlanItemKind.Pretask, "Task0"), .. MaaTasks("Task1", "Task2")]) is not null
            && RunProgress.Derive(continued, [(PlanItemKind.Pretask, "Task1"), (PlanItemKind.MaaTask, "Task2")]) is null,
            "结束后的记录只在等于当前计划末尾一段时显示。");
        Require(RunProgress.ContinueFrom(RunProgress.Derive(continued, MaaTasks("Task1", "Task2"))!) is null,
            "记录覆盖整份当前计划且首项失败时，继续运行与运行相同，不应提供。");
        Require(RunProgress.FormatDuration(TimeSpan.FromSeconds(65.9)) == "1:05"
            && RunProgress.FormatDuration(TimeSpan.FromMinutes(61)) == "1:01:00", "用时格式不符。");
    }

    // A Run as the Worker would report it. A finished item took the given seconds; a running one started that long ago.
    private static RunSnapshot ResultRun(RunState state, params (string Name, PlanItemState State, int? Seconds)[] items)
    {
        var empty = ProtocolJson.ToElement(new { });
        var now = DateTime.UtcNow;
        var active = state is RunState.Starting or RunState.Running or RunState.Stopping;
        var started = now.AddSeconds(-78);
        var planItems = items.Select(item => new RunPlanItem(
            Guid.NewGuid(), item.Name, item.Name, "Entry", empty, empty)).ToArray();
        var plan = new RunPlan(1, started, new ProjectProvenance("self-test", "1", 1, "self-test"), "self-test",
            empty, planItems);
        var snapshots = planItems.Select((item, index) => {
            var (_, itemState, seconds) = items[index];
            var running = itemState is PlanItemState.Starting or PlanItemState.Running;
            DateTime? start = seconds is { } span ? running ? now.AddSeconds(-span) : started : null;
            DateTime? end = running || itemState == PlanItemState.Pending ? null
                : start?.AddSeconds(seconds!.Value) ?? now;
            return new PlanItemSnapshot(item.PlanItemId, item.TaskName, item.TaskLabel, item.Entry, empty, empty,
                itemState, start, end, null, null, null);
        }).ToArray();
        var current = Array.FindIndex(snapshots, item =>
            item.State is PlanItemState.Starting or PlanItemState.Running);
        return new RunSnapshot(Guid.NewGuid(), "digest", state, started, started, null, active ? null : now,
            current < 0 ? null : snapshots[current].PlanItemId, current < 0 ? null : current, plan, snapshots,
            null, null);
    }

    // Shows the window one ready environment: an unfinished Run is the active one, a finished one the Last Run.
    private static void ObserveRuns(MainWindow window, RunSnapshot? run)
    {
        var active = run is { EndedAtUtc: null };
        var idle = ScenarioWorker(ProjectOf(window).RuntimeProfileDigest);
        SetOnboardingField(window, "_sessionSnapshot",
            new ChildSessionSnapshot(ChildSessionState.Connected, ScenarioSessionId, 1, "fixture"));
        SetOnboardingField(window, "_workerSnapshot", new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true,
            idle with {
                RunState = active ? run!.State : RunState.Idle, ActiveRun = active ? run : null,
                LastRun = active ? null : run
            }, ""));
        InvokeOnboarding(window, "RefreshRuntimeControls");
        window.UpdateLayout();
    }

    // The result section says how far the Run got, with one segment per item, one line per outcome and every item
    // on demand. It gives way when there is no Run to describe or the environment needs recovery.
    private static void VerifyRunResultSection(AppLogger logger, string testDirectory)
    {
        string[] names = [
            "启动QQ大厅", "进入游戏", "丰饶之间", "通缉任务", "游戏托管", "生存演习", "竞技场", "押镖", "组织通灵兽",
            "杂货铺", "试试手气", "积分赛", "忍界远征", "领取奖励"
        ];
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "run-result"), (window, _) => {
            EndTour(window);
            var project = ProjectOf(window);
            foreach (var item in project.SelectedItems.ToArray()) {
                project.RemoveItem(item.Kind, item.Name);
            }
            foreach (var name in names.Take(4)) {
                project.AddItem(PlanItemKind.MaaTask, name);
            }
            InvokeEditor(window, "RenderTaskPlan");
            window.Width = 1180;
            window.Height = 760;
            var section = Named<FrameworkElement>(window, "RunResultSection");
            var toggle = Named<System.Windows.Controls.Button>(window, "RunResultSectionToggle");
            var count = Named<TextBlock>(window, "RunResultCount");
            var time = Named<TextBlock>(window, "RunResultTime");
            var meter = Named<ItemsControl>(window, "RunResultMeter");
            var outcomes = Named<ItemsControl>(window, "RunResultOutcomes");
            var all = Named<ItemsControl>(window, "RunResultAllItems");
            var scroll = Named<ScrollViewer>(window, "RunResultAllScroll");
            const PlanItemState pending = PlanItemState.Pending, running = PlanItemState.Running;
            const PlanItemState succeeded = PlanItemState.Succeeded, failed = PlanItemState.Failed;
            const PlanItemState cancelled = PlanItemState.Cancelled;

            ObserveRuns(window, null);
            Require(!section.IsVisible, "没有 Run 时应隐藏整个运行结果分区。");

            ObserveRuns(window, ResultRun(RunState.Running, (names[0], succeeded, 18), (names[1], running, 44),
                (names[2], pending, null), (names[3], pending, null)));
            var panel = Named<FrameworkElement>(window, "RuntimeSidebar");
            double Top(string name) => Named<FrameworkElement>(window, name).TransformToAncestor(panel)
                .Transform(new Point()).Y;
            Require(section.IsVisible && Top("DesktopSection") < Top("RunResultSection")
                && Top("RunResultSection") < Top("ScheduleSection"), "运行结果分区默认应位于桌面分身下方。");
            var continueRun = Named<System.Windows.Controls.Button>(window, "ContinueRunButton");
            Require(!continueRun.IsVisible, "运行中不提供继续运行。");
            Require((string)toggle.Content == "本次运行" && count.Text == "1 / 4 项完成"
                && time.Text.StartsWith("总用时 1:", StringComparison.Ordinal), "运行中应显示完成数和总用时。");
            RequireMeter("Brush.Success", "Brush.Primary", "Brush.Progress.Track", "Brush.Progress.Track");
            RequireLines(outcomes, "进行中|进入游戏", "成功|1", "待执行|2");
            Require(!scroll.IsVisible && Named<TextBlock>(window, "RunResultAllCount").Text == "4",
                "全部任务默认收起，只显示总数。");
            Capture("running");
            ClickOnboarding(window, "RunResultAllToggle");
            var lines = Lines(all);
            Require(scroll.IsVisible && lines.Length == 4 && lines[0] == "启动QQ大厅|0:18"
                && lines[1].StartsWith("进入游戏|进行中 · 0:4", StringComparison.Ordinal)
                && lines[2] == "丰饶之间|待执行" && lines[3] == "通缉任务|待执行", $"逐项状态不符：{string.Join("；", lines)}");
            RequireRowColor(1, "Brush.Text.Secondary", "Brush.Primary.Foreground");
            RequireRowColor(2, "Brush.Text.Disabled", "Brush.Text.Disabled");
            var row = all.ItemContainerGenerator.ContainerFromIndex(1);
            var total = time.Text;
            PumpUntil(() => Lines(all)[1] != lines[1] && time.Text != total, "进行中的用时应每秒更新。");
            Require(ReferenceEquals(row, all.ItemContainerGenerator.ContainerFromIndex(1)), "计时只更新文字，不重建列表。");
            ClickOnboarding(window, "RunResultAllToggle");
            Require(!scroll.IsVisible, "再次点击应收起全部任务。");

            var failure = ResultRun(RunState.Failed, (names[0], succeeded, 18), (names[1], failed, 60),
                (names[2], cancelled, null), (names[3], cancelled, null));
            ObserveRuns(window, failure);
            Require((string)toggle.Content == "上次运行" && AutomationProperties.GetName(toggle) == "收起上次运行"
                && count.Text == "1 / 4 项成功"
                && time.Text == $"{failure.EndedAtUtc!.Value.ToLocalTime():HH:mm} · 用时 1:18",
                $"结束后应显示成功数、结束时间和用时：{count.Text}，{time.Text}。");
            RequireMeter("Brush.Success", "Brush.Error", "Brush.Progress.Track", "Brush.Progress.Track");
            RequireLines(outcomes, "失败|进入游戏", "成功|1", "未执行|2");
            Require(ShownHeaderControls(window).Length == 0
                && !Named<FrameworkElement>(window, "RuntimeStatusDetail").IsVisible
                && Named<System.Windows.Controls.Button>(window, "StartPlanButton") is { IsVisible: true, IsEnabled: true },
                "运行失败后面板不提供重试开始任务，由“运行”重新开始。");
            Require(continueRun.IsVisible && continueRun.IsEnabled
                && Math.Abs(Top("RunResultSectionToggle") + toggle.ActualHeight / 2
                    - Top("ContinueRunButton") - continueRun.ActualHeight / 2) <= PixelTolerance
                && continueRun.TransformToAncestor(panel).Transform(new Point(continueRun.ActualWidth, 0)).X
                    > section.ActualWidth / 2, "中间项失败后，分区标题行右侧应提供继续运行。");
            Capture("failed");
            ClickOnboarding(window, "RunResultAllToggle");
            RequireLines(all, "启动QQ大厅|0:18", "进入游戏|失败 · 1:00", "丰饶之间|未执行", "通缉任务|未执行");
            RequireRowColor(1, "Brush.Text.Secondary", "Brush.Error.Foreground");
            RequireRowColor(3, "Brush.Text.Disabled", "Brush.Text.Disabled");
            var sizes = Texts(Named<FrameworkElement>(window, "RunResultSectionBody"))
                .Where(text => text.Text.Any(char.IsLetterOrDigit)) // Leaves out icon glyphs.
                .Select(text => $"{text.Text}={text.FontSize}").ToArray();
            Require(sizes.All(size => size.EndsWith("=14", StringComparison.Ordinal)),
                $"运行结果分区内的文字应同一字号：{string.Join("；", sizes)}。");
            Capture("failed-expanded");

            // Recovery owns the panel: the result gives way and 桌面分身 explains the fault in plain rows.
            var desktop = new DesktopToggle(DesktopAction.Show, true, DesktopStatus.Background);
            var recoveryTitle = Named<TextBlock>(window, "RecoveryTitleText");
            window.ApplyRuntimeControls(new(new RuntimeAction.Recover(true, RecoveryReason.WorkerLost), false,
                LockReason.RuntimeBusy, desktop, true));
            window.UpdateLayout();
            var detail = Named<TextBlock>(window, "RuntimeStatusDetail");
            var recover = Named<FrameworkElement>(window, "RecoverEnvironmentButton");
            Require(!section.IsVisible && recoveryTitle.IsVisible && recoveryTitle.Text.Length > 0
                && detail.IsVisible && detail.Text.Length > 0 && recover.IsVisible
                && Top("RecoveryTitleText") < Top("RuntimeStatusDetail")
                && Top("RuntimeStatusDetail") < Top("RecoverEnvironmentButton"),
                "故障时应隐藏运行结果，并在桌面分身分区依次显示标题、说明和结束按钮。");
            RequireThemeBrush(recoveryTitle.Foreground, System.Windows.Application.Current.Resources,
                "Brush.Error.Foreground", "故障标题");
            Capture("recovery");

            ObserveRuns(window, ResultRun(RunState.Cancelled, (names[0], succeeded, 18), (names[1], cancelled, 33),
                (names[2], cancelled, null), (names[3], cancelled, null)));
            Require(!recoveryTitle.IsVisible && section.IsVisible && count.Text == "1 / 4 项成功", "离开故障后应恢复显示。");
            ClickOnboarding(window, "RunResultAllToggle");
            RequireMeter("Brush.Success", "Brush.Warning", "Brush.Progress.Track", "Brush.Progress.Track");
            RequireLines(outcomes, "已停止|进入游戏", "成功|1", "未执行|2");
            Require(continueRun.IsVisible && continueRun.IsEnabled, "停止后应可继续运行。");
            Capture("stopped");

            ObserveRuns(window, ResultRun(RunState.Succeeded, (names[0], succeeded, 18), (names[1], succeeded, 60),
                (names[2], succeeded, 42), (names[3], succeeded, 191)));
            Require(count.Text == "4 / 4 项成功", "全部成功时应显示成功数。");
            RequireMeter("Brush.Success", "Brush.Success", "Brush.Success", "Brush.Success");
            RequireLines(outcomes, "成功|4");
            Require(!continueRun.IsVisible, "全部成功时不提供继续运行。");
            Capture("success");

            ObserveRuns(window, ResultRun(RunState.Failed, (names[0], failed, 18), (names[1], cancelled, null),
                (names[2], cancelled, null), (names[3], cancelled, null)));
            Require(section.IsVisible && !continueRun.IsVisible, "整份计划首项失败时，继续运行与运行相同，不应提供。");

            // A continued Run covers the end of the plan and stays shown; failing again offers to continue again.
            var continued = ResultRun(RunState.Failed, (names[1], failed, 20), (names[2], cancelled, null),
                (names[3], cancelled, null));
            ObserveRuns(window, continued);
            Require(section.IsVisible && count.Text == "0 / 3 项成功" && continueRun.IsVisible,
                "继续运行的记录等于计划末尾一段时应显示，首项再次失败仍可继续。");
            var startButton = Named<System.Windows.Controls.Button>(window, "StartPlanButton");
            foreach (var blocked in new RuntimeControlState[] {
                new(new RuntimeAction.Start(false, StartBlocker.ConfigurationInvalid), true, null, desktop, true),
                new(new RuntimeAction.Prepare(false), true, null,
                    desktop with { Enabled = false, Status = DesktopStatus.NotRunning }, false)
            }) {
                window.ApplyRuntimeControls(blocked);
                window.UpdateLayout();
                Require(continueRun.IsVisible && !continueRun.IsEnabled && !startButton.IsEnabled
                    && continueRun.ToolTip is string reason && reason.Length > 0 && Equals(reason, startButton.ToolTip),
                    $"{blocked.Action} 时继续运行应与运行一样不可用，并给出相同原因。");
            }
            var originalConfiguration = project.ActiveConfigurationId;
            foreach (var (change, undo) in new (Action, Action)[] {
                (() => project.RemoveItem(PlanItemKind.MaaTask, names[3]),
                    () => project.AddItem(PlanItemKind.MaaTask, names[3])),
                (() => project.AddItem(PlanItemKind.MaaTask, names[4]),
                    () => project.RemoveItem(PlanItemKind.MaaTask, names[4])),
                (() => project.SetItemEnabled(PlanItemKind.MaaTask, names[2], false),
                    () => project.SetItemEnabled(PlanItemKind.MaaTask, names[2], true)),
                (() => project.MoveTask(names[3], 1), () => project.MoveTask(names[3], 3)),
                (() => project.CreateConfiguration(), () => project.ActivateConfiguration(originalConfiguration))
            }) {
                change();
                InvokeEditor(window, "RenderTaskPlan");
                ObserveRuns(window, continued);
                Require(!section.IsVisible, "任务序列对不上计划末尾一段时应隐藏运行结果分区。");
                undo();
                InvokeEditor(window, "RenderTaskPlan");
                ObserveRuns(window, continued);
                Require(section.IsVisible, "恢复计划后记录应重新显示。");
            }

            ObserveRuns(window, ResultRun(RunState.Succeeded, (names[0], succeeded, 18)));
            Require(!section.IsVisible, "上次运行与当前计划不一致时应隐藏分区。");

            foreach (var name in names.Skip(4)) {
                project.AddItem(PlanItemKind.MaaTask, name);
            }
            InvokeEditor(window, "RenderTaskPlan");
            ObserveRuns(window, ResultRun(RunState.Failed, names.Select((name, index) => (name,
                index < 6 ? succeeded : index == 6 ? failed : cancelled, index <= 6 ? (int?)(18 + index) : null))
                .ToArray()));
            ClickOnboarding(window, "RunResultAllToggle");
            var activity = Named<FrameworkElement>(window, "ActivitySectionBody");
            Capture("long-expanded");
            foreach (var height in new[] { 900, 760, 640 }) {
                window.Height = height;
                window.UpdateLayout();
                PumpUntil(() => scroll.ScrollableHeight > 0 && scroll.ActualHeight <= 196 + PixelTolerance
                    && (height != 900 || Math.Abs(scroll.ActualHeight - 196) <= PixelTolerance)
                    && activity.ActualHeight >= 22 && activity.TransformToAncestor(panel)
                        .Transform(new Point(0, activity.ActualHeight)).Y <= panel.ActualHeight + PixelTolerance,
                    $"{height} 高度下 14 项应在列表内滚动且运行动态仍可见。");
            }
            Capture("long-failed-min-height");
            // A collapsed activity log gives its room to the list, and takes it back when expanded.
            var fitted = scroll.ActualHeight;
            ClickOnboarding(window, "ActivitySectionToggle");
            PumpUntil(() => !activity.IsVisible && scroll.ActualHeight > fitted + PixelTolerance
                && scroll.ActualHeight <= 196 + PixelTolerance, "收起运行动态后全部任务的高度应回升。");
            ClickOnboarding(window, "ActivitySectionToggle");
            PumpUntil(() => Math.Abs(scroll.ActualHeight - fitted) <= PixelTolerance && activity.ActualHeight >= 22,
                "展开运行动态后全部任务应重新让出空间。");

            void RequireMeter(params string[] brushes)
            {
                meter.UpdateLayout();
                var segments = Enumerable.Range(0, meter.Items.Count).Select(index => SettingsVisualDescendants(
                    meter.ItemContainerGenerator.ContainerFromIndex(index)).OfType<Border>().First()).ToArray();
                Require(segments.Length == brushes.Length && segments.All(segment => segment.ActualWidth > 0),
                    "分段条应每项一段。");
                foreach (var (segment, key) in segments.Zip(brushes)) {
                    RequireThemeBrush(segment.Background, System.Windows.Application.Current.Resources, key, "分段条");
                }
            }

            void RequireRowColor(int index, string name, string status)
            {
                var texts = Texts(all.ItemContainerGenerator.ContainerFromIndex(index));
                var resources = System.Windows.Application.Current.Resources;
                RequireThemeBrush(texts[0].Foreground, resources, name, "任务名");
                RequireThemeBrush(texts[1].Foreground, resources, status, "任务状态");
            }

            void Capture(string state)
            {
                var screenshots = Environment.GetEnvironmentVariable("BUNSHIN_PANEL_SCREENSHOTS");
                if (string.IsNullOrEmpty(screenshots)) {
                    return;
                }
                Directory.CreateDirectory(screenshots);
                var themes = ((App)System.Windows.Application.Current).Themes;
                try {
                    foreach (var (mode, suffix) in new[] { (AppThemeMode.Dark, "dark"), (AppThemeMode.Light, "light") }) {
                        themes.Apply(mode);
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        window.UpdateLayout();
                        SaveScreenshot(window, Path.Combine(screenshots, $"run-result-{state}-{suffix}.png"));
                    }
                } finally {
                    themes.Apply(AppThemeMode.Light);
                }
            }
        }, beforeLoad: directory => {
            var path = Path.Combine(directory, "interface.json");
            var project = JsonNode.Parse(File.ReadAllText(path))!;
            project["task"] = new JsonArray(names.Select(name =>
                (JsonNode)new JsonObject { ["name"] = name, ["entry"] = "Entry" }).ToArray());
            File.WriteAllText(path, project.ToJsonString());
        });

        static TextBlock[] Texts(DependencyObject row) =>
            SettingsVisualDescendants(row).OfType<TextBlock>().Where(text => text.IsVisible).ToArray();

        static string[] Lines(ItemsControl list) => Enumerable.Range(0, list.Items.Count).Select(index =>
            string.Join("|", Texts(list.ItemContainerGenerator.ContainerFromIndex(index)).Select(text => text.Text)))
            .ToArray();

        static void RequireLines(ItemsControl list, params string[] expected)
        {
            list.UpdateLayout();
            var actual = Lines(list);
            Require(actual.SequenceEqual(expected),
                $"{list.Name} 应为 {string.Join("；", expected)}，实际为 {string.Join("；", actual)}。");
        }
    }

    // 继续运行 submits the end of the plan through the same start flow as 运行, preparing the environment first when
    // needed. A plan that did not start is never picked up by the other button.
    private static void VerifyContinueRunWiring(AppLogger logger, string directory)
    {
        var windowDirectory = Path.Combine(directory, "window");
        var fixture = CreateProjectFixture(windowDirectory);
        var digest = ProjectPlanModule.Open(fixture, Path.Combine(fixture, "plan.json")).RuntimeProfileDigest;
        WithFakeWorker(logger, Path.Combine(directory, "worker"), (coordinator, record, pipeName) => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var connect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var synchronised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastRun = new TaskCompletionSource<RunSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new System.Collections.Concurrent.ConcurrentQueue<RunStartRequest>();
            // A new Worker without the old record; every start is refused before it begins, and the first refusal
            // also brings back a failed record so both buttons can be tried on a ready environment.
            var worker = Task.Run(async () => {
                try {
                    await connect.Task.WaitAsync(timeout.Token);
                    await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(
                        pipeName, record, 0, timeout.Token);
                    opened.TrySetResult();
                    var current = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0) with { StateRevision = 1 };
                    while (true) {
                        var request = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                            pipe, ProtocolOperations.RunStart, timeout.Token, current);
                        requests.Enqueue(ProtocolJson.Deserialize<RunStartRequest>(request.Data));
                        await pipe.WriteAsync(WireEnvelope.Failure(request.Operation, request.RequestId!.Value,
                            "worker_busy", "模拟 Worker 忙"), timeout.Token);
                        if (current.LastRun is null) {
                            current = current with { StateRevision = 2, LastRun = await lastRun.Task };
                            await pipe.WriteAsync(StateEvent(current), timeout.Token);
                        }
                    }
                } catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
                }
            });
            var wts = new SimulatedWts();
            using var session = wts.CreateManager(logger);
            var environment = CreateEnvironment(logger, session, coordinator);
            var probe = new PresentationProbe();
            MainWindow? shown = null;
            try {
                WithRuntimeControlWindow(logger, windowDirectory, window => {
                    shown = window;
                    var interfacePath = Path.Combine(windowDirectory, "project", "interface.json");
                    var definition = JsonNode.Parse(File.ReadAllText(interfacePath))!;
                    foreach (var name in new[] { "SecondTask", "ThirdTask" }) {
                        var task = definition["task"]![0]!.DeepClone();
                        task["name"] = name;
                        task["label"] = name;
                        definition["task"]!.AsArray().Add(task);
                    }
                    File.WriteAllText(interfacePath, definition.ToJsonString());
                    InvokeOnboarding(window, "LoadProject");
                    var first = ProjectOf(window).SelectedItems.Single().Name;
                    ProjectOf(window).AddItem(PlanItemKind.MaaTask, "SecondTask");
                    ProjectOf(window).AddItem(PlanItemKind.MaaTask, "ThirdTask");
                    InvokeEditor(window, "RenderTaskPlan");
                    var failed = ResultRun(RunState.Failed, (first, PlanItemState.Succeeded, 10),
                        ("SecondTask", PlanItemState.Failed, 5), ("ThirdTask", PlanItemState.Cancelled, null));
                    lastRun.SetResult(failed);
                    string[] whole = [first, "SecondTask", "ThirdTask"], tail = ["SecondTask", "ThirdTask"];
                    var continueRun = Named<System.Windows.Controls.Button>(window, "ContinueRunButton");

                    // The desktop clone has ended, but its record is still on screen.
                    SetOnboardingField(window, "_workerSnapshot", new WorkerCoordinatorSnapshot(
                        WorkerObservation.ChildSessionEnded, false,
                        WorkerCoordinatorSelfTest.CreateSnapshot(record, 0) with { LastRun = failed }, "ended"));
                    InvokeOnboarding(window, "RefreshRuntimeControls");
                    PumpUntil(() => continueRun.IsVisible && continueRun.IsEnabled, "桌面分身已关闭但记录还在时应可继续运行。");
                    RunStartRequest Next(string action, int count)
                    {
                        PumpUntil(() => requests.Count == count && window.CurrentOperation is PendingOperation.None
                            && probe.Dialogs.Count == count, $"{action}应提交一次并结束。", failure => new InvalidOperationException(
                                $"{failure} 提交 {requests.Count}，操作 {window.CurrentOperation}，"                                + $"提示 {string.Join("；", probe.Dialogs.Select(dialog => $"{dialog.Operation}:{dialog.Message}"))}"));
                        return requests.ToArray()[^1];
                    }
                    string[] Tasks(RunStartRequest request) => request.Plan.Items.Select(item => item.TaskName).ToArray();

                    Click(window, "ContinueRunButton");
                    PumpUntil(() => coordinator.Snapshot.SnapshotFresh, "准备时新 Worker 应完成同步。");
                    synchronised.TrySetResult();
                    var prepared = Next("准备后继续运行", 1);
                    Require(Tasks(prepared).SequenceEqual(tail), "准备运行环境后，继续运行应只执行起点及之后各项。");

                    SetOnboardingField(window, "_sessionSnapshot", session.Snapshot);
                    InvokeOnboarding(window, "RefreshRuntimeControls");
                    PumpUntil(() => continueRun.IsVisible && continueRun.IsEnabled, "环境就绪后记录仍在时应可继续运行。");
                    Click(window, "StartPlanButton");
                    var run = Next("运行", 2);
                    Require(Tasks(run).SequenceEqual(whole) && run.RunId != prepared.RunId,
                        "继续运行没有开始后，运行应执行完整计划，不复用继续运行的计划。");
                    Click(window, "StartPlanButton");
                    var again = Next("再次运行", 3);
                    Require(again.RunId == run.RunId, "没有开始的运行，再次运行时应复用同一份计划。");
                    Click(window, "ContinueRunButton");
                    var continued = Next("继续运行", 4);
                    Require(Tasks(continued).SequenceEqual(tail) && continued.RunId != run.RunId
                        && continued.RunId != prepared.RunId, "运行没有开始后，继续运行不复用完整计划，也不复用此前的继续运行。");
                    Click(window, "StartPlanButton");
                    var rerun = Next("继续运行后运行", 5);
                    Require(Tasks(rerun).SequenceEqual(whole) && rerun.RunId != continued.RunId,
                        "继续运行没有开始后，运行应另起完整计划。");
                    Click(window, "ContinueRunButton");
                    var continuedAgain = Next("再次继续运行", 6);
                    Require(Tasks(continuedAgain).SequenceEqual(tail) && continuedAgain.RunId != continued.RunId
                        && continuedAgain.RunId != rerun.RunId, "继续运行每次都应另起计划。");
                }, session: session, coordinator: coordinator, environment: environment, presentation: probe.Presentation,
                    prepareEnvironment: async (_, _, _) => {
                        // A new Worker clears the old record before the Run starts.
                        SetOnboardingField(shown!, "_workerSnapshot", new WorkerCoordinatorSnapshot(
                            WorkerObservation.WorkerNotStarted, false, null, "preparing"));
                        connect.TrySetResult();
                        await opened.Task.WaitAsync(timeout.Token);
                        await synchronised.Task.WaitAsync(timeout.Token);
                        ConnectTestDesktop(session, record.ChildSessionId);
                    });
            } finally {
                timeout.Cancel();
                worker.GetAwaiter().GetResult();
            }
        }, runtimeProfileDigest: digest);
    }

    // The task the result section names as 进行中.
    private static string? RunningResultTask(MainWindow window)
    {
        var outcomes = Named<ItemsControl>(window, "RunResultOutcomes");
        outcomes.UpdateLayout();
        return Enumerable.Range(0, outcomes.Items.Count)
            .Select(index => SettingsVisualDescendants(outcomes.ItemContainerGenerator.ContainerFromIndex(index))
                .OfType<TextBlock>().Select(text => text.Text).ToArray())
            .SingleOrDefault(texts => texts[0] == "进行中")?[1];
    }

    // The console is one card with two sections; the activity log takes all height below the run control.
    private static void VerifyRuntimeConsole(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "runtime-console"), (window, _) => {
            EndTour(window);
            var console = Named<FrameworkElement>(window, "RuntimeSidebar");
            var home = Named<FrameworkElement>(window, "HomeView");
            foreach (var width in new[] { 920, 1180, 1440 }) {
                window.Width = width;
                window.Height = width == 920 ? 640 : width == 1180 ? 760 : 900;
                PumpOnboarding();
                Require(Math.Abs(console.ActualWidth - 320) < 1
                    && console.ActualWidth is >= 220 and <= 800
                    && Named<FrameworkElement>(window, "TasksView").ActualWidth > 0
                    && console.TransformToAncestor(home).Transform(new Point(console.ActualWidth, 0)).X
                        <= home.ActualWidth + PixelTolerance, $"{width} 宽度下侧栏宽度或主页布局不符。");
            }
            var screenshots = Environment.GetEnvironmentVariable("BUNSHIN_PANEL_SCREENSHOTS");
            if (!string.IsNullOrEmpty(screenshots)) {
                Directory.CreateDirectory(screenshots);
                window.Width = 1180;
                window.Height = 760;
                PumpOnboarding();
                SaveScreenshot(window, Path.Combine(screenshots, "panel-idle.png"));
            }
            var desktop = new DesktopToggle(DesktopAction.Show, true, DesktopStatus.Background);

            foreach (var (state, showClose, endReason) in new (RuntimeControlState, bool, string)[] {
                (new(new RuntimeAction.Start(true), true, null, desktop, true), true, ""),
                (new(new RuntimeAction.Stop(true), false, LockReason.RunActive, desktop, true), true, "请先停止任务"),
                (new(new RuntimeAction.Prepare(true), true, null,
                    desktop with { Enabled = false, Status = DesktopStatus.NotRunning }, true), false, "未创建"),
                (new(new RuntimeAction.InProgress(ProgressKind.PreparingEnvironment), false, LockReason.RuntimeBusy,
                    desktop with { Enabled = false, Status = DesktopStatus.Connecting }, false), false, "处理中"),
                (new(new RuntimeAction.Recover(true, RecoveryReason.WorkerLost), false, LockReason.RuntimeBusy,
                    desktop, true), true, "")
            }) {
                window.ApplyRuntimeControls(state);
                var end = Named<System.Windows.Controls.MenuItem>(window, "EndEnvironmentMenuItem");
                var start = Named<System.Windows.Controls.MenuItem>(window, "StartSessionMenuItem");
                Require(end.IsEnabled == (endReason.Length == 0)
                    && (string?)end.ToolTip == (endReason.Length == 0 ? "注销分身，游戏随之关闭" : endReason)
                    && end.Visibility == (showClose ? Visibility.Visible : Visibility.Collapsed)
                    && start.Visibility == (showClose ? Visibility.Collapsed : Visibility.Visible)
                    && start.IsEnabled == (state.Action is RuntimeAction.Prepare { Enabled: true }),
                    $"{state.Action} 时分身菜单的动作、可用性或原因不符。");
            }

            SetOnboardingField(window, "_sessionSnapshot",
                new ChildSessionSnapshot(ChildSessionState.Connected, 7, 1, "fixture"));
            foreach (var (observation, fresh, detail) in new[] {
                (WorkerObservation.WorkerNotStarted, false, "桌面分身已连接，等待 Worker 启动…"),
                (WorkerObservation.WorkerStarting, false, "正在启动 Worker…"),
                (WorkerObservation.Connected, false, "正在同步 Worker 状态…"),
                (WorkerObservation.Connected, true, "正在完成运行环境准备…")
            }) {
                SetOnboardingField(window, "_workerSnapshot",
                    new WorkerCoordinatorSnapshot(observation, fresh, null, ""));
                window.ApplyRuntimeControls(new(new RuntimeAction.InProgress(ProgressKind.PreparingEnvironment),
                    false, LockReason.RuntimeBusy, desktop, false));
                Require(Named<TextBlock>(window, "RuntimeStatusDetail").Text == detail,
                    "准备阶段只能描述已观察到的 Session 和 Worker 状态。");
            }
        });
    }

    // A Run leaves the plan looking as it did, only locked; the result section alone follows it.
    private static void VerifyRunProgressWiring(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "run-progress-wiring"), (window, _) => {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            EndTour(window);
            // An unchecked task is left out of the Run; its row must not say so.
            var project = ProjectOf(window);
            project.AddItem(PlanItemKind.MaaTask, "Spare");
            project.SetItemEnabled(PlanItemKind.MaaTask, "Spare", false);
            InvokeEditor(window, "RenderTaskPlan");
            var empty = JsonDocument.Parse("{}").RootElement.Clone();
            var started = DateTime.UtcNow.AddSeconds(-65);
            var item = new RunPlanItem(Guid.NewGuid(), "RealTask", "Real task", "Entry", empty, empty);
            var plan = new RunPlan(1, started, new ProjectProvenance("self-test", "1", 1, "self-test"), "self-test",
                empty, [item]);
            PlanItemSnapshot Item(PlanItemState state, DateTime? ended) => new(item.PlanItemId, item.TaskName,
                item.TaskLabel, item.Entry, empty, empty, state, started, ended, null, null, null);
            var run = new RunSnapshot(Guid.NewGuid(), "digest", RunState.Running, started, started, null, null,
                item.PlanItemId, 0, plan, [Item(PlanItemState.Running, null)], null, null);
            var record = new WorkerAdmissionRecord(Guid.NewGuid(), "token", 1, Environment.ProcessId, "self-test",
                DateTime.UtcNow);
            var idle = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
            void Observe(WorkerSnapshot worker)
            {
                SetOnboardingField(window, "_workerSnapshot",
                    new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true, worker, ""));
                InvokeOnboarding(window, "RefreshRuntimeControls");
                PumpOnboarding();
            }
            var timer = (System.Windows.Threading.DispatcherTimer)typeof(MainWindow).GetField("_runTimer", flags)!
                .GetValue(window)!;
            var items = Named<System.Windows.Controls.Panel>(window, "PlanItemsPanel");
            var count = Named<TextBlock>(window, "PlanCountText");
            string CardText() => string.Join("\n", ConfigurationDescendants(items).OfType<TextBlock>()
                .Select(text => text.Text));
            // Everything in the rows that changes the configuration: selection, removal and parameter editors.
            System.Windows.Controls.Control[] Editors() => ConfigurationDescendants(items)
                .OfType<System.Windows.Controls.Control>()
                .Where(control => control is System.Windows.Controls.Primitives.ToggleButton
                    or System.Windows.Controls.TextBox or System.Windows.Controls.ComboBox
                    or System.Windows.Controls.ListBox
                    || control is System.Windows.Controls.Button button
                        && AutomationProperties.GetName(button).Contains("移除", StringComparison.Ordinal))
                .ToArray();
            void ToggleDetails()
            {
                var header = ConfigurationDescendants(items).OfType<System.Windows.Controls.Button>()
                    .Single(button => button.Tag is ProjectTaskChoice { Name: "RealTask" }
                        && button.Style == window.FindResource("PlanHeaderButtonStyle"));
                header.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.UpdateLayout();
            }
            Observe(idle);
            if (ConfigurationDescendants(items).OfType<System.Windows.Controls.TextBox>().Any()) {
                ToggleDetails(); // Starts from a folded card, so the expansion below shows the parameters.
            }
            var idleCardText = CardText();
            var idleCount = count.Text;
            var idleEditors = Editors().Length;
            Require(idleEditors > 0 && Editors().All(editor => editor.IsEnabled), "空闲时任务行应可编辑。");

            Observe(idle with { RunState = RunState.Running, ActiveRun = run });
            Require(items.IsVisible && CardText() == idleCardText && count.Text == idleCount,
                $"运行中任务行和计数应与空闲时一致：{count.Text}；{CardText()}");
            Require(Editors().Length == idleEditors && Editors().All(editor => !editor.IsEnabled),
                "运行中任务行应保持原样并锁定。");
            ToggleDetails();
            Require(Editors().Length > idleEditors && Editors().All(editor => !editor.IsEnabled),
                "运行中仍可展开查看参数，但不能修改。");
            ToggleDetails();
            Require(timer.IsEnabled
                && Named<System.Windows.Controls.Button>(window, "StopPlanButton").IsVisible
                && !Named<System.Windows.Controls.Button>(window, "TaskShelfHeaderButton").IsEnabled
                && Named<TextBlock>(window, "RunResultCount").Text == "0 / 1 项完成"
                && Named<TextBlock>(window, "RunResultTime").Text.StartsWith("总用时 1:", StringComparison.Ordinal)
                && RunningResultTask(window) == "Real task",
                "运行结果分区应显示进度、当前项与总用时。");
            var screenshots = Environment.GetEnvironmentVariable("BUNSHIN_PANEL_SCREENSHOTS");
            if (!string.IsNullOrEmpty(screenshots)) {
                Directory.CreateDirectory(screenshots);
                window.Width = 1180;
                window.Height = 760;
                PumpOnboarding();
                SaveScreenshot(window, Path.Combine(screenshots, "panel-running.png"));
            }

            Observe(idle with {
                LastRun = run with {
                    State = RunState.Succeeded, EndedAtUtc = started, Items = [Item(PlanItemState.Succeeded, started)]
                }
            });
            Require(CardText() == idleCardText && count.Text == idleCount && !timer.IsEnabled
                && Editors().All(editor => editor.IsEnabled), "Run 结束后任务行应恢复可编辑，计时停止。");
            ToggleDetails();
            Require(Editors().Length > idleEditors && Editors().All(editor => editor.IsEnabled),
                "Run 结束后展开任务应可以修改参数。");
            window.ApplyRuntimeControls(new(new RuntimeAction.Prepare(true), true, null,
                new DesktopToggle(DesktopAction.Show, false, DesktopStatus.NotRunning), true));
            Require(Named<UIElement>(window, "RunResultSection").IsVisible
                && Named<TextBlock>(window, "RunResultCount").Text == "1 / 1 项成功",
                "匹配当前配置的 Last Run 不能因运行环境需要重新准备而隐藏。");
        }, beforeLoad: directory => {
            var path = Path.Combine(directory, "interface.json");
            var project = JsonNode.Parse(File.ReadAllText(path))!;
            project["task"]!.AsArray().Add((JsonNode)new JsonObject { ["name"] = "Spare", ["entry"] = "Entry" });
            File.WriteAllText(path, project.ToJsonString());
        });
    }
}
