using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Gui.ChildSession;
using Gui.Runtime;
using Gui.Worker;
using ProjectModel;
using Protocol;
using Shape = System.Windows.Shapes.Shape;
using WpfControl = System.Windows.Controls.Control;

namespace Gui.Views;

public partial class MainWindow
{
    private void InitializeRuntimePanel()
    {
        RuntimeSidebar.Attach(_settings);
        RuntimeSidebar.StartSessionMenuItem.Click += PrepareEnvironmentButton_Click;
        RuntimeSidebar.RetryEnvironmentButton.Click += RetryRuntimeHeaderButton_Click;
        RuntimeSidebar.ContinueRunButton.Click += ContinueRunButton_Click;
        RuntimeSidebar.RecoverEnvironmentButton.Click += RecoverEnvironmentButton_Click;
        RuntimeSidebar.DesktopViewButton.Click += DesktopViewButton_Click;
        RuntimeSidebar.EndEnvironmentMenuItem.Click += EndEnvironmentMenuItem_Click;
        RuntimeSidebar.ClearActivityButton.Click += ClearActivity_Click;
        RuntimeSidebar.RuntimeSchedulePageButton.Click += (_, _) => SwitchSection(MainSection.Schedule);
        RuntimeSidebar.NextRunRow.Click += (_, _) => SwitchSection(MainSection.Schedule);
        RuntimeSidebar.SchedulePolicyToggleRequested += ScheduleView.SetPolicyEnabled;
    }

    internal void ApplyRuntimeControls(RuntimeControlState state)
    {
        ApplyDesktopToggle(state.Desktop);

        var action = state.Action;
        // A failed Run is started again with 运行, so the panel only retries preparation.
        RuntimeSidebar.RetryEnvironmentButton.Visibility =
            Visible(action is RuntimeAction.Retry { Target: RetryTarget.Prepare });
        RuntimeSidebar.RetryEnvironmentButton.IsEnabled = action is RuntimeAction.Retry { Enabled: true };
        RuntimeSidebar.RecoverEnvironmentButton.Visibility = Visible(action is RuntimeAction.Recover);
        RuntimeSidebar.RecoverEnvironmentButton.IsEnabled = action is RuntimeAction.Recover { Enabled: true };
        if (action is RuntimeAction.Recover { Reason: var recovery }) {
            var reason = RecoveryText(recovery);
            RuntimeSidebar.RecoverEnvironmentButton.ToolTip = reason;
            AutomationProperties.SetHelpText(RuntimeSidebar.RecoverEnvironmentButton, reason);
        }
        RuntimeSidebar.RuntimeHeaderProgressRing.Visibility = Visible(action is RuntimeAction.InProgress);
        if (action is RuntimeAction.InProgress { Kind: var kind }) {
            var operation = kind switch {
                ProgressKind.StoppingRun => PendingOperation.StoppingRun,
                ProgressKind.StartingRun => PendingOperation.StartingRun,
                _ => PendingOperation.PreparingEnvironment
            };
            var progressText = kind == ProgressKind.EndingEnvironment
                ? "正在结束桌面分身" : $"正在{OperationLabel(operation)}";
            AutomationProperties.SetName(RuntimeSidebar.RuntimeHeaderProgressRing, progressText);
            RuntimeSidebar.RuntimeProgressText.Text = $"{progressText}…";
        }
        ApplyMoreMenu(state);
        var progress = CurrentRunProgress;
        ApplyRuntimeStatus(state);
        ApplyPanelSummary(state, progress);
        RuntimeSidebar.ShowRunResult(state.Action is RuntimeAction.Recover ? null : progress);
        _runTimer.IsEnabled = progress is { Active: true };

        TaskPlanEditor.ApplyConfigurationLock(state.ConfigurationEditable, state.ConfigurationLock);
        TaskPlanEditor.ApplyRunPresentation(state, progress,
            PlanStartBlocker(action) is { } blocker ? StartBlockerText(blocker) : null);
        // 继续运行 is offered beside the result and can be used exactly when 运行 can, for the same reason.
        var continueRun = RuntimeSidebar.ContinueRunButton;
        var from = ContinuationStart(progress);
        continueRun.Visibility = Visible(from is not null);
        continueRun.IsEnabled = TaskPlanEditor.StartPlanButton.IsEnabled;
        continueRun.ToolTip = continueRun.IsEnabled && from is { } start
            ? $"从「{start.TaskLabel}」开始" : TaskPlanEditor.StartPlanButton.ToolTip;
        ApplyConfigurationLockToShell(state.ConfigurationEditable, state.ConfigurationLock);

        static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    }

