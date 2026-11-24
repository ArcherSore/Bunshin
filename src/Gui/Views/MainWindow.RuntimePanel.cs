using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Gui.ChildSession;
using Gui.Worker;
using Shape = System.Windows.Shapes.Shape;
using WpfControl = System.Windows.Controls.Control;

namespace Gui.Views;

public partial class MainWindow
{
    private void InitializeRuntimePanel()
    {
        RuntimeSidebar.Attach(_settings);
        RuntimeSidebar.PrepareEnvironmentButton.Click += PrepareAndRunButton_Click;
        RuntimeSidebar.StartSessionMenuItem.Click += PrepareEnvironmentButton_Click;
        RuntimeSidebar.RetryEnvironmentButton.Click += RetryRuntimeHeaderButton_Click;
        RuntimeSidebar.RecoverEnvironmentButton.Click += RecoverEnvironmentButton_Click;
        RuntimeSidebar.StartTaskHeaderButton.Click += StartRunButton_Click;
        RuntimeSidebar.StopTaskHeaderButton.Click += StopRunButton_Click;
        RuntimeSidebar.DesktopViewButton.Click += DesktopViewButton_Click;
        RuntimeSidebar.OpenLogsMenuItem.Click += OpenLogsMenuItem_Click;
        RuntimeSidebar.EndEnvironmentMenuItem.Click += EndEnvironmentMenuItem_Click;
        RuntimeSidebar.AutoSessionMenuItem.Click += AutoSessionMenuItem_Click;
        RuntimeSidebar.ClearActivityButton.Click += ClearActivity_Click;
        RuntimeSidebar.RuntimeSchedulePageButton.Click += (_, _) => SwitchSection(MainSection.Schedule);
        RuntimeSidebar.SchedulePolicyToggleRequested += ScheduleView.SetPolicyEnabled;
        RuntimeSidebar.RunProgressSizeChanged += (_, _) => DrawRunProgressBar();
    }

    internal void ApplyRuntimeControls(RuntimeControlState state)
    {
        ApplyDesktopToggle(state.Desktop);

        var action = state.Action;
        RuntimeSidebar.PrepareEnvironmentButton.Visibility = Visible(action is RuntimeAction.Prepare);
        RuntimeSidebar.PrepareEnvironmentButton.IsEnabled = action is RuntimeAction.Prepare { Enabled: true };
        RuntimeSidebar.RetryEnvironmentButton.Visibility = Visible(action is RuntimeAction.Retry);
        RuntimeSidebar.RetryEnvironmentButton.IsEnabled = action is RuntimeAction.Retry { Enabled: true };
        RuntimeSidebar.StartTaskHeaderButton.Visibility = Visible(action is RuntimeAction.Start);
        RuntimeSidebar.StartTaskHeaderButton.IsEnabled = action is RuntimeAction.Start { Enabled: true };
        Explain(RuntimeSidebar.StartTaskHeaderButton, (action as RuntimeAction.Start)?.Blocker);
        Explain(RuntimeSidebar.RetryEnvironmentButton, (action as RuntimeAction.Retry)?.Blocker);
        if (action is RuntimeAction.Retry { Target: var retryTarget }) {
            var retryText = retryTarget == RetryTarget.Prepare ? "重试准备运行环境" : "重试开始任务";
            RuntimeSidebar.RetryEnvironmentText.Text = retryText;
            AutomationProperties.SetName(RuntimeSidebar.RetryEnvironmentButton, retryText);
        }
        RuntimeSidebar.StopTaskHeaderButton.Visibility = Visible(action is RuntimeAction.Stop);
        RuntimeSidebar.StopTaskHeaderButton.IsEnabled = action is RuntimeAction.Stop { Enabled: true };
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
        ApplyRuntimeStatus(state, progress);
        ApplyRunProgress(progress);
        ApplyPanelSummary(state, progress);

        TaskPlanEditor.ApplyConfigurationLock(state.ConfigurationEditable, state.ConfigurationLock);
        Sidebar.AllowNewConfiguration(state.ConfigurationEditable);

        static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

        // A blocked Start, or a Retry that would start, says why on hover and to screen readers; the task area
        // holds the detailed message. The visible label needs no tooltip of its own.
        static void Explain(FrameworkElement button, StartBlocker? blocker)
        {
            var reason = blocker is { } value ? StartBlockerText(value) : null;
            button.ToolTip = reason;
            AutomationProperties.SetHelpText(button, reason ?? string.Empty);
        }
    }

