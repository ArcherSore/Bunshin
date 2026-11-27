using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Gui.ChildSession;
using Gui.Infrastructure;
using Gui.Models;
using ProjectModel;
using Protocol;
using Gui.Settings;
using Gui.Runtime;
using Updates;
using Gui.Worker;
using WpfSymbol = Wpf.Ui.Controls.SymbolRegular;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace Gui.Views;

public partial class MainWindow : FluentWindow, Themes.IThemeWindowBackground
{
    private const int MaximumGuiLogEntries = 1000;
    private HwndSource? _modalWindowSource;

    private readonly AppLogger _logger;
    private readonly Themes.ThemeService _themes;
    private readonly ApplicationSettings _settings;
    private MainSection _section;
    private readonly string _applicationDirectory;
    private readonly Func<ProjectPlanModule, Func<MaaFrameworkOptions>, bool, Task> _prepareEnvironment;
    private CancellationTokenSource? _workerReadyWait;
    private readonly ChildSessionManager _sessionManager;
    private readonly WorkerCoordinator _workerCoordinator;
    private readonly GuiTelemetry _telemetry;
    private readonly RuntimeEnvironment _environment;
    private readonly Func<Task> _requestExitAsync;
    private readonly Func<Task> _endEnvironmentAsync;
    private readonly Func<string, Task> _installUpdateAsync;
    private ChildSessionSnapshot _sessionSnapshot = ChildSessionSnapshot.Empty;
    private WorkerCoordinatorSnapshot _workerSnapshot = WorkerCoordinatorSnapshot.Empty;
    private ProjectPlanModule? _projectPlan;
    private RunKey? _lastStartKey;
    private long _projectVersion;
    private long _editVersion;
    private readonly RunLifecycleHost _lifecycle;
    private readonly RunPresentation _presentation;
    private Guid? _closingId;
    private ScrollViewer? _homeLogScrollViewer;
    private readonly RunLogFeed _runLog;
    private bool _allowClose;
    private bool _environmentPreparationFailed;
    private bool _exitInProgress;
    private bool _followLogs = true;
    private bool _projectConfigurationValid;
    // Only the timing text changes while a Run is active; cards are never rebuilt for it.
    private readonly DispatcherTimer _runTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    internal MainWindow(
        AppLogger logger,
        ChildSessionManager sessionManager, WorkerCoordinator workerCoordinator, RuntimeEnvironment environment,
        Func<Task> requestExitAsync, Func<Task> endEnvironmentAsync, Func<string, Task> installUpdateAsync,
        string? applicationDirectory = null, Func<CancellationToken, Task<EngineCheckResult>>? checkForUpdate = null,
        Func<ProjectPlanModule, Func<MaaFrameworkOptions>, bool, Task>? prepareEnvironment = null,
        Func<string, IProgress<EngineProgress>, CancellationToken, Task<string>>? prepareUpdate = null,
        AutostartTask? autostart = null, LaunchCommand? launchCommand = null,
        GuiTelemetry? telemetry = null, TimeProvider? scheduleClock = null, Action? quitAfterRun = null,
        DispatcherTimer? scheduleTimer = null, RunPresentation? presentation = null,
        Themes.ThemeService? themeService = null)
    {
        _themes = themeService ?? ((App)System.Windows.Application.Current).Themes;
        _launchCommand = launchCommand ?? new LaunchCommand();
        _quitAfterRun = quitAfterRun;
        _presentation = presentation ?? new();
        _scheduleClock = scheduleClock ?? TimeProvider.System;
        _scheduleTimer = scheduleTimer ?? new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        InitializeComponent();
        _runLog = new RunLogFeed(LogLines, MaximumGuiLogEntries);
        DataContext = this;
        _logger = logger;
        _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        _checkForUpdate = checkForUpdate ?? CheckWithEngineAsync;
        _prepareUpdate = prepareUpdate ?? PrepareWithEngineAsync;
        _settings = new ApplicationSettings(_applicationDirectory, logger,
            CheckUpdateFromSettingsAsync, ExportDiagnosticsFromSettingsAsync, ReplayOnboarding, OpenAfdianAsync,
            ManageChildSessionCredentialsAsync, ClearDebugFilesAsync, ViewWelcome, OpenTaskDescriptionLink,
            ReadLicense, autostart, _themes.Apply);
        InitializeShell();
        InitializeOnboarding();
        InitializeRuntimePanel();
        SettingsView.DataContext = _settings.Page;
        HelpView.DataContext = new HelpPageModel();
        ScheduleView.Attach(() => _projectPlan, ScheduleInvalidReason, HandleOperationError, _scheduleClock,
            Path.Combine(_applicationDirectory, "config", "schedule-share-hint.txt"));
        TaskPlanEditor.Attach(logger, () => _projectPlan, () => CurrentControls.ConfigurationEditable,
            HandleOperationError, OpenTaskDescriptionLink);
        TaskPlanEditor.PlanEdited += (_, _) => {
            _lastStartKey = null;
            _editVersion++;
            RefreshSidebarSchedule();
        };
        TaskPlanEditor.ConfigurationValidated += (_, valid) => {
            _projectConfigurationValid = valid;
            RefreshRuntimeControls();
        };
        TaskPlanEditor.ConfigurationsRendered += (_, project) => {
            _settings.UpdateAutoRunConfigurations(AutoRunTarget.Of(project), !project.ConfigurationsUnreadable);
            RefreshShellTitles();
        };
        TaskPlanEditor.StartRequested += StartPlanButton_Click;
        TaskPlanEditor.StopRequested += StopRunButton_Click;
        _savedRuntimeSidebarWidth = _preferredRuntimeSidebarWidth = LoadSidebarWidth(RuntimeSidebarWidthFile, "运行面板");
        _sessionManager = sessionManager;
        _workerCoordinator = workerCoordinator;
        _workerCoordinator.FrameworkOptions = _settings.CreateFrameworkOptions;
        _telemetry = telemetry ?? new GuiTelemetry();
        _workerCoordinator.TelemetryEnabled = () => _settings.HelpImprove.Value;
        _workerCoordinator.TelemetryOptions = TelemetryOptionsFor;
        _settings.FrameworkOptionsChanged += OnFrameworkOptionsChanged;
        // A surviving Worker may have connected before the choice above was available.
        OnFrameworkOptionsChanged(_settings.SaveDraw);
        ClearDebugFilesOnStartup();
        _environment = environment;
        _lifecycle = new(workerCoordinator, Dispatcher, () => ActiveConfigurationName, PresentRunAction, RequestQuitAfterRun, logger, scheduleClock);
        _lifecycle.Changed += RefreshRuntimeControls;
        _lifecycle.ScheduledCompleted += terminal => ScheduledRunCompleted?.Invoke(this, terminal);
        _lifecycle.ScheduledCleanupCompleted += () => AddScheduleActivity("全部完成，已关闭桌面分身");
        _lifecycle.CleanupPending += EndScheduledRunAsync;
        _environment.Ended += _lifecycle.EnvironmentRecovered;
        _environment.PendingLifecycleStart = view => _lifecycle.ReadOrchestration().PendingStart(view)?.RunId;
        _prepareEnvironment = prepareEnvironment ?? environment.PrepareAsync;
        _requestExitAsync = requestExitAsync;
        _endEnvironmentAsync = endEnvironmentAsync;
        _installUpdateAsync = installUpdateAsync;
        _sessionSnapshot = sessionManager.Snapshot;
        RuntimeSidebar.HomeLogListBox.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(LogListBox_ScrollChanged));
        _sessionManager.StateChanged += OnSessionStateChanged;
        _workerCoordinator.StateChanged += OnWorkerStateChanged;
        _environment.StateChanged += OnEnvironmentStateChanged;
        _workerCoordinator.LogReceived += OnWorkerLogReceived;
        _workerSnapshot = workerCoordinator.Snapshot;
        _runLog.Observe(_workerSnapshot.WorkerSnapshot);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        InitializeWindowPrompts();
        InitializeForcedStartDialog();
        KeyDown += MainWindow_KeyDown;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        IsVisibleChanged += MainWindow_IsVisibleChanged;
        Activated += (_, _) => _ = CheckScheduledRunsAsync();
        StateChanged += MainWindow_StateChanged;
        HomeView.SizeChanged += (_, _) => RuntimeSidebarVisibility(_section);
        _runTimer.Tick += (_, _) => RefreshRunTiming();
        SwitchSection(MainSection.Home);
        RefreshRuntimeControls();
    }

    // Run logs newest first, each stretch from one Plan Item headed by its RunLogGroup.
    public ObservableCollection<object> LogLines { get; } = [];

    internal event EventHandler? HiddenToTray;

    internal void AllowClose() => _allowClose = true;

    internal void SetExitInProgress(bool exitInProgress)
    {
        if (exitInProgress && _closingId == null) {
            _closingId = Guid.NewGuid();
            _lifecycle.Apply(new LifecycleEvent.ClosingStarted(_closingId.Value));
        }
        _exitInProgress = exitInProgress;
        if (exitInProgress) {
            _exitGeneration++;
            CancelWindowConfirmations();
            _forcedStartCountdown?.TrySetResult(false);
            _workerReadyWait?.Cancel();
            EndOnboarding(handled: false);
        }
        UpdateUpdaterControls();
        RefreshRuntimeControls();
        if (!exitInProgress && _closingId is Guid id) {
            _closingId = null;
            _lifecycle.Apply(new LifecycleEvent.ClosingCancelled(id));
            RefreshWindowPrompts();
        }
    }

    internal void CommitExit()
    {
        if (_closingId is Guid id) _lifecycle.Apply(new LifecycleEvent.ClosingCommitted(id));
        CloseWindowPrompts();
        RefreshWindowPrompts();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await InitializeStartupAsync(RestoreExistingSessionAsync);
        // Deliver launch on the dispatcher like the independent clock signal. The first arrival gets the gate.
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Normal);
        if (_allowClose || _exitInProgress) {
            return;
        }
        try {
            // An auto-run prepares the environment itself, so the preparation below would only repeat it.
            if (await AutoRunOnLaunchAsync()) {
                return;
            }
            if (_settings.AutoStartDesktop.Value && CurrentControls.Action is RuntimeAction.Prepare { Enabled: true }) {
                await PrepareFromPanelAsync();
            }
        } catch (Exception exception) {
            _logger.Warn("启动时自动执行或启动分身失败。", exception);
        }
    }

    private async Task InitializeStartupAsync(Func<Task> restoreExistingSession)
    {
        _homeLogScrollViewer = FindVisualChild<ScrollViewer>(RuntimeSidebar.HomeLogListBox);
        try {
            LoadProject();
        } catch (Exception exception) {
            _logger.Warn(
                $"加载 Project Interface 失败。AppContext.BaseDirectory={AppContext.BaseDirectory}", exception);
            ShowProjectUnavailableState(
                "项目无法加载",
                $"请使用完整发布包，确保 {AppIdentity.Name}.exe 同级目录直接包含 interface.json。");
            TaskPlanEditor.ShowProjectValidationError(exception);
            RefreshRuntimeControls();
        }
        ShowWelcomeIfChanged();
        InitializeUpdates();
        try {
            var restore = restoreExistingSession();
            StartScheduleClock();
            await restore;
        } finally {
            _onboardingStartupCompleted = true;
            ReevaluateOnboarding();
        }
    }

    private async Task RestoreExistingSessionAsync()
    {
        var existingId = _sessionManager.DetectExistingSession();
        if (existingId is null) {
            return;
        }
        await RunOperationAsync(
            PendingOperation.RestoringSession,
            async () =>
            {
                await _sessionManager.EnsureConnectedAsync(showPreview: false);
                _logger.Info($"已有 Child Session {existingId} 恢复完成。");
            },
            unattended: _launchCommand.AutoStart);
    }

    // WPF-UI assigns a local Background when it updates the backdrop, including on HWND creation.
    void Themes.IThemeWindowBackground.RestoreBackground() => SetResourceReference(BackgroundProperty, "Brush.Canvas");

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Themes.ThemeService.InitializeWindow(this);
        var source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(_themes.SystemThemeHook);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && IsGlobalModalOpen) {
            e.Cancel = true;
            return;
        }
        if (_allowClose) {
            _lifecycle.Dispose();
            CloseWindowPrompts();
            _environment.PendingLifecycleStart = null;
            _onboardingClosed = true;
            EndOnboarding(handled: false);
            RemoveModalWindowHook();
            _runTimer.Stop();
            _scheduleTimer.Stop();
            _forcedStartTimer.Stop();
            _forcedStartCountdown?.TrySetResult(false);
            _updateCancellation?.Cancel();
            _sessionManager.StateChanged -= OnSessionStateChanged;
            _workerCoordinator.StateChanged -= OnWorkerStateChanged;
            _environment.StateChanged -= OnEnvironmentStateChanged;
            _environment.Ended -= _lifecycle.EnvironmentRecovered;
            _workerCoordinator.LogReceived -= OnWorkerLogReceived;
            IsVisibleChanged -= MainWindow_IsVisibleChanged;
            StateChanged -= MainWindow_StateChanged;
            return;
        }

        e.Cancel = true;
        if (_exitInProgress) { return; }
        if (!_settings.CloseToTray.Value) {
            // Defer until Closing returns; the exit flow may synchronously call Close again.
            Dispatcher.InvokeAsync(async () => await _requestExitAsync());
            return;
        }
        Hide();
        _logger.Info("主窗口已隐藏到托盘。");
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    private void SwitchSection(MainSection section)
    {
        CancelSidebarDrag();
        CancelRuntimeSidebarDrag();
        _section = section;
        HomeView.Visibility = section == MainSection.Home
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateRuntimePanelLayout();
        HelpView.Visibility = section == MainSection.Help ? Visibility.Visible : Visibility.Collapsed;
        ScheduleView.Visibility = section == MainSection.Schedule ? Visibility.Visible : Visibility.Collapsed;

        Sidebar.MarkActive(section);
        RefreshShellTitles();
        ReevaluateOnboarding();
    }

    private async void ShowSessionButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(
            PendingOperation.ShowingDesktop,
            async () => await _sessionManager.EnsureConnectedAsync(showPreview: true));

    private void HideSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_exitInProgress) {
            return;
        }

        try {
            _sessionManager.HidePreview();
        } catch (Exception exception) {
            HandleOperationError("隐藏子桌面失败", exception);
        }
    }

    private void TryOpenDirectory(string directory, string directoryName)
    {
        try {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true
            });
        } catch (Exception exception) {
            var title = $"打开{directoryName}失败";
            _logger.Error($"{title}。", exception);
            ShowActionableError(
                title,
                exception,
                $"请确认 Windows 资源管理器可用，并检查{directoryName}访问权限后重试。",
                offerLogDirectory: false);
        }
    }

    private void DesktopViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingActive || CurrentControls.Desktop is not { Enabled: true } desktop) {
            return;
        }
        if (desktop.Action == DesktopAction.Hide) {
            HideSessionButton_Click(sender, e);
        } else {
            ShowSessionButton_Click(sender, e);
        }
    }

    // The same routine as the tray's "结束桌面分身", including its confirmation.
    private async void EndEnvironmentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (EndEnvironmentBlocker(CurrentControls) is null) {
            await _endEnvironmentAsync();
        }
    }

    // Why the desktop clone cannot be ended from the menu now, or null when it can.
    internal static string? EndEnvironmentBlocker(RuntimeControlState state) =>
        state.Action is RuntimeAction.Recover { Enabled: true } ? null
        : !state.CommandsAvailable ? "处理中"
        : !state.Desktop.Enabled ? "未创建" : null;

    private void StartPlanButton_Click(object sender, RoutedEventArgs e) => StartPlan(continueRun: false);

    // Starts exactly as 运行 does, from the record's first unsuccessful item.
    private void ContinueRunButton_Click(object sender, RoutedEventArgs e) => StartPlan(continueRun: true);

    private void StartPlan(bool continueRun)
    {
        switch (CurrentControls.Action) {
            case RuntimeAction.Prepare { Enabled: true } when PlanStartBlocker(CurrentControls.Action) is null:
            case RuntimeAction.Retry { Target: RetryTarget.Prepare, Enabled: true }:
                PrepareAndRun(continueRun);
                break;
            case RuntimeAction.Start { Enabled: true }:
            case RuntimeAction.Retry { Target: RetryTarget.Start, Enabled: true }:
                StartRun(continueRun);
                break;
            default:
                LogIgnoredAction();
                break;
        }
    }

    private StartBlocker? PlanStartBlocker(RuntimeAction action) => action switch {
        RuntimeAction.Start start => start.Blocker,
        RuntimeAction.Retry retry => retry.Blocker,
        RuntimeAction.Prepare { Enabled: false } => StartBlocker.CommandsUnavailable,
        RuntimeAction.Prepare when _projectPlan?.EnabledItems.Count is null or 0 => StartBlocker.NoTasks,
        RuntimeAction.Prepare when !_projectConfigurationValid => StartBlocker.ConfigurationInvalid,
        _ => null
    };

    private void PrepareAndRunButton_Click(object sender, RoutedEventArgs e) => PrepareAndRun(continueRun: false);

    private async void PrepareAndRun(bool continueRun)
    {
        if (!TaskPlanEditor.CommitRunInput() || TaskPlanEditor.RevealInvalidParameter() || OperationOrExitInProgress
            || !TryReadContinuation(continueRun, out var tail)) {
            LogIgnoredAction();
            return;
        }
        await PrepareFromPanelAsync(startRun: true, tail: tail);
    }

    // Read on the click, before preparation can replace the Worker and its record. Passed along rather than kept, so
    // automatic runs and 重试准备运行环境 always run the whole plan.
    private bool TryReadContinuation(bool continueRun,
        out IReadOnlyList<(PlanItemKind Kind, string Name)>? tail)
    {
        tail = continueRun ? CurrentContinuation() : null;
        return !continueRun || tail is not null;
    }

    private async void PrepareEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        await PrepareFromPanelAsync(showDesktop: true);
    }

    // Only a manual "仅启动分身" shows the Child Session window; every other preparation keeps it hidden.
    private async Task PrepareFromPanelAsync(bool startRun = false, bool unattended = false, bool showDesktop = false,
        IReadOnlyList<(PlanItemKind Kind, string Name)>? tail = null)
    {
        if (OperationOrExitInProgress) {
            LogIgnoredAction();
            return;
        }
        await RunOperationAsync(PendingOperation.PreparingEnvironment,
            () => PrepareCurrentEnvironmentAsync(startRun, showDesktop, tail: tail), unattended, startsRun: startRun);
    }

    private async Task PrepareCurrentEnvironmentAsync(bool startRun, bool showDesktop = false,
        RunSource source = RunSource.Manual, IReadOnlyList<(PlanItemKind Kind, string Name)>? tail = null)
    {
        await PrepareProjectEnvironmentAsync(project: null, showDesktop);
        if (startRun && !_exitInProgress && !_environment.Closing && await WaitForRunStartAsync()) {
            await StartCurrentPlanAsync(source, PendingOperation.PreparingEnvironment, tail);
        }
    }

    private async Task PrepareProjectEnvironmentAsync(ProjectPlanModule? project, bool showDesktop)
    {
        _environmentPreparationFailed = false;
        try {
            if (project is null) {
                LoadProject();
                project = RequireProject();
            }
            await _prepareEnvironment(project, _settings.CreateFrameworkOptions, showDesktop);
        } catch {
            _environmentPreparationFailed = true;
            throw;
        }
    }

    private async Task<bool> WaitForRunStartAsync(bool scheduled = false)
    {
        _logger.Info("等待 Worker 依赖检查完成后自动开始当前任务计划。");
        using var readyWait = new CancellationTokenSource();
        _workerReadyWait = readyWait;
        try {
            await _workerCoordinator.WaitForReadyAsync(readyWait.Token);
        } finally {
            _workerReadyWait = null;
        }
        if (_exitInProgress || _environment.Closing) {
            return false;
        }
        SetPendingOperation(PendingOperation.StartingRun, scheduled);
        return true;
    }

    // RuntimeControls chooses what Retry means; this handler only carries it out.
    private void RetryRuntimeHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentControls.Action is RuntimeAction.Retry { Target: RetryTarget.Start }) {
            StartRunButton_Click(sender, e);
        } else {
            PrepareAndRunButton_Click(sender, e);
        }
    }

    // The same routine as the tray's "结束桌面分身", including its confirmation.
    private async void RecoverEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentControls.Action is RuntimeAction.Recover { Enabled: true }) {
            await _endEnvironmentAsync();
        } else {
            LogIgnoredAction();
        }
    }

    private void StartRunButton_Click(object sender, RoutedEventArgs e) => StartRun(continueRun: false);

    private async void StartRun(bool continueRun)
    {
        if (!TaskPlanEditor.CommitRunInput() || TaskPlanEditor.RevealInvalidParameter()
            || CurrentControls.Action is not (RuntimeAction.Start { Enabled: true }
                or RuntimeAction.Retry { Target: RetryTarget.Start, Enabled: true })
            || !TryReadContinuation(continueRun, out var tail)) {
            LogIgnoredAction();
            return;
        }
        await RunOperationAsync(PendingOperation.StartingRun, () => StartCurrentPlanAsync(tail: tail), startsRun: true);
    }

    // Called only while the application operation gate is held, after preparation or a direct Start. A tail is
    // the end of the plan 继续运行 covers.
    private async Task StartCurrentPlanAsync(RunSource source = RunSource.Manual,
        PendingOperation operation = PendingOperation.StartingRun,
        IReadOnlyList<(PlanItemKind Kind, string Name)>? tail = null)
    {
        // The operation may wait behind another one, so the session is checked again when it runs.
        if (_sessionManager.Snapshot.State != ChildSessionState.Connected) {
            throw new InvalidOperationException("Child Session 尚未连接，当前不能开始任务。 ");
        }
        var project = RequireProject();
        if (!_projectConfigurationValid) {
            throw new InvalidOperationException(
                "当前 任务配置尚未通过正式 PI Resolver 校验。 ");
        }
        var view = _workerCoordinator.ReadPublishedSnapshot();
        var orchestration = _lifecycle.ReadOrchestration();
        if (orchestration.PendingQuitReason != null || orchestration.ManualErrorPending) return;
        var worker = view.WorkerSnapshot ?? throw new InvalidOperationException("Worker 快照已失效。");
        if (orchestration.PendingStart(view) != null || _exitInProgress || _environment.Closing) return;
        if (_environment.ManualEndPending) {
            if (source == RunSource.Manual) ReportSkippedOperation(PendingOperation.StartingRun, ManualEndReason);
            return;
        }

        var intent = CreateRunIntent(source, project.ActiveConfigurationId, ActiveConfigurationName, operation);
        // A plan that did not start is reused only by the next 运行 of the same input; 继续运行 covers a different part
        // of the plan, so it neither reuses one nor leaves its own to be reused.
        var retry = intent with { RetryOf = _lastStartKey };
        var attempt = tail is null ? orchestration.Lifecycle.ReusableAttempt(retry, worker.WorkerInstanceId) : null;
        if (attempt != null) intent = retry;
        else attempt = CreateConfigurationAttempt(project, project.ActiveConfigurationId, tail);
        var key = new RunKey(worker.WorkerInstanceId, attempt.RunId);
        _lastStartKey = tail is null ? key : null;
        await SubmitRunAsync(intent, key, attempt);
    }

    private RunIntent CreateRunIntent(RunSource source, Guid configurationId, string name,
        PendingOperation operation = PendingOperation.StartingRun) =>
        new(Guid.NewGuid(), source, OperationLabel(operation) + "失败",
            new(configurationId, _projectVersion, _editVersion), name,
            QuitAfterRun: source == RunSource.Launch && _launchCommand.QuitAfterRun);

    private Task SubmitRunAsync(RunIntent intent, RunKey key, RunStartAttempt attempt)
    {
        RunResultLog.WritePlan(_logger, attempt, intent.ConfigurationName);
        _telemetry.Register(attempt.RedactionValues);
        return _lifecycle.SubmitAsync(intent, key, attempt);
    }

    private async void StopRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentControls.Action is not RuntimeAction.Stop { Enabled: true }
            || _workerSnapshot.WorkerSnapshot is not { ActiveRun: { } selected } shown) {
            LogIgnoredAction();
            return;
        }
        await RunOperationAsync(
            PendingOperation.StoppingRun,
            async () =>
            {
                var current = _workerCoordinator.ReadPublishedSnapshot();
                if (current is not { Fresh: true, WorkerSnapshot: { ActiveRun: { } activeRun, WorkerState: not WorkerState.Faulted } worker }
                    || worker.WorkerInstanceId != shown.WorkerInstanceId || activeRun.RunId != selected.RunId
                    || !RuntimeControls.IsStoppable(activeRun)) {
                    LogIgnoredAction();
                    return;
                }
                var response = await _workerCoordinator.StopRunAsync(activeRun.RunId);
                if (response.Disposition != "stop_requested") {
                    throw new InvalidDataException(
                        $"首次 run.stop 未返回 stop_requested：{response.Disposition}。 ");
                }
                _logger.Info($"run.stop 已确认 stop_requested：runId={activeRun.RunId:D}。 ");
            });
    }

    private void MainWindow_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (PromptOverlay.Visibility == Visibility.Visible) {
            e.Handled = HandleWindowPromptKey(e);
            return;
        }
        if (!IsOnboardingVisible && !IsGlobalModalOpen && !SettingsOpen && HandleSidebarShortcut(e)) {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ForcedStartOverlay.Visibility == Visibility.Visible) {
            e.Handled = true;
            return;
        }
        if (IsOnboardingVisible) {
            if (e.Key == Key.Escape) {
                EndOnboarding(handled: true);
                e.Handled = true;
            } else if (!(e.Key == Key.System && e.SystemKey == Key.F4)
                && e.Key is not (Key.Tab or Key.Enter or Key.Space)) {
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Escape && UpdateOverlay.Visibility == Visibility.Visible) {
            CloseUpdate_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ProjectReader.Visibility == Visibility.Visible) {
            CloseProjectReader();
            e.Handled = true;
            return;
        }
    }

    // Esc first clears the search, then closes the panel, wherever focus is. It arrives here only when no editor
    // used it, so reverting a parameter draft or cancelling a rename keeps its own Esc.
    private void MainWindow_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Escape) {
            return;
        }
        if (SettingsOpen && !IsGlobalModalOpen) {
            CloseSettings();
            e.Handled = true;
        } else if (!IsGlobalModalOpen && HomeView.Visibility == Visibility.Visible && TaskPlanEditor.DismissTaskShelf()) {
            e.Handled = true;
        }
    }

    private void MainWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!IsGlobalModalOpen && !IsOnboardingVisible && HomeView.Visibility == Visibility.Visible) {
            TaskPlanEditor.DismissTaskShelfOnClick();
        }
    }

    private Task OpenAfdianAsync()
    {
        var address = ProjectProfile.SponsorUrl;
        try {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            _settings.OpenAfdian.Status = string.Empty;
        } catch (Exception exception) {
            _settings.OpenAfdian.Status = "无法打开爱发电，请检查默认浏览器设置。";
            _logger.Warn("打开爱发电赞助页面失败。", exception);
            return QueueWindowPrompt(new("无法打开爱发电",
                $"请手动访问：{address}\n\n按 Ctrl+C 可复制此提示中的网址。", CopyText: address));
        }
        return Task.CompletedTask;
    }

    private void OpenTaskDescriptionLink(Uri uri)
    {
        try {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        } catch (Exception exception) {
            _logger.Warn("打开任务说明链接失败。", exception);
            ShowActionableError("无法打开链接", exception, "请检查默认浏览器设置。", offerLogDirectory: false);
        }
    }

    // A control can stay clickable for a moment after its state changes; "nothing happened" is then still in the log.
    private void LogIgnoredAction([CallerMemberName] string action = "") => _logger.Debug(
        $"操作未执行{AppLogger.Field("action", action)}{AppLogger.Field("current", CurrentControls.Action)}"
        + $"{AppLogger.Field("pending", CurrentOperation)}{AppLogger.Field("exiting", _exitInProgress)}");

    private const string ManualEndReason = "用户正在结束桌面分身";

    private void ReportSkippedOperation(PendingOperation operation, string reason) =>
        AddActivityEntry(new(_scheduleClock.GetLocalNow(), LogLevel.Info, $"跳过{OperationLabel(operation)}：{reason}"));

    // An unattended operation (started by the launch itself) reports its failure without a dialog nobody can answer.
    private async Task RunOperationAsync(PendingOperation pending, Func<Task> operation, bool unattended = false, bool startsRun = false,
        [CallerMemberName] string caller = "")
    {
        if (OperationOrExitInProgress) {
            LogIgnoredAction(caller);
            return;
        }

        async Task RunInGateAsync()
        {
            if (startsRun || pending == PendingOperation.PreparingEnvironment) {
                var view = _lifecycle.ReadOrchestration();
                if (view.PendingQuitReason != null || view.ManualErrorPending) return;
                if (_environment.EndUnconfirmed) {
                    ReportSkippedOperation(pending, "运行环境状态未知");
                    return;
                }
            }
            await operation();
        }
        await RunWindowOperationAsync(pending, async () => {
            if (!startsRun) await _environment.RunExclusiveAsync(RunInGateAsync);
            else if (_forcedStartsInProgress != 0 || !await _environment.TryRunExclusiveAsync(RunInGateAsync)) {
                if (_environment.ManualEndPending) ReportSkippedOperation(pending, ManualEndReason);
                else if (_forcedStartsInProgress != 0) ReportSkippedOperation(pending, "正在运行其他任务");
                else LogIgnoredAction(caller);
            }
        }, unattended);
    }

    private async Task RunWindowOperationAsync(PendingOperation pending, Func<Task> operation, bool unattended)
    {
        using var scope = _logger.Scope(OperationLabel(pending));
        SetPendingOperation(pending);
        try {
            await operation();
        } catch (OperationCanceledException) {
            _logger.Warn($"操作已取消：正在{OperationLabel(pending)}...");
        } catch (Exception exception) when (unattended) {
            ReportUnattendedFailure($"{OperationLabel(pending)}失败", exception);
        } catch (Exception exception) {
            await PresentOperationErrorAsync($"{OperationLabel(pending)}失败", exception);
        } finally {
            SetPendingOperation(PendingOperation.None);
        }
    }

    // One label per operation feeds its progress text, cancellation log and failure dialog title.
    private static string OperationLabel(PendingOperation operation) => operation switch {
        PendingOperation.RestoringSession => "恢复已有桌面分身",
        PendingOperation.ShowingDesktop => "显示子桌面",
        PendingOperation.PreparingEnvironment => "准备运行环境",
        PendingOperation.StartingRun => "开始任务",
        PendingOperation.StoppingRun => "停止任务",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private void HandleOperationError(string operation, Exception exception) =>
        _ = PresentOperationErrorAsync(operation, exception);

    private Task<bool> PresentOperationErrorAsync(string operation, Exception exception, string? detail = null)
    {
        _logger.Error($"{operation}。", exception);
        var helloGuidance = CanShowWindowPrompt
            ? GetWindowsHelloGuidance(operation, exception, _presentation.RequiresWindowsHello()) : null;
        var message = helloGuidance ?? $"{detail ?? exception.GetBaseException().Message}\n\n{GetRecoveryGuidance(operation)}";
        return ShowErrorPromptAsync(operation, message, exception, offerLogs: true);
    }

    internal static string? GetWindowsHelloGuidance(string operation, Exception exception, bool windowsHelloOnly)
    {
        if (!windowsHelloOnly || exception.GetBaseException() is not ChildSessionConnectionException { ConnectionNotEstablished: false }
            || !operation.Contains("运行环境", StringComparison.Ordinal)) {
            return null;
        }
        return "桌面分身登录失败。检测到已开启「仅允许对此设备上的 Microsoft 帐户使用 Windows Hello 登录」。"
            + "\n\n1. 打开 Windows「设置 → 帐户 → 登录选项」，关闭该开关。"
            + "\n2. 用帐户密码登录一次 Windows，然后重试准备运行环境。"
            + "\n\n详细错误已记录到日志。";
    }

    private static string GetRecoveryGuidance(string operation)
    {
        if (operation.Contains("桌面分身", StringComparison.Ordinal)
            || operation.Contains("子桌面", StringComparison.Ordinal)) {
            return "请确认程序以管理员权限运行，并检查桌面分身状态后重试。";
        }

        // The task may already be running, so these two results must not invite a second start.
        if (operation is "启动结果未知" or "运行结果未确认") {
            return "请稍后查看任务状态，确认没有在运行后再开始。";
        }

        return "请检查当前配置和系统状态后重试。";
    }

    private void LoadProject()
    {
        var configPath = Path.Combine(_applicationDirectory, "config", "configurations.json");
        LegacyConfigFile.Migrate(configPath);
        if (_onboardingPreferences is null) {
            _onboardingPreferences = new OnboardingPreferences(Path.GetDirectoryName(configPath)!, _logger);
            _onboardingPreferences.CaptureBeforeProjectLoad(configPath);
        }
        var previous = _projectPlan;
        var project = ProjectPlanModule.Open(_applicationDirectory, configPath,
            message => _logger.Info(message));
        _projectPlan = project;
        _settings.ViewWelcome.IsVisible = project.HasWelcome;
        ShowProjectIdentity(project);
        _lastStartKey = null;
        _projectVersion++;
        var expanded = project.InitializedTaskName
            ?? (_onboardingPreferences.ShouldOfferAutomatically && !_onboardingAutoEnded
                ? project.SelectedTaskNames.FirstOrDefault() : null);
        TaskPlanEditor.ShowProject(expanded is null ? null : (PlanItemKind.MaaTask, expanded));
        if (project.LoadWarning is not null) {
            _logger.Warn(project.LoadWarning);
        }
        // Every prepare reloads the project; the log only needs it the first time and when it has changed.
        if (previous?.SourceInterfaceDigest != project.SourceInterfaceDigest
            || previous?.RuntimeProfileDigest != project.RuntimeProfileDigest) {
            _logger.Info(
                $"已加载 Project Interface：{project.ProjectName} {project.ProjectVersion}；"
                + $"interfaceDigest={project.SourceInterfaceDigest}；"
                + $"runtimeProfileDigest={project.RuntimeProfileDigest}。");
        }
    }

    private ProjectPlanModule RequireProject() =>
        _projectPlan ?? throw new InvalidOperationException("项目尚未加载。");

    private void ShowProjectUnavailableState(
        string emptyStateTitle,
        string emptyStateDetail)
    {
        _projectPlan = null;
        RefreshShellTitles();
        _settings.ViewWelcome.IsVisible = false;
        ShowProjectIdentity(null);
        _lastStartKey = null;
        _projectVersion++;
        _projectConfigurationValid = false;
        TaskPlanEditor.ShowProjectUnavailable(emptyStateTitle, emptyStateDetail);
    }

    internal static bool IsUserFacingRunLog(WorkerLogEntry entry) =>
        string.Equals(entry.Source, ProtocolConstants.RunLogSource, StringComparison.Ordinal);

    // The task label comes from the Run Plan the Worker reports, falling back to the name the log carries.
    internal static LogEntry? CreateUserFacingRunLogEntry(WorkerLogEntry workerEntry, WorkerSnapshot? snapshot = null)
    {
        if (!IsUserFacingRunLog(workerEntry)) {
            return null;
        }

        var timestampUtc = DateTime.SpecifyKind(workerEntry.TimestampUtc, DateTimeKind.Utc);
        string? label = null;
        if (workerEntry.PlanItemId is { } planItemId) {
            label = new[] { snapshot?.ActiveRun, snapshot?.LastRun }.OfType<RunSnapshot>()
                .SelectMany(run => run.Plan.Items).FirstOrDefault(item => item.PlanItemId == planItemId)?.TaskLabel;
            label = string.IsNullOrWhiteSpace(label) ? workerEntry.TaskName : label;
        }
        return new LogEntry(
            new DateTimeOffset(timestampUtc).ToLocalTime(),
            ParseWorkerLogLevel(workerEntry.Level),
            workerEntry.Message, workerEntry.RunId, workerEntry.PlanItemId,
            string.IsNullOrWhiteSpace(label) ? null : label);
    }

    internal static void WriteWorkerDiagnosticLog(AppLogger logger, WorkerLogEntry entry) =>
        logger.WriteWorker(
            ParseWorkerLogLevel(entry.Level), $"#{entry.Sequence} [{entry.Source}] {entry.Message}",
            new DateTimeOffset(DateTime.SpecifyKind(entry.TimestampUtc, DateTimeKind.Utc)));

    private void AddRunLogEntry(WorkerLogEntry workerEntry)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => AddRunLogEntry(workerEntry));
            return;
        }

        var entry = CreateUserFacingRunLogEntry(workerEntry, _workerSnapshot.WorkerSnapshot);
        if (entry is null) {
            return;
        }
        AddActivityEntry(entry);
    }

    private void AddActivityEntry(LogEntry entry)
    {
        var shouldFollow = _followLogs && IsLogNearTop();
        var previousOffset = _homeLogScrollViewer?.VerticalOffset ?? 0;
        var inserted = _runLog.Add(entry);
        if (!shouldFollow && _runLog.EntryCount > 1) {
            _homeLogScrollViewer?.ScrollToVerticalOffset(previousOffset + inserted);
        }

        if (shouldFollow) {
            _ = Dispatcher.BeginInvoke(ScrollLogsToLatest, DispatcherPriority.Background);
            return;
        }

        _followLogs = false;
    }

    private void OnWorkerStateChanged(object? sender, WorkerCoordinatorSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => OnWorkerStateChanged(sender, snapshot));
            return;
        }

        // A change raised on another thread runs here late, possibly after a newer one raised on this thread, so the
        // coordinator's current snapshot is applied rather than the one the event carried.
        _workerSnapshot = _workerCoordinator.Snapshot;
        _runLog.Observe(_workerSnapshot.WorkerSnapshot);
        RefreshRuntimeControls();
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RefreshWindowPrompts();
        ReevaluateOnboarding();
        if (IsVisible) {
            _ = CheckScheduledRunsAsync();
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        RefreshWindowPrompts();
        ReevaluateOnboarding();
    }

    private bool IsGlobalModalOpen => UpdateOverlay.Visibility == Visibility.Visible
        || ProjectReader.Visibility == Visibility.Visible || ForcedStartOverlay.Visibility == Visibility.Visible
        || PromptOverlay.Visibility == Visibility.Visible;

    private void EnsureModalWindowHook()
    {
        if (_modalWindowSource is null && PresentationSource.FromVisual(MainWindowContent) is HwndSource source) {
            _modalWindowSource = source;
            source.AddHook(ModalWindowHook);
        }
    }

    private void RemoveModalWindowHook()
    {
        // The countdown can sit above another dialog; the hook stays until the last one closes.
        if ((IsGlobalModalOpen || SettingsOpen) && !_allowClose) {
            return;
        }
        _modalWindowSource?.RemoveHook(ModalWindowHook);
        _modalWindowSource = null;
    }

    private nint ModalWindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!IsGlobalModalOpen && !SettingsOpen) {
            return 0;
        }
        // WPF-UI caption buttons handle native messages independently of WPF overlay hit testing.
        const int wmNcHitTest = 0x0084;
        const int wmNcLeftButtonDown = 0x00A1;
        const int wmNcLeftButtonUp = 0x00A2;
        const int wmNcLeftButtonDoubleClick = 0x00A3;
        const int wmSysCommand = 0x0112;
        if (message == wmNcHitTest) {
            handled = true;
            return 1; // HTCLIENT routes caption-area clicks to the dismiss scrim.
        }
        var command = (int)(wParam.ToInt64() & 0xFFF0);
        if (message is wmNcLeftButtonDown or wmNcLeftButtonUp or wmNcLeftButtonDoubleClick
            || message == wmSysCommand && command is 0xF020 or 0xF030 or 0xF060 or 0xF120) {
            handled = true;
        }
        return 0;
    }

    private void OnWorkerLogReceived(object? sender, WorkerLogEntry entry)
    {
        WriteWorkerDiagnosticLog(_logger, entry);

        if (IsUserFacingRunLog(entry)) {
            AddRunLogEntry(entry);
        }
    }

    private static LogLevel ParseWorkerLogLevel(string level) => level.ToLowerInvariant() switch {
        "critical" => LogLevel.Critical,
        "error" => LogLevel.Error,
        "warning" or "warn" => LogLevel.Warn,
        "debug" => LogLevel.Debug,
        _ => LogLevel.Info
    };

    private void LogListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer scrollViewer) {
            _homeLogScrollViewer = scrollViewer;
        }

        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0) {
            _followLogs = IsLogNearTop();
        }
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e)
    {
        _runLog.Clear();
        _followLogs = true;
    }

    private bool IsLogNearTop()
    {
        _homeLogScrollViewer ??= FindVisualChild<ScrollViewer>(RuntimeSidebar.HomeLogListBox);
        return _homeLogScrollViewer is null || _homeLogScrollViewer.VerticalOffset <= 0.1;
    }

    private void ScrollLogsToLatest()
    {
        if (_followLogs && LogLines.FirstOrDefault() is { } latestHeading) {
            RuntimeSidebar.HomeLogListBox.ScrollIntoView(latestHeading);
        }
    }

    private void OnSessionStateChanged(object? sender, ChildSessionSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => OnSessionStateChanged(sender, snapshot));
            return;
        }

        _sessionSnapshot = snapshot;
        RefreshRuntimeControls();
    }

    // Raised by whichever entry ends the Runtime Environment, so the tray's end shows here as well.
    private void OnEnvironmentStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => OnEnvironmentStateChanged(sender, e));
            return;
        }

        RefreshRuntimeControls();
    }

    // Another operation is running or the application is exiting, so a new operation must not start.
    private bool OperationOrExitInProgress => CurrentOperation != PendingOperation.None || _exitInProgress;

    internal PendingOperation CurrentOperation => _lifecycle.Orchestration.Operation;

    internal void SetPendingOperation(PendingOperation operation, bool scheduled = false)
    {
        _lifecycle.SetOperation(operation, scheduled);
        Mouse.OverrideCursor = operation != PendingOperation.None ? System.Windows.Input.Cursors.Wait : null;
        RefreshRuntimeControls();
    }

    // Derived fresh on every read, so a click handler never acts on a decision the window no longer shows.
    private RuntimeControlState CurrentControls => RuntimeControls.Derive(new RuntimeControlInputs(
        _sessionSnapshot, _workerSnapshot, _projectPlan is { } project
            ? new ProjectReadiness(true, project.EnabledItems.Count, _projectConfigurationValid,
                project.RuntimeProfileDigest)
            : ProjectReadiness.NotLoaded,
        CurrentOperation == PendingOperation.None && _lifecycle.Orchestration.PendingStart(_workerSnapshot) != null
            && _workerSnapshot.Fresh
            ? PendingOperation.StartingRun : CurrentOperation,
        _environmentPreparationFailed, _exitInProgress, _environment.Ending,
        _environment.EndUnconfirmed));

    // The only refresh path for runtime inputs.
    private void RefreshRuntimeControls()
    {
        ReevaluateOnboarding();
        ApplyRuntimeControls(CurrentControls);
    }

    private string ActiveConfigurationName => _projectPlan is { } project
        ? project.Configurations.Single(item => item.Id == project.ActiveConfigurationId).Name : string.Empty;

    private void ShowActionableError(string title, Exception exception, string recovery, bool offerLogDirectory) =>
        _ = ShowErrorPromptAsync(title, $"{exception.GetBaseException().Message}\n\n{recovery}", exception, offerLogDirectory);

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) {
                return result;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) {
                return descendant;
            }
        }

        return null;
    }
}