    private void ApplyPanelSummary(RuntimeControlState state, RunProgressView? progress)
    {
        var (status, tone) = state.Action switch {
            RuntimeAction.Stop => ("运行中", "Brush.Success"),
            RuntimeAction.Recover => ("故障", "Brush.Error"),
            RuntimeAction.Retry { Target: RetryTarget.Prepare } => ("需重试", "Brush.Error"),
            RuntimeAction.InProgress { Kind: ProgressKind.PreparingEnvironment } => ("准备中", "Brush.Warning"),
            RuntimeAction.InProgress { Kind: ProgressKind.EndingEnvironment } => ("正在结束", "Brush.Warning"),
            RuntimeAction.InProgress { Kind: ProgressKind.StartingRun } => ("启动中", "Brush.Warning"),
            RuntimeAction.InProgress { Kind: ProgressKind.StoppingRun } => ("停止中", "Brush.Warning"),
            _ => state.Desktop.Status switch {
                DesktopStatus.Interactive => ("可操作", "Brush.Warning"),
                DesktopStatus.Viewing => ("观看中", "Brush.Success"),
                DesktopStatus.Background => ("已启动", "Brush.Success"),
                DesktopStatus.Connecting => ("启动中", "Brush.Warning"),
                DesktopStatus.Ending => ("正在结束", "Brush.Warning"),
                DesktopStatus.Disconnected => ("已断开", "Brush.Error"),
                _ => ("未启动", null)
            }
        };
        RuntimeSidebar.RuntimeStatusText.Text = status;
        RuntimeSidebar.RuntimeStatusText.SetResourceReference(TextBlock.ForegroundProperty, tone switch {
            "Brush.Success" => "Brush.Success.Foreground",
            "Brush.Error" => "Brush.Error.Foreground",
            "Brush.Warning" => "Brush.Warning.Foreground",
            _ => "Brush.Text.Primary"
        });
        RuntimeSidebar.SessionStatusDot.SetResourceReference(Shape.FillProperty, tone ?? "Brush.Transparent");
        RuntimeSidebar.SessionStatusDot.SetResourceReference(Shape.StrokeProperty, "Brush.Border.Strong");
        RuntimeSidebar.SessionStatusDot.StrokeThickness = tone is null ? 1 : 0;
        RuntimeSidebar.NextRunRow.Visibility = progress is { Active: true } || state.Action is RuntimeAction.InProgress or RuntimeAction.Recover
            ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(RuntimeSidebar.RuntimeStatusPill, $"桌面分身：{RuntimeSidebar.RuntimeStatusText.Text}");
        RuntimeSidebar.DesktopViewText.Text = state.Desktop.Action == DesktopAction.Hide ? "隐藏窗口" : "显示窗口";
        RuntimeSidebar.DesktopViewButton.ToolTip = state.Desktop.Action == DesktopAction.Hide
            ? "隐藏窗口，分身和任务继续运行" : "显示分身窗口，默认只读";
        RuntimeSidebar.RuntimeStatusPill.ToolTip = state.Desktop.Status == DesktopStatus.Connecting ? PreparationStage() : null;
    }

    private static string StartBlockerText(StartBlocker blocker) => blocker switch {
        StartBlocker.CommandsUnavailable => "当前操作完成后可开始任务",
        StartBlocker.ProjectNotLoaded => "项目未加载，无法开始任务",
        StartBlocker.NoTasks => "请先添加或勾选要执行的任务",
        StartBlocker.ConfigurationInvalid => "当前配置未通过校验，请先修正任务参数",
        _ => "运行环境尚未就绪，请稍候"
    };

