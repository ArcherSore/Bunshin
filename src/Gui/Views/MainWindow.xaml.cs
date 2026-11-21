using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
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
using WpfBrush = System.Windows.Media.Brush;
using WpfSymbol = Wpf.Ui.Controls.SymbolRegular;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMessageBox = System.Windows.MessageBox;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace Gui.Views;

public partial class MainWindow : FluentWindow
{
    private const int MaximumGuiLogEntries = 1000;
    private HwndSource? _modalWindowSource;

    private enum MainSection
    {
        Home,
        Schedule,
        Help,
        Settings
    }

    private readonly AppLogger _logger;
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
    private PendingOperation _pendingOperation;
    private bool _environmentPreparationFailed;
    private bool _exitInProgress;
    private bool _followLogs = true;
    private bool _projectConfigurationValid;
    private double? _preferredRuntimeSidebarWidth;
    private double? _savedRuntimeSidebarWidth;
    private bool _updatingRuntimeSidebarWidth;
    private bool _runtimeSidebarDragging;
    private bool _runtimeSidebarKeyboardCue;
    private static readonly DependencyPropertyDescriptor RuntimeSidebarWidthProperty =
        DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition));
    private IInputElement? _descriptionDrawerPreviousFocus;
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
        DispatcherTimer? scheduleTimer = null, RunPresentation? presentation = null)
    {
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
            ReadLicense, autostart);
        SettingsView.DataContext = _settings.Page;
        HelpView.DataContext = new HelpPageModel();
        ScheduleView.Attach(() => _projectPlan, ScheduleInvalidReason, HandleOperationError, _scheduleClock,
            Path.Combine(_applicationDirectory, "config", "schedule-share-hint.txt"));
        ScheduleView.HomeRequested += (_, _) => SwitchSection(MainSection.Home);
        TaskPlanEditor.Attach(logger, () => _projectPlan, () => CurrentControls.ConfigurationEditable,
            () => CurrentControls.ConfigurationLock, HandleOperationError);
        TaskPlanEditor.PlanEdited += (_, _) => {
            _lastStartKey = null;
            _editVersion++;
        };
        TaskPlanEditor.ConfigurationValidated += (_, valid) => {
            _projectConfigurationValid = valid;
            RefreshRuntimeControls();
        };
        TaskPlanEditor.ConfigurationsRendered += (_, project) => _settings.UpdateAutoRunConfigurations(
            AutoRunTarget.Of(project), !project.ConfigurationsUnreadable);
        TaskPlanEditor.TaskDescriptionRequested += (_, task) => OpenTaskDescriptionDrawer(task);
        TaskPlanEditor.TaskDescriptionCloseRequested += (_, _) => CloseTaskDescriptionDrawer();
        _savedRuntimeSidebarWidth = _preferredRuntimeSidebarWidth = LoadRuntimeSidebarWidth();
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
        _lifecycle = new(workerCoordinator, Dispatcher, () => ActiveConfigurationName, DeliverRunAction, logger);
        _lifecycle.Changed += RefreshRuntimeControls;
        _environment.PendingLifecycleStart = view => _lifecycle.PendingStart(view)?.RunId;
        _prepareEnvironment = prepareEnvironment ?? environment.PrepareAsync;
        _requestExitAsync = requestExitAsync;
        _endEnvironmentAsync = endEnvironmentAsync;
        _installUpdateAsync = installUpdateAsync;
        _sessionSnapshot = sessionManager.Snapshot;
        HomeLogListBox.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(LogListBox_ScrollChanged));
        _sessionManager.StateChanged += OnSessionStateChanged;
        _workerCoordinator.StateChanged += OnWorkerStateChanged;
        _environment.StateChanged += OnEnvironmentStateChanged;
        _workerCoordinator.LogReceived += OnWorkerLogReceived;
        _workerSnapshot = workerCoordinator.Snapshot;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        InitializeForcedStartDialog();
        KeyDown += MainWindow_KeyDown;
        IsVisibleChanged += MainWindow_IsVisibleChanged;
        Activated += (_, _) => _ = CheckScheduledRunsAsync();
        StateChanged += MainWindow_StateChanged;
        SizeChanged += (_, _) => TaskPlanEditor.UpdateConfigurationLockText();
        RuntimeSidebarWidthProperty.AddValueChanged(RuntimeSidebarColumn, RuntimeSidebarColumn_WidthChanged);
        foreach (var routedEvent in new[] {
            MouseEnterEvent, MouseLeaveEvent, MouseMoveEvent, GotKeyboardFocusEvent, LostKeyboardFocusEvent,
            PreviewKeyDownEvent, SizeChangedEvent, System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            System.Windows.Controls.Primitives.Thumb.DragCompletedEvent }) {
            RuntimeSidebarSplitter.AddHandler(routedEvent, new RoutedEventHandler(RuntimeSidebarHandle_Update), true);
        }
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
            _forcedStartCountdown?.TrySetResult(false);
            _workerReadyWait?.Cancel();
            EndOnboarding(handled: false);
        }
        UpdateUpdaterControls();
        RefreshRuntimeControls();
        if (!exitInProgress && _closingId is Guid id) {
            _closingId = null;
            _lifecycle.Apply(new LifecycleEvent.ClosingCancelled(id));
            ResumeScheduledCleanup();
        }
    }

    internal void CommitExit()
    {
        if (_closingId is Guid id) _lifecycle.Apply(new LifecycleEvent.ClosingCommitted(id));
    }

    internal void EnvironmentRecovered() => _lifecycle.Apply(new LifecycleEvent.EnvironmentRecovered());

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
            AutoSessionMenuItem.IsChecked = File.Exists(AutoSessionPath)
                && File.ReadAllText(AutoSessionPath).Trim() == "true";
            // An auto-run prepares the environment itself, so the preparation below would only repeat it.
            if (await AutoRunOnLaunchAsync()) {
                return;
            }
            if (AutoSessionMenuItem.IsChecked && CurrentControls.Action is RuntimeAction.Prepare { Enabled: true }) {
                await PrepareFromPanelAsync();
            }
        } catch (Exception exception) {
            _logger.Warn("读取自动启动分身设置失败。", exception);
        }
    }

    private async Task InitializeStartupAsync(Func<Task> restoreExistingSession)
    {
        _homeLogScrollViewer = FindVisualChild<ScrollViewer>(HomeLogListBox);
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TaskPlanEditor.AddConfigurationWheelHook(PresentationSource.FromVisual(this) as HwndSource);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && IsGlobalModalOpen) {
            e.Cancel = true;
            return;
        }
        if (_allowClose) {
            _lifecycle.Dispose();
            _environment.PendingLifecycleStart = null;
            _onboardingClosed = true;
            EndOnboarding(handled: false);
            RemoveModalWindowHook();
            TaskPlanEditor.RemoveConfigurationWheelHook();
            _runTimer.Stop();
            _scheduleTimer.Stop();
            _forcedStartTimer.Stop();
            _forcedStartCountdown?.TrySetResult(false);
            RuntimeSidebarWidthProperty.RemoveValueChanged(RuntimeSidebarColumn, RuntimeSidebarColumn_WidthChanged);
            _updateCancellation?.Cancel();
            _sessionManager.StateChanged -= OnSessionStateChanged;
            _workerCoordinator.StateChanged -= OnWorkerStateChanged;
            _environment.StateChanged -= OnEnvironmentStateChanged;
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

    private void NavigationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RailItem { Tag: string sectionName }
            && Enum.TryParse(sectionName, ignoreCase: false, out MainSection section)) {
            SwitchSection(section);
        }
    }

    private void SwitchSection(MainSection section)
    {
        _section = section;
        CloseTaskDescriptionDrawer();
        HomeView.Visibility = section == MainSection.Home
            ? Visibility.Visible
            : Visibility.Collapsed;
        RuntimeSidebarVisibility(section);
        SettingsView.Visibility = section == MainSection.Settings
            ? Visibility.Visible
            : Visibility.Collapsed;
        HelpView.Visibility = section == MainSection.Help ? Visibility.Visible : Visibility.Collapsed;
        ScheduleView.Visibility = section == MainSection.Schedule ? Visibility.Visible : Visibility.Collapsed;
        if (section == MainSection.Settings) {
            RefreshChildSessionCredentialStatus();
            RefreshDebugFileUsage();
        }

        HomeNavigationItem.IsActive = section == MainSection.Home;
        ScheduleNavigationItem.IsActive = section == MainSection.Schedule;
        HelpNavigationItem.IsActive = section == MainSection.Help;
        SettingsNavigationItem.IsActive = section == MainSection.Settings;
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

    private void TryOpenLogsDirectory()
    {
        try {
            Directory.CreateDirectory(_logger.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "explorer.exe",
                Arguments = $"\"{_logger.LogDirectory}\"",
                UseShellExecute = true
            });
        } catch (Exception exception) {
            _logger.Error("打开日志目录失败。", exception);
            ShowActionableError(
                "打开日志目录失败",
                exception,
                "请确认 Windows 资源管理器可用，并检查日志目录访问权限后重试。",
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

    private void RuntimeSidebarVisibility(MainSection section)
    {
        var visible = section == MainSection.Home;
        RuntimeSidebar.Visibility = RuntimeSidebarSplitter.Visibility = visible
            ? Visibility.Visible : Visibility.Collapsed;
        _updatingRuntimeSidebarWidth = true;
        try {
            // ActualWidth can exceed the allotted space when the columns' minimum sizes no longer fit.
            var homeWidth = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(HomeView).Width;
            var minimum = visible ? 340 + RuntimeSidebar.Margin.Right : 0;
            var maximum = visible ? Math.Max(minimum, Math.Min(540 + RuntimeSidebar.Margin.Right,
                homeWidth - HomeView.ColumnDefinitions[0].MinWidth)) : 0;
            RuntimeSidebarColumn.MinWidth = minimum;
            RuntimeSidebarColumn.MaxWidth = maximum;
            var preferred = _preferredRuntimeSidebarWidth
                ?? SidebarWidth(homeWidth) + RuntimeSidebar.Margin.Right;
            RuntimeSidebarColumn.Width = new GridLength(Math.Clamp(preferred, minimum, maximum));
        } finally {
            _updatingRuntimeSidebarWidth = false;
        }
    }

    private void RuntimeSidebarColumn_WidthChanged(object? sender, EventArgs e)
    {
        // Observe native mouse, keyboard and automation resizing; automatic layout never replaces the preference.
        if (!_updatingRuntimeSidebarWidth) {
            _preferredRuntimeSidebarWidth = RuntimeSidebarColumn.Width.Value;
        }
    }

    // The GridSplitter does all resizing; this only places its handle and picks the visual state.
    private void RuntimeSidebarHandle_Update(object sender, RoutedEventArgs e)
    {
        var splitter = RuntimeSidebarSplitter;
        if (e.RoutedEvent == System.Windows.Controls.Primitives.Thumb.DragStartedEvent) {
            _runtimeSidebarDragging = true;
            _runtimeSidebarKeyboardCue = false;
        } else if (e.RoutedEvent == System.Windows.Controls.Primitives.Thumb.DragCompletedEvent) {
            _runtimeSidebarDragging = false;
            SaveRuntimeSidebarWidth();
        } else if (e is KeyboardEventArgs) {
            if (e.RoutedEvent == LostKeyboardFocusEvent) {
                // Arrow-key resizing has no drag end; leaving the splitter finishes it.
                SaveRuntimeSidebarWidth();
            }
            // Like a focus rectangle, focus shows the handle only when it came from the keyboard.
            _runtimeSidebarKeyboardCue = e.RoutedEvent != LostKeyboardFocusEvent
                && InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        }
        var pointer = _runtimeSidebarDragging || splitter.IsMouseOver;
        var keyboard = _runtimeSidebarKeyboardCue && splitter.IsKeyboardFocused;
        if ((pointer || keyboard) && splitter.Template.FindName("Handle", splitter) is FrameworkElement handle
            && splitter.Template.FindName("Tether", splitter) is FrameworkElement tether) {
            // Follow the pointer, kept 24 DIP from the console's ends so it clears the rounded corners.
            var y = Math.Clamp(pointer ? Mouse.GetPosition(splitter).Y : splitter.ActualHeight / 2,
                24, Math.Max(24, splitter.ActualHeight - 24));
            handle.Margin = new Thickness(0, y - handle.Height / 2, 0, 0);
            // The edge segment's clip starts 10 DIP down, below the corner radius.
            tether.Margin = new Thickness(0, y - 10 - tether.Height / 2, 0, 0);
        }
        VisualStateManager.GoToState(splitter, _runtimeSidebarDragging ? "Dragging"
            : splitter.IsMouseOver ? "Hover" : keyboard ? "Keyboard" : "Hidden", true);
    }

    private string RuntimeSidebarWidthPath =>
        Path.Combine(_applicationDirectory, "config", "runtime-sidebar-width.txt");

    // The saved width is the user's preference; layout still clamps it to the space each window size allows.
    private double? LoadRuntimeSidebarWidth()
    {
        try {
            if (!File.Exists(RuntimeSidebarWidthPath)) {
                return null;
            }
            var text = File.ReadAllText(RuntimeSidebarWidthPath).Trim();
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var width)
                || !double.IsFinite(width) || width <= 0) {
                throw new InvalidDataException($"侧栏宽度必须为正数：{text}");
            }
            return width;
        } catch (Exception exception) {
            _logger.Warn("读取运行控制栏宽度失败，已使用默认宽度。", exception);
            return null;
        }
    }

    private void SaveRuntimeSidebarWidth()
    {
        if (_preferredRuntimeSidebarWidth is not { } width || width == _savedRuntimeSidebarWidth) {
            return;
        }
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimeSidebarWidthPath)!);
            File.WriteAllText(RuntimeSidebarWidthPath,
                width.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            _savedRuntimeSidebarWidth = width;
        } catch (Exception exception) {
            _logger.Warn("保存运行控制栏宽度失败。", exception);
        }
    }

    // The console takes about a third of the home page, bounded so neither it nor the task area gets cramped.
    internal static double SidebarWidth(double homeWidth) => Math.Clamp(Math.Round(homeWidth * 0.35), 340, 540);

    private void RuntimeMoreButton_Click(object sender, RoutedEventArgs e)
    {
        RuntimeMoreMenu.PlacementTarget = RuntimeMoreButton;
        RuntimeMoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        RuntimeMoreMenu.IsOpen = true;
    }

    private void OpenLogsMenuItem_Click(object sender, RoutedEventArgs e) => TryOpenLogsDirectory();

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

    private string AutoSessionPath => Path.Combine(_applicationDirectory, "config", "auto-session.txt");

    private void AutoSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(AutoSessionPath)!);
            File.WriteAllText(AutoSessionPath, AutoSessionMenuItem.IsChecked ? "true" : "false");
            _settings.LogToggleChanged("application.autoSession", AutoSessionMenuItem.IsChecked);
        } catch (Exception exception) {
            _logger.Warn("保存自动启动分身设置失败。", exception);
            AutoSessionMenuItem.IsChecked = !AutoSessionMenuItem.IsChecked;
            ShowActionableError("保存自动启动分身设置失败", exception, "请检查配置目录写入权限。", offerLogDirectory: false);
        }
    }

    private async void PrepareAndRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TaskPlanEditor.CommitRunInput() || TaskPlanEditor.RevealInvalidParameter() || OperationOrExitInProgress) {
            LogIgnoredAction();
            return;
        }
        await PrepareFromPanelAsync(startRun: true);
    }

    private async void PrepareEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        await PrepareFromPanelAsync(showDesktop: true);
    }

    // Only a manual "仅启动分身" shows the Child Session window; every other preparation keeps it hidden.
    private async Task PrepareFromPanelAsync(
        bool startRun = false, bool unattended = false, bool showDesktop = false)
    {
        if (OperationOrExitInProgress) {
            LogIgnoredAction();
            return;
        }
        await RunOperationAsync(PendingOperation.PreparingEnvironment,
            () => PrepareCurrentEnvironmentAsync(startRun, showDesktop), unattended);
    }

    private async Task PrepareCurrentEnvironmentAsync(bool startRun, bool showDesktop = false,
        RunSource source = RunSource.Manual)
    {
        await PrepareProjectEnvironmentAsync(project: null, showDesktop);
        if (startRun && !_exitInProgress && !_environment.Closing && await WaitForRunStartAsync()) {
            await StartCurrentPlanAsync(source, PendingOperation.PreparingEnvironment);
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

    private async Task<bool> WaitForRunStartAsync()
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
        SetPendingOperation(PendingOperation.StartingRun);
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

    private async void StartRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TaskPlanEditor.CommitRunInput() || TaskPlanEditor.RevealInvalidParameter()
            || CurrentControls.Action is not (RuntimeAction.Start { Enabled: true }
                or RuntimeAction.Retry { Target: RetryTarget.Start, Enabled: true })) {
            LogIgnoredAction();
            return;
        }
        await RunOperationAsync(PendingOperation.StartingRun, () => StartCurrentPlanAsync());
    }

    // Called only while the application operation gate is held, after preparation or a direct Start.
    private async Task StartCurrentPlanAsync(RunSource source = RunSource.Manual,
        PendingOperation operation = PendingOperation.StartingRun)
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
        _lifecycle.Drain();
        var view = _workerCoordinator.Snapshot;
        var worker = view.WorkerSnapshot ?? throw new InvalidOperationException("Worker 快照已失效。");
        if (_lifecycle.PendingStart(view) != null || _lifecycle.State.Quits.Values.Contains(QuitStage.Requested)
            || _exitInProgress || _environment.Closing) return;

        var intent = CreateRunIntent(source, project.ActiveConfigurationId, ActiveConfigurationName, operation);
        var retry = intent with { RetryOf = _lastStartKey };
        var attempt = _lifecycle.State.ReusableAttempt(retry, worker.WorkerInstanceId);
        if (attempt != null) intent = retry;
        else attempt = CreateConfigurationAttempt(project, project.ActiveConfigurationId);
        var key = new RunKey(worker.WorkerInstanceId, attempt.RunId);
        _lastStartKey = key;
        await SubmitRunAsync(intent, key, attempt);
    }

    private RunIntent CreateRunIntent(RunSource source, Guid configurationId, string name,
        PendingOperation operation = PendingOperation.StartingRun) =>
        new(Guid.NewGuid(), source, OperationLabel(operation) + "失败",
            new(configurationId, _projectVersion, _editVersion), name, Foreground: source == RunSource.Manual,
            QuitAfterRun: source == RunSource.Launch && _launchCommand.QuitAfterRun);

    private Task SubmitRunAsync(RunIntent intent, RunKey key, RunStartAttempt attempt)
    {
        RunResultLog.WritePlan(_logger, attempt, intent.ConfigurationName);
        _telemetry.Register(attempt.RedactionValues);
        return _lifecycle.SubmitAsync(intent, key, attempt);
    }

    private async void StopRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentControls.Action is not RuntimeAction.Stop { Enabled: true }) {
            LogIgnoredAction();
            return;
        }
        await RunOperationAsync(
            PendingOperation.StoppingRun,
            async () =>
            {
                var activeRun = _workerSnapshot.WorkerSnapshot?.ActiveRun
                                ?? throw new InvalidOperationException("Worker 当前没有 active Run。 ");
                if (!RuntimeControls.IsStoppable(activeRun)) {
                    throw new InvalidOperationException("当前执行计划尚未进入可停止状态。 ");
                }
                var response = await _workerCoordinator.StopRunAsync(activeRun.RunId);
                if (response.Disposition != "stop_requested") {
                    throw new InvalidDataException(
                        $"首次 run.stop 未返回 stop_requested：{response.Disposition}。 ");
                }
                _manuallyStoppedRun = activeRun.RunId;
                _logger.Info($"run.stop 已确认 stop_requested：runId={activeRun.RunId:D}。 ");
            });
    }

    private void TaskDescriptionCloseButton_Click(object sender, RoutedEventArgs e) => CloseTaskDescriptionDrawer();

    private void TaskDescriptionScrim_Click(object sender, RoutedEventArgs e) => CloseTaskDescriptionDrawer();

    private void MainWindow_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
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
        if (e.Key == Key.Escape && WelcomeOverlay.Visibility == Visibility.Visible) {
            CloseWelcome();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && TaskDescriptionOverlay.Visibility == Visibility.Visible) {
            CloseTaskDescriptionDrawer();
            e.Handled = true;
        }
    }

    private void OpenTaskDescriptionDrawer(ProjectTaskChoice task)
    {
        if (_onboardingActive) {
            return;
        }
        _descriptionDrawerPreviousFocus = Keyboard.FocusedElement;
        TaskDescriptionDrawerTitle.Text = task.Label;
        TaskDescriptionViewer.Document = MarkdownDocument.Create(task.Description, OpenTaskDescriptionLink);
        FindVisualChild<ScrollViewer>(TaskDescriptionViewer)?.ScrollToTop();
        TaskDescriptionOverlay.Visibility = Visibility.Visible;
        TaskDescriptionCloseButton.Focus();
    }

    private void CloseTaskDescriptionDrawer()
    {
        if (TaskDescriptionOverlay.Visibility != Visibility.Visible) {
            return;
        }
        TaskDescriptionOverlay.Visibility = Visibility.Collapsed;
        if (_descriptionDrawerPreviousFocus is FrameworkElement element) {
            if (!element.IsVisible && element.Tag is ProjectTaskChoice task
                && TaskPlanEditor.PlanItemCard(task) is { } card) {
                card.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            if (element.IsVisible) {
                element.Focus();
            }
        }
        _descriptionDrawerPreviousFocus = null;
        ReevaluateOnboarding();
    }

    // Esc first clears the search, then closes the panel, wherever focus is. It arrives here only when no editor
    // used it, so reverting a parameter draft or cancelling a rename keeps its own Esc.
    private void MainWindow_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape && HomeView.Visibility == Visibility.Visible && TaskPlanEditor.DismissTaskShelf()) {
            e.Handled = true;
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
            WpfMessageBox.Show(this, $"请手动访问：{address}\n\n按 Ctrl+C 可复制此提示中的网址。",
                "无法打开爱发电", MessageBoxButton.OK, MessageBoxImage.Information);
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
        + $"{AppLogger.Field("pending", _pendingOperation)}{AppLogger.Field("exiting", _exitInProgress)}");

    // An unattended operation (started by the launch itself) reports its failure without a dialog nobody can answer.
    private async Task RunOperationAsync(PendingOperation pending, Func<Task> operation, bool unattended = false,
        [CallerMemberName] string caller = "")
    {
        if (OperationOrExitInProgress) {
            LogIgnoredAction(caller);
            return;
        }

        if (pending is PendingOperation.StartingRun or PendingOperation.PreparingEnvironment) {
            await RunWindowOperationAsync(pending, async () => {
                if (!await _environment.TryRunExclusiveAsync(operation)) {
                    LogIgnoredAction(caller);
                }
            }, unattended);
        } else {
            await RunWindowOperationAsync(pending, () => _environment.RunExclusiveAsync(operation), unattended);
        }
        ResumeScheduledCleanup();
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
            HandleOperationError($"{OperationLabel(pending)}失败", exception);
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
        PresentOperationError(operation, exception);

    private void PresentOperationError(string operation, Exception exception, string? detail = null)
    {
        _logger.Error($"{operation}。", exception);
        var helloGuidance = GetWindowsHelloGuidance(
            operation, exception, _presentation.RequiresWindowsHello());
        if (helloGuidance is not null) {
            _presentation.ShowError(this, operation, exception, helloGuidance, MessageBoxButton.OK);
            return;
        }
        ShowActionableError(operation, exception, GetRecoveryGuidance(operation), offerLogDirectory: true, detail);
    }

    internal static string? GetWindowsHelloGuidance(string operation, Exception exception, bool windowsHelloOnly)
    {
        if (!windowsHelloOnly || exception.GetBaseException() is not ChildSessionConnectionException
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
        CloseTaskDescriptionDrawer();
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
        _settings.ViewWelcome.IsVisible = false;
        ShowProjectIdentity(null);
        _lastStartKey = null;
        _projectVersion++;
        _projectConfigurationValid = false;
        TaskPlanEditor.ShowProjectUnavailable(emptyStateTitle, emptyStateDetail);
        CloseTaskDescriptionDrawer();
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
        UpdateRunLogCount();
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
        RefreshRuntimeControls();
        ResumeScheduledCleanup();
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ReevaluateOnboarding();
        if (IsVisible) {
            _ = CheckScheduledRunsAsync();
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e) => ReevaluateOnboarding();

    private bool IsGlobalModalOpen => UpdateOverlay.Visibility == Visibility.Visible
        || WelcomeOverlay.Visibility == Visibility.Visible || ForcedStartOverlay.Visibility == Visibility.Visible;

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
        if (IsGlobalModalOpen && !_allowClose) {
            return;
        }
        _modalWindowSource?.RemoveHook(ModalWindowHook);
        _modalWindowSource = null;
    }

    private nint ModalWindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!IsGlobalModalOpen) {
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
        UpdateRunLogCount();
        _followLogs = true;
    }

    private void UpdateRunLogCount() =>
        RunLogCountText.Text = _runLog.EntryCount == 0 ? string.Empty : $"{_runLog.EntryCount} 条";

    private bool IsLogNearTop()
    {
        _homeLogScrollViewer ??= FindVisualChild<ScrollViewer>(HomeLogListBox);
        return _homeLogScrollViewer is null || _homeLogScrollViewer.VerticalOffset <= 0.1;
    }

    private void ScrollLogsToLatest()
    {
        if (_followLogs && LogLines.FirstOrDefault() is { } latestHeading) {
            HomeLogListBox.ScrollIntoView(latestHeading);
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
    private bool OperationOrExitInProgress => _pendingOperation != PendingOperation.None || _exitInProgress;

    private void SetPendingOperation(PendingOperation operation)
    {
        _pendingOperation = operation;
        Mouse.OverrideCursor = operation != PendingOperation.None ? System.Windows.Input.Cursors.Wait : null;
        RefreshRuntimeControls();
    }

    // Derived fresh on every read, so a click handler never acts on a decision the window no longer shows.
    private RuntimeControlState CurrentControls => RuntimeControls.Derive(new RuntimeControlInputs(
        _sessionSnapshot, _workerSnapshot, _projectPlan is { } project
            ? new ProjectReadiness(true, project.EnabledItems.Count, _projectConfigurationValid,
                project.RuntimeProfileDigest)
            : ProjectReadiness.NotLoaded,
        _pendingOperation == PendingOperation.None && _lifecycle.PendingStart(_workerSnapshot) != null
            && _workerSnapshot.Fresh
            ? PendingOperation.StartingRun : _pendingOperation,
        _environmentPreparationFailed, _exitInProgress, _environment.Ending,
        _environment.EndUnconfirmed));

    // The only refresh path for runtime inputs.
    private void RefreshRuntimeControls()
    {
        ReevaluateOnboarding();
        ApplyRuntimeControls(CurrentControls);
    }

    internal void ApplyRuntimeControls(RuntimeControlState state)
    {
        ApplyDesktopToggle(state.Desktop);

        var action = state.Action;
        PrepareEnvironmentButton.Visibility = Visible(action is RuntimeAction.Prepare);
        PrepareEnvironmentButton.IsEnabled = action is RuntimeAction.Prepare { Enabled: true };
        RetryEnvironmentButton.Visibility = Visible(action is RuntimeAction.Retry);
        RetryEnvironmentButton.IsEnabled = action is RuntimeAction.Retry { Enabled: true };
        StartTaskHeaderButton.Visibility = Visible(action is RuntimeAction.Start);
        StartTaskHeaderButton.IsEnabled = action is RuntimeAction.Start { Enabled: true };
        Explain(StartTaskHeaderButton, (action as RuntimeAction.Start)?.Blocker);
        Explain(RetryEnvironmentButton, (action as RuntimeAction.Retry)?.Blocker);
        if (action is RuntimeAction.Retry { Target: var retryTarget }) {
            var retryText = retryTarget == RetryTarget.Prepare ? "重试准备运行环境" : "重试开始任务";
            RetryEnvironmentText.Text = retryText;
            AutomationProperties.SetName(RetryEnvironmentButton, retryText);
        }
        StopTaskHeaderButton.Visibility = Visible(action is RuntimeAction.Stop);
        StopTaskHeaderButton.IsEnabled = action is RuntimeAction.Stop { Enabled: true };
        RecoverEnvironmentButton.Visibility = Visible(action is RuntimeAction.Recover);
        RecoverEnvironmentButton.IsEnabled = action is RuntimeAction.Recover { Enabled: true };
        if (action is RuntimeAction.Recover { Reason: var recovery }) {
            var reason = RecoveryText(recovery);
            RecoverEnvironmentButton.ToolTip = reason;
            AutomationProperties.SetHelpText(RecoverEnvironmentButton, reason);
        }
        RuntimeHeaderProgressRing.Visibility = Visible(action is RuntimeAction.InProgress);
        if (action is RuntimeAction.InProgress { Kind: var kind }) {
            var operation = kind switch {
                ProgressKind.StoppingRun => PendingOperation.StoppingRun,
                ProgressKind.StartingRun => PendingOperation.StartingRun,
                _ => PendingOperation.PreparingEnvironment
            };
            var progressText = kind == ProgressKind.EndingEnvironment
                ? "正在结束桌面分身" : $"正在{OperationLabel(operation)}";
            AutomationProperties.SetName(RuntimeHeaderProgressRing, progressText);
            RuntimeProgressText.Text = $"{progressText}…";
        }
        ApplyMoreMenu(state);
        var progress = CurrentRunProgress;
        ApplyRuntimeStatus(state, progress);
        ApplyRunProgress(progress);
        ApplyPanelSummary(state, progress);

        TaskPlanEditor.ApplyConfigurationLock(state.ConfigurationEditable, state.ConfigurationLock);

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
        RuntimeStatusText.Text = running ? "已启动" : state.Desktop.Status switch {
            DesktopStatus.Connecting => "启动中",
            DesktopStatus.Ending => "正在结束",
            DesktopStatus.Disconnected => "已断开",
            _ => "未启动"
        };
        RuntimeStatusPill.Background = System.Windows.Media.Brushes.Transparent;
        RuntimeStatusText.Foreground = (WpfBrush)FindResource("Brush.Text.Secondary");
        SessionStatusDot.Fill = running ? (WpfBrush)FindResource("Brush.Success")
            : System.Windows.Media.Brushes.Transparent;
        SessionStatusDot.Stroke = running ? null : (WpfBrush)FindResource("Brush.Border.Strong");
        SessionStatusDot.StrokeThickness = running ? 0 : 1.5;
        AutomationProperties.SetName(RuntimeStatusPill, $"桌面分身：{RuntimeStatusText.Text}");
        DesktopViewText.Text = state.Desktop.Action == DesktopAction.Hide ? "隐藏窗口" : "显示窗口";
        DesktopViewButton.ToolTip = state.Desktop.Action == DesktopAction.Hide
            ? "隐藏窗口，分身和任务继续运行" : "显示分身窗口，默认只读";
        StartSessionMenuItem.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StartSessionMenuItem.IsEnabled = state.Action is RuntimeAction.Prepare { Enabled: true };
        RuntimeStatusPill.ToolTip = state.Desktop.Status == DesktopStatus.Connecting ? PreparationStage() : null;
        EndEnvironmentMenuItem.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        SessionMenuSeparator.Visibility = StartSessionMenuItem.Visibility == Visibility.Visible || running
            ? Visibility.Visible : Visibility.Collapsed;
        if (state.Action is RuntimeAction.InProgress { Kind: var busyKind }) {
            RuntimeProgressText.Text = busyKind == ProgressKind.StoppingRun ? "正在停止…"
                : busyKind == ProgressKind.EndingEnvironment ? "正在结束…" : "启动中…";
        }
        RunSummaryCard.Visibility = progress is not null || state.Action is RuntimeAction.Recover
            ? Visibility.Visible : Visibility.Collapsed;
        if (progress is null || state.Action is RuntimeAction.Recover) {
            return;
        }
        RunSummaryCard.Background = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                progress.Failed > 0 ? "#FEF4F5" : "#F6F8FB"));
        RunSummaryCard.BorderBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                progress.Failed > 0 ? "#F2D0D4" : "#E4EAF0"));
        RuntimeStatusDetail.Visibility = Visibility.Collapsed;
        RuntimeStatusTitle.Text = progress.Current?.TaskLabel
            ?? progress.Items.FirstOrDefault(item => item.State == PlanItemProgress.Failed)?.TaskLabel
            ?? (progress.Stopped > 0 ? "任务已停止" : $"{progress.Succeeded} 项全部成功");
        RunProgressCaption.Text = progress.Active
            ? $"{(state.Action is RuntimeAction.InProgress { Kind: ProgressKind.StoppingRun } ? "停止中" : "运行中")}"
                + $" · 第 {(progress.CurrentIndex ?? 0) + 1} / {progress.Total} 项"
            : progress.Failed == 0 && progress.Stopped == 0 && progress.NotRun == 0
                ? $"{progress.EndedAtUtc?.ToLocalTime():HH:mm} 完成"
                : RunProgress.Caption(progress).Replace("上次运行：", "");
        RunProgressCaption.Foreground = (WpfBrush)FindResource(progress.Active ? "Brush.Primary"
            : progress.Failed > 0 ? "Brush.Error.Foreground" : "Brush.Success");
        RunProgressCaption.FontWeight = FontWeights.SemiBold;
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

    // The glyph and dot report the Child Session and the fill marks an open viewer; it deliberately has no tooltip.
    // The dot takes the status pill's tone colours, so both agree on what amber, green, blue and red mean.
    private void ApplyDesktopToggle(DesktopToggle desktop)
    {
        DesktopViewIcon.Data = Geometry.Parse(desktop.Action == DesktopAction.Hide
            ? "M2.6 10s2.7-5 7.4-5c1.5 0 2.8.5 3.9 1.2M17.4 10s-2.7 5-7.4 5c-1.5 0-2.8-.5-3.9-1.2M3.5 16.5l13-13"
            : "M2.6 10s2.7-5 7.4-5 7.4 5 7.4 5-2.7 5-7.4 5-7.4-5-7.4-5Z"
                + "M12.3 10A2.3 2.3 0 1 1 7.7 10A2.3 2.3 0 1 1 12.3 10");
        DesktopViewButton.IsEnabled = desktop.Enabled;
        if (desktop.Status == DesktopStatus.Interactive) {
            DesktopViewButton.Background = (WpfBrush)FindResource("Brush.Warning.Surface");
            DesktopViewButton.Foreground = (WpfBrush)FindResource("Brush.Warning.Foreground");
        } else {
            DesktopViewButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            DesktopViewButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }
        AutomationProperties.SetName(DesktopViewButton,
            desktop.Action == DesktopAction.Hide ? "隐藏窗口" : "显示窗口");
    }

    private void ApplyMoreMenu(RuntimeControlState state)
    {
        var endBlocker = state.Action is RuntimeAction.Stop ? "请先停止任务" : EndEnvironmentBlocker(state);
        EndEnvironmentMenuItem.IsEnabled = endBlocker is null;
        EndEnvironmentMenuItem.InputGestureText = endBlocker ?? string.Empty;
        EndEnvironmentMenuItem.Tag = endBlocker ?? "注销分身，游戏随之关闭";
    }

    private RunProgressView? CurrentRunProgress => _projectPlan is { } project
        ? RunProgress.Derive(_workerSnapshot.WorkerSnapshot,
            project.EnabledItems
                .Select(item => (item.Kind, item.Name)).ToArray()) : null;

    private enum StatusTone { Idle, Busy, Ok, Run, Bad }

    private static (string Surface, string Foreground) ToneBrushes(StatusTone tone) => tone switch {
        StatusTone.Busy => ("Brush.Warning.Surface", "Brush.Warning.Foreground"),
        StatusTone.Ok => ("Brush.TasksAccent.Surface", "Brush.Success"),
        StatusTone.Run => ("Brush.Primary.Surface", "Brush.Primary"),
        StatusTone.Bad => ("Brush.Error.Surface", "Brush.Error"),
        _ => ("Brush.Surface.Disabled", "Brush.Text.Secondary")
    };

    // The pill, title and detail restate the action RuntimeControls chose; progress only adds the current item.
    private void ApplyRuntimeStatus(RuntimeControlState state, RunProgressView? progress)
    {
        var planSummary = _projectPlan is { } project ? $"{project.EnabledItems.Count} 项任务待执行" : "项目未加载";
        var current = progress?.Current;
        var (tone, pill, title, detail, warn) = state.Action switch {
            RuntimeAction.Prepare => (StatusTone.Idle, "未准备", "开始任务前需要准备运行环境",
                "将准备桌面分身和 Worker", false),
            RuntimeAction.InProgress { Kind: ProgressKind.PreparingEnvironment } => (StatusTone.Busy, "准备中",
                "正在准备运行环境", PreparationStage(), false),
            RuntimeAction.InProgress { Kind: ProgressKind.StartingRun } => (StatusTone.Busy, "启动中", "正在开始任务",
                "正在把执行计划交给 Worker…", false),
            RuntimeAction.InProgress { Kind: ProgressKind.StoppingRun } => (StatusTone.Busy, "停止中", "正在停止任务",
                "等待当前任务安全结束…", false),
            RuntimeAction.InProgress => (StatusTone.Busy, "处理中", "正在结束桌面分身", "正在注销 Child Session…", false),
            RuntimeAction.Stop when current is not null => (StatusTone.Run, "运行中",
                $"第 {progress!.CurrentIndex + 1} / {progress.Total} 项：{current.TaskLabel}",
                $"本项已运行 {ElapsedText(current.StartedAtUtc, null)}", false),
            RuntimeAction.Stop => (StatusTone.Run, "运行中", "任务运行中", string.Empty, false),
            RuntimeAction.Start { Blocker: { } blocker } => (StatusTone.Ok, "就绪", planSummary,
                StartBlockerText(blocker), true),
            RuntimeAction.Start => (StatusTone.Ok, "就绪", planSummary,
                $"{ActiveConfigurationName} · 运行环境已就绪{(progress is null ? "" : "，可以再次开始")}", false),
            RuntimeAction.Retry { Target: RetryTarget.Prepare } => (StatusTone.Bad, "需重试", "运行环境未就绪",
                "重新准备运行环境后再开始任务", false),
            RuntimeAction.Retry retry => (StatusTone.Bad, "需重试", "上次运行失败",
                retry.Blocker is { } blocker ? StartBlockerText(blocker) : "修正后可以重新开始任务",
                retry.Blocker is not null),
            RuntimeAction.Recover { Reason: var reason } => (StatusTone.Bad, "故障", RecoveryTitle(reason),
                RecoveryText(reason), false),
            _ => (StatusTone.Idle, string.Empty, string.Empty, string.Empty, false)
        };
        var (surface, foreground) = ToneBrushes(tone);
        RuntimeStatusPill.Background = (WpfBrush)FindResource(surface);
        RuntimeStatusText.Foreground = (WpfBrush)FindResource(foreground);
        RuntimeStatusText.Text = pill;
        AutomationProperties.SetName(RuntimeStatusPill, $"运行状态：{pill}");
        ApplyScheduleCapsule(tone);
        RuntimeStatusTitle.Text = title;
        RuntimeStatusDetail.Text = detail;
        RuntimeStatusDetail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RuntimeStatusDetail.Foreground =
            (WpfBrush)FindResource(warn ? "Brush.Warning.Foreground" : "Brush.Text.Secondary");

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

    private string ActiveConfigurationName => _projectPlan is { } project
        ? project.Configurations.Single(item => item.Id == project.ActiveConfigurationId).Name : string.Empty;

    private static string ElapsedText(DateTime? startedAtUtc, DateTime? endedAtUtc) =>
        RunProgress.Elapsed(startedAtUtc, endedAtUtc, DateTime.UtcNow) is { } elapsed
            ? RunProgress.FormatDuration(elapsed) : "00:00";

    // RunProgress already hides a Last Run whose plan does not match the configuration on screen.
    private void ApplyRunProgress(RunProgressView? progress)
    {
        var shown = progress is not null;
        RunProgressPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (progress is not null) {
            RunProgressCaption.Text = RunProgress.Caption(progress);
            ApplyRunElapsed(progress);
            AutomationProperties.SetName(RunProgressBar, RunProgress.AccessibleCaption(progress));
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
        if (CurrentControls.Action is RuntimeAction.Stop && progress.Current is { } current) {
            RuntimeStatusDetail.Text = $"本项已运行 {ElapsedText(current.StartedAtUtc, null)}";
        }
    }

    private void ApplyRunElapsed(RunProgressView progress) => RunProgressElapsed.Text = progress.Active
        ? $"总用时 {ElapsedText(progress.StartedAtUtc, null)}"
        : $"用时 {ElapsedText(progress.StartedAtUtc, progress.EndedAtUtc)}";

    private void RunProgressBar_SizeChanged(object sender, SizeChangedEventArgs e) => DrawRunProgressBar();

    // Fill covers finished items only; the running item is marked by its position, never by an invented percentage.
    private void DrawRunProgressBar()
    {
        RunProgressBar.Children.Clear();
        if (_displayedProgress is not { Total: > 0 } progress || RunProgressBar.ActualWidth <= 0) {
            return;
        }
        var width = RunProgressBar.ActualWidth;
        var gap = progress.Total > 20 ? 0 : 2;
        var segment = (width - gap * (progress.Total - 1)) / progress.Total;
        void Add(double left, double size, string brush)
        {
            var bar = new System.Windows.Shapes.Rectangle {
                Width = Math.Max(0, size), Height = 6, Fill = (WpfBrush)FindResource(brush),
                RadiusX = gap == 0 ? 0 : 3, RadiusY = gap == 0 ? 0 : 3
            };
            Canvas.SetLeft(bar, left);
            RunProgressBar.Children.Add(bar);
        }
        for (var item = 0; item < progress.Total; item++) {
            var brush = progress.Items[item].State switch {
                PlanItemProgress.Succeeded => "Brush.Success",
                PlanItemProgress.Failed => "Brush.Error",
                PlanItemProgress.Running => "Brush.Primary",
                PlanItemProgress.Stopped => "Brush.Border.Strong",
                _ => "Brush.Progress.Track"
            };
            Add((segment + gap) * item, segment, brush);
        }
        RunProgressBar.Clip = new RectangleGeometry(new Rect(0, 0, width, 6), 3, 3);
    }

    private void ShowActionableError(string title, Exception exception, string recovery, bool offerLogDirectory, string? detail = null)
    {
        var message = $"{exception.GetBaseException().Message}\n\n{recovery}";
        if (detail != null) message = detail + "\n\n" + message;
        if (!offerLogDirectory) {
            _presentation.ShowError(null, title, exception, message, MessageBoxButton.OK);
            return;
        }

        var answer = _presentation.ShowError(null, title, exception, $"{message}\n\n是否打开日志目录查看详细信息？",
            MessageBoxButton.YesNo);
        if (answer == MessageBoxResult.Yes) {
            TryOpenLogsDirectory();
        }
    }

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
