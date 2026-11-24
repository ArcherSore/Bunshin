using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Gui.ChildSession;
using Gui.Infrastructure;
using Gui.Settings;
using Gui.Themes;
using Gui.Views;
using Gui.Worker;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using Protocol;

namespace Gui;

public partial class App : System.Windows.Application
{
    private AppLogger? _logger;
    private ChildSessionManager? _sessionManager;
    private WorkerCoordinator? _workerCoordinator;
    private RuntimeEnvironment? _environment;
    private MainWindow? _mainWindow;
    private readonly GuiTelemetry _telemetry = new();
    private Forms.NotifyIcon? _trayIcon;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private bool _hasShownTrayNotification;
    private bool _userExitRequested;
    internal ThemeService Themes { get; private set; } = null!;

    private void ApplyStartupTheme(bool selfTest)
    {
        Themes = new ThemeService(Resources);
        // The self-test starts fixed on light so that its checks do not depend on the machine's setting.
        Themes.Apply(selfTest ? AppThemeMode.Light
            : ApplicationSettings.ReadAppearance(AppContext.BaseDirectory));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase)) {
            ApplyStartupTheme(selfTest: true);
            Environment.ExitCode = SelfTestRunner.Run(e.Args);
            Shutdown(Environment.ExitCode);
            return;
        }

        var command = LaunchCommand.Parse(e.Args);
        // Both come before anything that would touch the single-instance lock, the log or a window.
        if (command.Help) {
            ConsoleOutput.Write(LaunchCommand.HelpText);
            Shutdown(0);
            return;
        }
        if (!Elevation.IsElevated()) {
            Environment.ExitCode = RelaunchElevated(e.Args) ? 0 : 1;
            Shutdown(Environment.ExitCode);
            return;
        }

        if (!TryAcquireSingleInstance(command)) {
            Shutdown(Environment.ExitCode);
            return;
        }

        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        _logger = new AppLogger();
        // Before the handler that shows a dialog, so a UI failure is reported even if the user never closes it. The
        // SDK reports AppDomain and unobserved task exceptions itself once the loaded project enables it.
        _telemetry.Attach(Dispatcher);
        RegisterGlobalExceptionHandlers(_logger);
        LogStartup(_logger);

        _sessionManager = new ChildSessionManager(_logger);
        _workerCoordinator = new WorkerCoordinator(
            _logger, Path.Combine(AppContext.BaseDirectory, "state"),
            Path.Combine(AppContext.BaseDirectory, "worker", AppIdentity.WorkerFileName));
        _environment = new RuntimeEnvironment(_logger, _sessionManager, _workerCoordinator);
        ApplyStartupTheme(selfTest: false);
        _mainWindow = new MainWindow(
            _logger, _sessionManager, _workerCoordinator, _environment, RequestExitAsync, EndEnvironmentAsync,
            InstallUpdateAsync, telemetry: _telemetry, launchCommand: command, quitAfterRun: () => _ = ExitAsync(confirm: null),
            presentation: new RunPresentation { ShowBalloon = ShowBalloon });
        _mainWindow.HiddenToTray += MainWindow_HiddenToTray;
        _mainWindow.ProjectIdentityChanged += MainWindow_ProjectIdentityChanged;
        MainWindow = _mainWindow;
        CreateTrayIcon();
        // A launch by the system (logon) has no one to look at the window; it waits in the tray.
        if (command.AutoStart) {
            _mainWindow.StartInTray();
        } else {
            _mainWindow.Show();
        }
    }

    internal static string ApplicationVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";

    internal static void LogStartup(AppLogger logger)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        logger.Info($"{AppIdentity.Name} 正式 GUI 启动 [version={ApplicationVersion}] [pid={process.Id}] "
            + $"[session={process.SessionId}] [elevated={Elevation.IsElevated()}] [os={Environment.OSVersion}] "
            + $"[arch={RuntimeInformation.ProcessArchitecture}] [runtime={RuntimeInformation.FrameworkDescription}] "
            + $"[directory={AppContext.BaseDirectory}] [logDirectory={logger.LogDirectory}] "
            + $"[args={string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}]");
    }

    internal Task RequestExitAsync() => ExitWithConfirmationAsync(
        () => ConfirmInMainWindowAsync(window => window.ConfirmExitAsync()));

    internal Task ExitAsync(Func<bool>? confirm, RuntimeEnvironment? environment = null, MainWindow? window = null,
        Action<Exception>? reportError = null, Action? shutdown = null) =>
        ExitWithConfirmationAsync(confirm is null ? null : () => Task.FromResult(confirm()), environment, window,
            reportError, shutdown);

    private async Task ExitWithConfirmationAsync(Func<Task<bool>>? confirm, RuntimeEnvironment? environment = null,
        MainWindow? window = null, Action<Exception>? reportError = null, Action? shutdown = null)
    {
        environment ??= _environment;
        window ??= _mainWindow;
        if (environment is null || window is null || environment.Closing || (confirm is not null && _userExitRequested)) {
            return;
        }
        // A manual request owns its dialogs until they close. Automatic quit can enter while the user is deciding.
        if (confirm is not null) _userExitRequested = true;
        var exitStarted = false;
        var exitCompleted = false;
        try {
            var result = await environment.EndWithConfirmationAsync(confirm, async ended => {
                if (ended.Outcome != EnvironmentEndOutcome.Ended) return;

                window.CommitExit();
                _logger?.Info($"{AppIdentity.Name} 正常退出。");
                _trayIcon?.Dispose();
                _trayIcon = null;
                _sessionManager?.Dispose();
                if (_workerCoordinator is not null) {
                    await _workerCoordinator.DisposeAsync();
                    _workerCoordinator = null;
                }
                window.HiddenToTray -= MainWindow_HiddenToTray;
                window.AllowClose();
                window.Close();
                _logger?.Dispose();
                exitCompleted = true;
                if (shutdown is null) Shutdown(0);
                else shutdown();
            }, onAccepted: () => {
                exitStarted = true;
                environment.Closing = true;
                window.SetExitInProgress(true);
            });
            if (result.Error is { } error) {
                if (reportError is not null) reportError(error);
                else await window.ShowErrorPromptAsync(AppIdentity.Name,
                    $"{error.Message}已取消退出，避免遗留未知状态。请查看日志后重试。", error, offerLogs: true);
            }
        } finally {
            if (exitStarted && !exitCompleted) {
                environment.Closing = false;
                window.SetExitInProgress(false);
            }
            if (confirm is not null) _userExitRequested = false;
        }
    }

    private Task<bool> ConfirmInMainWindowAsync(Func<MainWindow, Task<bool>> confirm)
    {
        if (_mainWindow is not { } window) return Task.FromResult(false);
        if (!window.IsActive || !window.IsVisible || window.WindowState == WindowState.Minimized) ShowMainWindow();
        return confirm(window);
    }

    // The tray and the main window share this confirmation and report any teardown error once.
    internal async Task EndEnvironmentAsync()
    {
        if (IsExiting || _environment is null) {
            return;
        }

        EnvironmentEndResult ended;
        try {
            if (_environment.RequestEndWithConfirmation(
                () => ConfirmInMainWindowAsync(window => window.ConfirmEndEnvironmentAsync())) is not { } request) {
                return;
            }
            ended = await request;
        } catch (OperationCanceledException) {
            // Only an exit refuses the request; the exit may have finished asking by the time this resumes.
            return;
        }
        if (ended.Error is { } error && _mainWindow is { } window) {
            await window.ShowErrorPromptAsync("结束桌面分身未确认",
                $"{error.Message}已保留 Worker Admission，可再次结束桌面分身；详细信息已写入日志。",
                error, offerLogs: true);
        }
    }

    private bool IsExiting => _environment?.Closing == true;

    private void CreateTrayIcon()
    {
        if (_mainWindow is null || _sessionManager is null || _logger is null) {
            return;
        }

        _trayIcon = new Forms.NotifyIcon {
            Icon = LoadTrayIcon(),
            Text = AppIdentity.Name,
            Visible = true
        };
        _trayIcon.MouseUp += (_, args) => {
            if (args.Button != Forms.MouseButtons.Right) return;
            Dispatcher.Invoke(() => TrayMenu.Show(TrayMenu.Build(TrayMenuEntries(),
                action => _logger.Info($"托盘操作{AppLogger.Field("action", action)}"))));
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    internal IReadOnlyList<TrayMenuAction?> TrayMenuEntries() => [
        new("显示主窗口", ShowMainWindow, Default: true),
        new("显示子桌面", async () => await ShowChildDesktopFromTrayAsync()),
        new("结束桌面分身", async () => await EndEnvironmentAsync(), Danger: true),
        null,
        new("退出程序", async () => await RequestExitAsync(), Danger: true)
    ];

    private bool ShowBalloon(RunTerminalNotification notice)
    {
        if (_trayIcon == null) return false;
        try {
            _trayIcon.ShowBalloonTip(5000, notice.Title, notice.Message,
                notice.Failed ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
            return true;
        } catch (System.ComponentModel.Win32Exception exception) {
            _logger?.Warn("显示任务通知失败。", exception);
            return false;
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is not null) {
                using var stream = info.Stream;
                return new System.Drawing.Icon(stream, 32, 32);
            }
        } catch {
            // Fall back to exe icon or system default if resource stream is unavailable.
        }

        try {
            if (!string.IsNullOrEmpty(Environment.ProcessPath) &&
                !Path.GetFileNameWithoutExtension(Environment.ProcessPath)
                    .Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (extracted is not null) {
                    return extracted;
                }
            }
        } catch {
            // Fall back to system icon if extraction fails.
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void MainWindow_ProjectIdentityChanged(object? sender, ProjectIdentity identity)
    {
        if (_trayIcon is null) {
            return;
        }
        var previous = _trayIcon.Icon;
        _trayIcon.Icon = (identity.IconPath is { } path ? LoadProjectTrayIcon(path) : null) ?? LoadTrayIcon();
        _trayIcon.Text = identity.Name.Length > 127 ? identity.Name[..127] : identity.Name;
        if (previous != System.Drawing.SystemIcons.Application) {
            previous?.Dispose();
        }
    }

    private System.Drawing.Icon? LoadProjectTrayIcon(string path)
    {
        try {
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)) {
                return new System.Drawing.Icon(path, 32, 32);
            }
            using var source = new System.Drawing.Bitmap(path);
            using var scaled = new System.Drawing.Bitmap(source, 32, 32);
            var handle = scaled.GetHicon();
            try {
                return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
            } finally {
                DestroyIcon(handle);
            }
        } catch (Exception exception) {
            _logger?.Warn($"加载项目托盘图标失败，已使用默认图标。path={path}", exception);
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private void MainWindow_HiddenToTray(object? sender, EventArgs e)
    {
        if (_hasShownTrayNotification || _trayIcon is null) {
            return;
        }

        _hasShownTrayNotification = true;
        _trayIcon.BalloonTipTitle = $"{AppIdentity.Name} 仍在运行";
        _trayIcon.BalloonTipText = "主窗口已隐藏到托盘。双击托盘图标可恢复窗口，右键可退出程序。";
        _trayIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(5000);
        _logger?.Info("已显示首次关闭到托盘提示。");
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private async Task ShowChildDesktopFromTrayAsync()
    {
        if (IsExiting || _sessionManager is null || _logger is null || _environment is null) {
            return;
        }

        try {
            await _environment.RunExclusiveAsync(() => _sessionManager.EnsureConnectedAsync(showPreview: true));
        } catch (OperationCanceledException) when (IsExiting) {
            return;
        } catch (Exception exception) {
            _logger.Error("从托盘显示子桌面失败。", exception);
            if (_mainWindow is { } window) {
                await window.ShowErrorPromptAsync("显示子桌面失败",
                    $"{exception.GetBaseException().Message}\n\n请在主窗口检查桌面分身状态后重试；详细信息已写入日志。",
                    exception, offerLogs: true);
            }
        }
    }

    private void RegisterGlobalExceptionHandlers(AppLogger logger)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            logger.Critical("GUI 线程发生未处理异常，应用将继续运行。", args.Exception);
            args.Handled = true;
            var message = $"操作发生异常，但程序仍在运行：\n{args.Exception.GetBaseException().Message}\n\n请查看日志。";
            if (_mainWindow is { } window) {
                _ = window.ShowErrorPromptAsync(AppIdentity.Name, message, args.Exception, offerLogs: true);
            } else {
                WpfMessageBox.Show(message, AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            logger.Critical("进程发生不可恢复的未处理异常。", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.Error("后台任务发生未观察异常。", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstance();
        // Sends what is queued within one second, after a new instance may already start.
        _telemetry.Dispose();
        base.OnExit(e);
    }

    // The elevated process takes over; this one only exists to ask. Without administrator rights nothing can run,
    // so a declined prompt ends the launch.
    private static bool RelaunchElevated(string[] arguments)
    {
        try {
            return Elevation.TryRelaunch(arguments);
        } catch (Exception exception) {
            WpfMessageBox.Show(
                $"无法请求管理员权限，程序将退出：\n\n{exception.GetBaseException().Message}",
                AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool TryAcquireSingleInstance(LaunchCommand command)
    {
        try {
            if (command.AutoStart && RunningCopies.Exists()) {
                Environment.ExitCode = 0;
                return false;
            }
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var mutexName = $@"Local\{AppIdentity.Name}-{currentProcess.SessionId}";
            var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (!createdNew) {
                mutex.Dispose();
                Environment.ExitCode = 0;
                // A launch by the system (logon) has no one to answer a dialog, and the running instance wins.
                if (!command.AutoStart) {
                    WpfMessageBox.Show(
                        $"{AppIdentity.Name} 已在当前 Windows Session 中运行。请从任务栏或托盘打开现有实例。",
                        AppIdentity.Name,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return false;
            }

            _singleInstanceMutex = mutex;
            _ownsSingleInstanceMutex = true;
            return true;
        } catch (Exception exception) {
            Environment.ExitCode = 1;
            WpfMessageBox.Show(
                $"无法建立 {AppIdentity.Name} 单实例保护，程序将退出：\n\n{exception.GetBaseException().Message}",
                AppIdentity.Name,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ReleaseSingleInstance()
    {
        if (_singleInstanceMutex is null) {
            return;
        }

        if (_ownsSingleInstanceMutex) {
            try {
                _singleInstanceMutex.ReleaseMutex();
            } catch (ApplicationException) {
                // The process is already exiting; disposing the handle is sufficient fallback.
            }
        }

        _ownsSingleInstanceMutex = false;
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
    }
}