    private static string RecoveryText(RecoveryReason reason) => reason switch {
        RecoveryReason.StopUnconfirmed => "任务未能确认结束，任务可能仍在运行",
        RecoveryReason.RuntimeFaulted => "运行环境故障，Worker 无法继续执行任务",
        RecoveryReason.WorkerLost => "Worker 失联且未确认已退出；也可以等待它重新连接",
        _ => "上次结束桌面分身未确认"
    };

    // Unlocked viewer input gets a warning treatment even while the run itself is active.
    private void ApplyDesktopToggle(DesktopToggle desktop)
    {
        RuntimeSidebar.DesktopViewButton.IsEnabled = desktop.Enabled;
        if (desktop.Status == DesktopStatus.Interactive) {
            RuntimeSidebar.DesktopViewButton.SetResourceReference(WpfControl.ForegroundProperty, "Brush.Warning.Foreground");
        } else {
            RuntimeSidebar.DesktopViewButton.ClearValue(WpfControl.ForegroundProperty);
        }
        AutomationProperties.SetName(RuntimeSidebar.DesktopViewButton,
            desktop.Action == DesktopAction.Hide ? "隐藏窗口" : "显示窗口");
    }

    private void ApplyMoreMenu(RuntimeControlState state)
    {
        var showClose = state.Desktop.Status is DesktopStatus.Background or DesktopStatus.Viewing
            or DesktopStatus.Interactive or DesktopStatus.Ending || state.Action is RuntimeAction.Recover;
        RuntimeSidebar.StartSessionMenuItem.Visibility = showClose ? Visibility.Collapsed : Visibility.Visible;
        RuntimeSidebar.StartSessionMenuItem.IsEnabled = state.Action is RuntimeAction.Prepare { Enabled: true };
        RuntimeSidebar.EndEnvironmentMenuItem.Visibility = showClose ? Visibility.Visible : Visibility.Collapsed;
        var endBlocker = state.Action is RuntimeAction.Stop ? "请先停止任务" : EndEnvironmentBlocker(state);
        RuntimeSidebar.EndEnvironmentMenuItem.IsEnabled = endBlocker is null;
        RuntimeSidebar.EndEnvironmentMenuItem.ToolTip = endBlocker ?? "注销分身，游戏随之关闭";
    }

    // The running Run, or else the 运行记录 of the configuration on screen.
    private RunProgressView? CurrentRunProgress => _projectPlan is { } project
        ? RunProgress.Derive(_workerSnapshot.WorkerSnapshot, _runRecords.For(project.ActiveConfigurationId)) : null;

    private static (PlanItemKind Kind, string Name)[] PlanSequence(ProjectPlanModule project) =>
        project.EnabledItems.Select(item => (item.Kind, item.Name)).ToArray();

    // The record's item 继续运行 starts from, as it was named in that Run.
    private PlanItemProgressView? ContinuationStart(RunProgressView? progress)
    {
        if (_projectPlan is not { } project || progress is null
            || RunProgress.ContinueFrom(progress, PlanSequence(project)) is not { } from) {
            return null;
        }
        var (kind, name) = PlanSequence(project)[from];
        return progress.Items.First(item => item.Kind == kind && item.Name == name);
    }

    // The part of the plan 继续运行 covers, read on the click, before preparation reloads the project.
    private IReadOnlyList<(PlanItemKind Kind, string Name)>? CurrentContinuation() =>
        _projectPlan is { } project && CurrentRunProgress is { } progress
        && RunProgress.ContinueFrom(progress, PlanSequence(project)) is { } from ? PlanSequence(project)[from..] : null;

    // Admission and results reach the records from the Worker's snapshot and from the lifecycle, which also keeps an
    // ended result a later Run has pushed out of the snapshot.
    private void ObserveRunRecords()
    {
        foreach (var run in _lifecycle.Orchestration.Lifecycle.Runs.Values) {
            if (run.Conclusion is StartConclusion.Accepted or StartConclusion.EndUnconfirmed || run.SeenActive
                || run.Terminal is not null) {
                _runRecords.Observe(run.Key.RunId, run.Terminal);
            }
        }
        if (_workerSnapshot.WorkerSnapshot is { } worker) {
            if (worker.ActiveRun is { } active) {
                _runRecords.Observe(active.RunId);
            }
            if (worker.LastRun is { } last) {
                _runRecords.Observe(last.RunId, last);
            }
        }
    }