    private void ApplyPanelSummary(RuntimeControlState state, RunProgressView? progress)
    {
        var running = state.Desktop.Status is DesktopStatus.Background or DesktopStatus.Viewing
            or DesktopStatus.Interactive;
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
        RuntimeSidebar.StartSessionMenuItem.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        RuntimeSidebar.StartSessionMenuItem.IsEnabled = state.Action is RuntimeAction.Prepare { Enabled: true };
        RuntimeSidebar.RuntimeStatusPill.ToolTip = state.Desktop.Status == DesktopStatus.Connecting ? PreparationStage() : null;
        RuntimeSidebar.EndEnvironmentMenuItem.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RuntimeSidebar.SessionMenuSeparator.Visibility = RuntimeSidebar.StartSessionMenuItem.Visibility == Visibility.Visible || running
            ? Visibility.Visible : Visibility.Collapsed;
        RuntimeSidebar.RunSummaryCard.Visibility = progress is not null || state.Action is RuntimeAction.Recover
            ? Visibility.Visible : Visibility.Collapsed;
        if (progress is null || state.Action is RuntimeAction.Recover) {
            RuntimeSidebar.RunProgressPanel.Visibility = Visibility.Collapsed;
            RuntimeSidebar.RunProgressCaption.Text = string.Empty;
            if (state.Action is RuntimeAction.Recover) {
                RuntimeSidebar.RunSummaryCard.SetResourceReference(Border.BackgroundProperty, "Brush.Error.Surface");
                RuntimeSidebar.RunSummaryCard.SetResourceReference(Border.BorderBrushProperty, "Brush.Error.Border");
            }
            return;
        }
        RuntimeSidebar.RunSummaryCard.SetResourceReference(Border.BackgroundProperty,
            progress.Active ? "Brush.Transparent"
                : progress.Failed > 0 ? "Brush.RunSummary.FailureSurface" : "Brush.Surface.Subdued");
        RuntimeSidebar.RunSummaryCard.SetResourceReference(Border.BorderBrushProperty,
            progress.Active ? "Brush.Transparent"
                : progress.Failed > 0 ? "Brush.RunSummary.FailureBorder" : "Brush.Border.Subtle");
        if (state.Action is RuntimeAction.Stop) {
            RuntimeSidebar.RuntimeStatusDetail.Visibility = Visibility.Collapsed;
        }
        RuntimeSidebar.RuntimeStatusTitle.Text = progress.Current?.TaskLabel
            ?? progress.Items.FirstOrDefault(item => item.State == PlanItemProgress.Failed)?.TaskLabel
            ?? (progress.Stopped > 0 ? "任务已停止" : $"{progress.Succeeded} 项全部成功");
        RuntimeSidebar.RunProgressCaption.Text = progress.Active
            ? $"{(progress.CurrentIndex ?? 0) + 1} / {progress.Total}"
            : progress.Failed == 0 && progress.Stopped == 0 && progress.NotRun == 0
                ? $"{progress.EndedAtUtc?.ToLocalTime():HH:mm} 完成"
                : RunProgress.Caption(progress).Replace("上次运行：", "");
        RuntimeSidebar.RunProgressCaption.SetResourceReference(TextBlock.ForegroundProperty, progress.Active ? "Brush.Text.Muted"
            : progress.Failed > 0 ? "Brush.Error.Foreground" : "Brush.Success.Foreground");
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
            RuntimeSidebar.DesktopViewButton.SetResourceReference(WpfControl.BackgroundProperty, "Brush.Warning.Surface");
            RuntimeSidebar.DesktopViewButton.SetResourceReference(WpfControl.ForegroundProperty, "Brush.Warning.Foreground");
        } else {
            RuntimeSidebar.DesktopViewButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            RuntimeSidebar.DesktopViewButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }
        AutomationProperties.SetName(RuntimeSidebar.DesktopViewButton,
            desktop.Action == DesktopAction.Hide ? "隐藏窗口" : "显示窗口");
    }

    private void ApplyMoreMenu(RuntimeControlState state)
    {
        var endBlocker = state.Action is RuntimeAction.Stop ? "请先停止任务" : EndEnvironmentBlocker(state);
        RuntimeSidebar.EndEnvironmentMenuItem.IsEnabled = endBlocker is null;
        RuntimeSidebar.EndEnvironmentMenuItem.InputGestureText = endBlocker ?? string.Empty;
        RuntimeSidebar.EndEnvironmentMenuItem.Tag = endBlocker ?? "注销分身，游戏随之关闭";
    }

    private RunProgressView? CurrentRunProgress => _projectPlan is { } project
        ? RunProgress.Derive(_workerSnapshot.WorkerSnapshot,
            project.EnabledItems
                .Select(item => (item.Kind, item.Name)).ToArray()) : null;

    private enum StatusTone { Idle, Busy, Ok, Run, Bad }

    // Reuse the action chosen by RuntimeControls for the schedule capsule and the panel's supporting text.
    private void ApplyRuntimeStatus(RuntimeControlState state, RunProgressView? progress)
    {
        var planSummary = _projectPlan is { } project ? $"{project.EnabledItems.Count} 项任务待执行" : "项目未加载";
        var current = progress?.Current;
        var (tone, pill, title, detail, warn) = state.Action switch {
            RuntimeAction.Prepare => (StatusTone.Idle, "未准备", "开始任务前需要准备运行环境",
                string.Empty, false),
            RuntimeAction.InProgress { Kind: ProgressKind.PreparingEnvironment } => (StatusTone.Busy, "准备中",
                "正在准备运行环境", PreparationStage(), false),
            RuntimeAction.InProgress { Kind: ProgressKind.StartingRun } => (StatusTone.Busy, "启动中", "正在开始任务",
                "正在把执行计划交给 Worker…", false),
            RuntimeAction.InProgress { Kind: ProgressKind.StoppingRun } => (StatusTone.Busy, "停止中", "正在停止任务",
                "等待当前任务安全结束…", false),
            RuntimeAction.InProgress => (StatusTone.Busy, "处理中", "正在结束桌面分身", "正在关闭分身和其中的程序…", false),
            RuntimeAction.Stop when current is not null => (StatusTone.Run, "运行中",
                $"第 {progress!.CurrentIndex + 1} / {progress.Total} 项：{current.TaskLabel}",
                $"本项已运行 {ElapsedText(current.StartedAtUtc, null)}", false),
            RuntimeAction.Stop => (StatusTone.Run, "运行中", "任务运行中", string.Empty, false),
            RuntimeAction.Start { Blocker: { } blocker } => (StatusTone.Ok, "就绪", planSummary,
                StartBlockerText(blocker), true),
            RuntimeAction.Start => (StatusTone.Ok, "就绪", planSummary,
                string.Empty, false),
            RuntimeAction.Retry { Target: RetryTarget.Prepare } => (StatusTone.Bad, "需重试", "运行环境未就绪",
                "重新准备运行环境后再开始任务", false),
            RuntimeAction.Retry retry => (StatusTone.Bad, "需重试", "上次运行失败",
                retry.Blocker is { } blocker ? StartBlockerText(blocker) : "修正后可以重新开始任务",
                retry.Blocker is not null),
            RuntimeAction.Recover { Reason: var reason } => (StatusTone.Bad, "故障", RecoveryTitle(reason),
                RecoveryText(reason), false),
            _ => (StatusTone.Idle, string.Empty, string.Empty, string.Empty, false)
        };
        ApplyScheduleCapsule(tone, pill);
        Sidebar.ShowRunning(tone == StatusTone.Run);
        RuntimeSidebar.RuntimeStatusTitle.Text = title;
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

    private static string ElapsedText(DateTime? startedAtUtc, DateTime? endedAtUtc) =>
        RunProgress.Elapsed(startedAtUtc, endedAtUtc, DateTime.UtcNow) is { } elapsed
            ? RunProgress.FormatDuration(elapsed) : "00:00";

    // RunProgress already hides a Last Run whose plan does not match the configuration on screen.
    private void ApplyRunProgress(RunProgressView? progress)
    {
        var shown = progress is not null;
        RuntimeSidebar.RunProgressPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (progress is not null) {
            RuntimeSidebar.RunProgressCaption.Text = RunProgress.Caption(progress);
            ApplyRunElapsed(progress);
            AutomationProperties.SetName(RuntimeSidebar.RunProgressBar, RunProgress.AccessibleCaption(progress));
        }
        _displayedProgress = shown ? progress : null;
        DrawRunProgressBar();
        if (progress is { Active: true }) {
            _runTimer.Start();
        } else {
            _runTimer.Stop();
        }
    }

    private RunProgressView? _displayedProgress;

    private void RefreshRunTiming()
    {
        var progress = CurrentRunProgress;
        if (progress is not { Active: true }) {
            return;
        }
        ApplyRunElapsed(progress);
    }

    private void ApplyRunElapsed(RunProgressView progress) => RuntimeSidebar.RunProgressElapsed.Text = progress.Active
        ? $"总用时 {ElapsedText(progress.StartedAtUtc, null)}"
        : $"用时 {ElapsedText(progress.StartedAtUtc, progress.EndedAtUtc)}";

    // Fill covers finished items only; the running item is marked by its position, never by an invented percentage.
    private void DrawRunProgressBar()
    {
        RuntimeSidebar.RunProgressBar.Children.Clear();
        if (_displayedProgress is not { Total: > 0 } progress || RuntimeSidebar.RunProgressBar.ActualWidth <= 0) {
            return;
        }
        var width = RuntimeSidebar.RunProgressBar.ActualWidth;
        var segment = width / progress.Total;
        void Add(double left, double size, string brush)
        {
            var bar = new System.Windows.Shapes.Rectangle {
                Width = Math.Max(0, size), Height = 3
            };
            bar.SetResourceReference(Shape.FillProperty, brush);
            Canvas.SetLeft(bar, left);
            RuntimeSidebar.RunProgressBar.Children.Add(bar);
        }
        for (var item = 0; item < progress.Total; item++) {
            var brush = progress.Items[item].State switch {
                PlanItemProgress.Succeeded => progress.Active ? "Brush.Primary" : "Brush.Success",
                PlanItemProgress.Failed => "Brush.Error",
                PlanItemProgress.Stopped => "Brush.Border.Strong",
                _ => null
            };
            if (brush is not null) {
                Add(segment * item, segment, brush);
            }
        }
        RuntimeSidebar.RunProgressBar.Clip = new RectangleGeometry(new Rect(0, 0, width, 3), 1.5, 1.5);
    }

}