    private enum StatusTone { Idle, Busy, Ok, Run, Bad }

    // Reuse the action chosen by RuntimeControls for the schedule header and the panel's supporting text.
    private void ApplyRuntimeStatus(RuntimeControlState state)
    {
        var (tone, pill, detail, warn) = state.Action switch {
            RuntimeAction.Prepare => (StatusTone.Idle, "未准备", string.Empty, false),
            RuntimeAction.InProgress { Kind: ProgressKind.PreparingEnvironment } => (StatusTone.Busy, "准备中",
                PreparationStage(), false),
            RuntimeAction.InProgress { Kind: ProgressKind.StartingRun } => (StatusTone.Busy, "启动中",
                "正在把执行计划交给 Worker…", false),
            RuntimeAction.InProgress { Kind: ProgressKind.StoppingRun } => (StatusTone.Busy, "停止中",
                "等待当前任务安全结束…", false),
            RuntimeAction.InProgress => (StatusTone.Busy, "处理中", "正在关闭分身和其中的程序…", false),
            RuntimeAction.Stop => (StatusTone.Run, "运行中", string.Empty, false),
            // A failed Run leaves nothing to retry here, so a ready Runtime Environment reads as ready.
            RuntimeAction.Start or RuntimeAction.Retry { Target: RetryTarget.Start } =>
                PlanStartBlocker(state.Action) is { } blocker
                    ? (StatusTone.Ok, "就绪", StartBlockerText(blocker), true)
                    : (StatusTone.Ok, "就绪", string.Empty, false),
            RuntimeAction.Retry => (StatusTone.Bad, "需重试", "重新准备运行环境后再开始任务", false),
            RuntimeAction.Recover { Reason: var reason } => (StatusTone.Bad, "故障", RecoveryText(reason), false),
            _ => (StatusTone.Idle, string.Empty, string.Empty, false)
        };
        ApplyScheduleHeaderStatus(tone, pill);
        Sidebar.ShowRunning(tone == StatusTone.Run);
        RuntimeSidebar.RecoveryTitleText.Visibility = state.Action is RuntimeAction.Recover
            ? Visibility.Visible : Visibility.Collapsed;
        if (state.Action is RuntimeAction.Recover { Reason: var recovery }) {
            RuntimeSidebar.RecoveryTitleText.Text = RecoveryTitle(recovery);
        }
        RuntimeSidebar.RuntimeStatusDetail.Text = detail;
        RuntimeSidebar.RuntimeStatusDetail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RuntimeSidebar.RuntimeStatusDetail.SetResourceReference(TextBlock.ForegroundProperty,
            warn ? "Brush.Warning.Foreground" : "Brush.Text.Secondary");

        static string RecoveryTitle(RecoveryReason reason) => reason switch {
            RecoveryReason.StopUnconfirmed => "任务未能确认结束",
            RecoveryReason.RuntimeFaulted => "运行环境故障",
            RecoveryReason.WorkerLost => "Worker 失联",
            _ => "结束桌面分身未确认"
        };
    }

    // Preparation does not launch the game; describe only the stages Session and Worker observations confirm.
    private string PreparationStage() =>
        _sessionSnapshot.State != ChildSessionState.Connected
            ? "正在连接桌面分身…"
            : _workerSnapshot.Observation == WorkerObservation.WorkerNotStarted ? "桌面分身已连接，等待 Worker 启动…"
            : _workerSnapshot.Observation == WorkerObservation.WorkerStarting ? "正在启动 Worker…"
            : _workerSnapshot.Resynchronising
                ? "正在同步 Worker 状态…" : "正在完成运行环境准备…";

    private void RefreshRunTiming()
    {
        var progress = CurrentRunProgress;
        if (progress is not { Active: true }) {
            return;
        }
        RuntimeSidebar.UpdateRunTiming();
    }
}
