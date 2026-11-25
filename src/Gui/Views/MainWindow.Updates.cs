using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Gui.Infrastructure;
using Updates;

namespace Gui.Views;

public partial class MainWindow
{
    private readonly Func<CancellationToken, Task<EngineCheckResult>> _checkForUpdate;
    private readonly Func<string, IProgress<EngineProgress>, CancellationToken, Task<string>> _prepareUpdate;
    private CancellationTokenSource? _updateCancellation;
    private EngineCheckResult? _updateCheck;
    private string? _preparedReference;
    private bool _preparingUpdate;
    private bool _updateBusy;
    private IInputElement? _updatePreviousFocus;

    private UpdateCheckState _updateCheckState;
    private string? _renderedUpdateNotes;
    private string _updateOperationStatus = "";

    // Presentation only; download, preparation and installation retain their existing operation gates.
    private enum UpdateCheckState
    {
        Idle, Checking, UpToDate, UpdateAvailable, CheckFailed
    }

    private void InitializeUpdates()
    {
        UpdateDialog.CloseUpdateButton.Click += CloseUpdate_Click;
        UpdateDialog.CheckingCloseButton.Click += CloseUpdate_Click;
        UpdateDialog.LaterUpdateButton.Click += CloseUpdate_Click;
        UpdateDialog.DialogCheckUpdateButton.Click += CheckUpdate_Click;
        UpdateDialog.ReleaseNotesButton.Click += ReleaseNotes_Click;
        UpdateDialog.DownloadUpdateButton.Click += DownloadUpdate_Click;
        UpdateDialog.CancelDownloadButton.Click += CancelDownload_Click;
        UpdateDialog.InstallUpdateButton.Click += InstallUpdate_Click;

        _settings.CurrentVersion.Text = _projectPlan?.ProjectVersion ?? "—";
        try {
            _settings.LoadUpdatePreference();
            if (_settings.CheckOnStartup.Value) {
                _ = CheckUpdateAsync();
            }
        } catch (Exception exception) {
            _logger.Warn("更新初始化不可用。", exception);
            _updateCheckState = UpdateCheckState.CheckFailed;
            _settings.CheckUpdate.Status = "暂时无法检查更新，请确认使用完整发布包后重试。";
        }
        UpdateUpdaterControls();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => await _settings.CheckUpdate.ExecuteAsync();

    private async Task CheckUpdateFromSettingsAsync()
    {
        OpenUpdateDialog();
        await CheckUpdateAsync();
    }

    private async Task CheckUpdateAsync()
    {
        if (_updateBusy || _exitInProgress) {
            return;
        }
        _updateBusy = true;
        _updateCancellation = new CancellationTokenSource();
        _updateCheck = null;
        _preparedReference = null;
        _updateCheckState = UpdateCheckState.Checking;
        _updateOperationStatus = "";
        RefreshUpdateNotes();
        UpdateUpdaterControls();
        _settings.CheckUpdate.Status = "正在检查更新…";
        try {
            using var scope = _logger.Scope("检查更新");
            var result = await _checkForUpdate(_updateCancellation.Token);
            _logger.Info($"检查更新完成{AppLogger.Field("current", result.CurrentVersion)}"
                + AppLogger.Field("target", result.Update?.Version));
            if (_exitInProgress) {
                _updateCheckState = UpdateCheckState.Idle;
                return;
            }
            _updateCheck = result;
            _settings.CurrentVersion.Text = result.CurrentVersion;
            _updateCheckState = result.Update is null
                ? UpdateCheckState.UpToDate : UpdateCheckState.UpdateAvailable;
            _settings.CheckUpdate.Status = result.Update is null ? "当前已是最新版本" : "发现新版本，可查看更新。";
            RefreshUpdateNotes();
        } catch (OperationCanceledException) when (_exitInProgress) {
            // Exiting cancels this optional operation; it does not alter the runtime shutdown path.
            _logger.Debug("退出时取消了检查更新。");
            _updateCheckState = UpdateCheckState.Idle;
        } catch (Exception exception) {
            _logger.Warn($"检查更新失败{EngineDiagnostics(exception)}", exception);
            _updateCheckState = UpdateCheckState.CheckFailed;
            if (!_exitInProgress) {
                _settings.CheckUpdate.Status = exception is IOException or InvalidDataException
                    ? exception.Message : "检查更新失败，请确认 Update Engine 可运行后重试。";
            }
        } finally {
            _updateCancellation.Dispose();
            _updateCancellation = null;
            _updateBusy = false;
            UpdateUpdaterControls();
        }
    }

    private static Task<EngineCheckResult> CheckWithEngineAsync(CancellationToken cancellation)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, UpdateEngineClient.FileName);
        var engine = new UpdateEngineClient(new ProcessStartInfo(executable) {
            WorkingDirectory = AppContext.BaseDirectory
        });
        return engine.CheckAsync(AppContext.BaseDirectory, cancellation);
    }

    private static Task<string> PrepareWithEngineAsync(
        string descriptor, IProgress<EngineProgress> progress, CancellationToken cancellation)
    {
        var engine = new UpdateEngineClient(new ProcessStartInfo(
            Path.Combine(AppContext.BaseDirectory, UpdateEngineClient.FileName)) {
            WorkingDirectory = AppContext.BaseDirectory
        });
        return engine.PrepareAsync(AppContext.BaseDirectory, descriptor, progress, cancellation);
    }

    private static string EngineDiagnostics(Exception exception) =>
        AppLogger.Field("engine", UpdateEngineClient.Diagnostics(exception));

    private void RefreshUpdateNotes()
    {
        var notes = _updateCheck?.Update?.Notes ?? "";
        if (_renderedUpdateNotes == notes) {
            return;
        }
        _renderedUpdateNotes = notes;
        UpdateDialog.SetNotes(string.IsNullOrWhiteSpace(notes) ? "此版本暂无更新说明。" : notes, OpenUpdateLink);
    }

    private void OpenUpdateDialog()
    {
        if (IsGlobalModalOpen || _exitInProgress || _onboardingActive) {
            return;
        }
        _updatePreviousFocus = Keyboard.FocusedElement;
        UpdateUpdaterControls();
        UpdateOverlay.Visibility = Visibility.Visible;
        UpdateDialogSize();
        EnsureModalWindowHook();
        UpdateOverlay.Focus();
    }

    private void CloseUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_exitInProgress) {
            return;
        }
        UpdateOverlay.Visibility = Visibility.Collapsed;
        RemoveModalWindowHook();
        if (_updatePreviousFocus is not null) {
            Keyboard.Focus(_updatePreviousFocus);
        }
        ReevaluateOnboarding();
    }

    private void UpdateOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDialogSize();

    private void UpdateDialogSize()
    {
        UpdateDialog.MaxWidth = Math.Max(1, UpdateOverlay.ActualWidth - 48);
        UpdateDialog.MaxHeight = Math.Max(1, Math.Min(600, UpdateOverlay.ActualHeight * 0.8));
    }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_updateCheck?.Update?.ReleaseUrl, UriKind.Absolute, out var uri)) {
            OpenUpdateLink(uri);
        } else {
            // Older V2 engines can still display their notes without a separate release URL.
            UpdateDialog.FocusNotes();
        }
    }

    private void OpenUpdateLink(Uri uri)
    {
        if (uri.Scheme is not ("https" or "http")) {
            return;
        }
        try {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        } catch (Exception exception) {
            _updateOperationStatus = "无法打开链接，请检查默认浏览器设置。";
            UpdateUpdaterControls();
            _logger.Warn("打开更新链接失败。", exception);
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _exitInProgress || _updateCheck?.Update is not EngineUpdate update) { return; }
        _updateBusy = _preparingUpdate = true;
        _preparedReference = null;
        _updateCancellation = new CancellationTokenSource();
        _updateOperationStatus = "";
        UpdateDialog.StartProgress(update.Version);
        UpdateUpdaterControls();
        var target = AppLogger.Field("target", update.Version);
        var cancellation = _updateCancellation.Token;
        var progress = new DownloadProgress(value => {
            if (!_preparingUpdate || _exitInProgress || cancellation != _updateCancellation?.Token) { return; }
            UpdateDialog.ShowProgress(update.Version, value);
        });
        try {
            using var scope = _logger.Scope($"准备更新{target}");
            _preparedReference = await _prepareUpdate(update.Descriptor, progress, cancellation);
            _logger.Info($"更新已准备好{target}{progress.Downloaded}");
        } catch (OperationCanceledException) {
            _logger.Info($"准备更新已取消{target}{progress.Downloaded}");
            _updateOperationStatus = "已取消，可重新下载。";
        } catch (Exception exception) {
            _logger.Warn($"准备更新失败{target}{progress.Downloaded}{EngineDiagnostics(exception)}", exception);
            _updateOperationStatus = exception.Message;
        } finally {
            _updateCancellation.Dispose();
            _updateCancellation = null;
            _updateBusy = _preparingUpdate = false;
            UpdateUpdaterControls();
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _exitInProgress || _preparedReference is null) { return; }
        _updateBusy = true;
        _updateOperationStatus = "";
        UpdateUpdaterControls();
        var target = AppLogger.Field("target", _updateCheck?.Update?.Version);
        _logger.Info($"开始安装更新{target}");
        try {
            await _installUpdateAsync(_preparedReference);
        } catch (Exception exception) {
            _logger.Warn($"安装交接失败{target}{EngineDiagnostics(exception)}", exception);
            _updateOperationStatus = exception.Message;
        } finally {
            _updateBusy = false;
            UpdateUpdaterControls();
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();

    private void UpdateUpdaterControls()
    {
        _settings.CheckUpdate.IsEnabled = !_updateBusy && !_exitInProgress;
        var checking = _updateCheckState == UpdateCheckState.Checking;
        var available = _updateCheckState == UpdateCheckState.UpdateAvailable;
        Sidebar.ShowUpdate(_preparingUpdate ? UpdateMarker.Downloading
            : checking ? UpdateMarker.Checking : available ? UpdateMarker.Available : UpdateMarker.None);

        var state = _updateCheckState switch {
            UpdateCheckState.Checking => UpdateDialogState.Checking,
            UpdateCheckState.UpToDate => UpdateDialogState.Latest,
            UpdateCheckState.CheckFailed => UpdateDialogState.CheckFailed,
            UpdateCheckState.UpdateAvailable when _preparingUpdate => UpdateDialogState.Preparing,
            UpdateCheckState.UpdateAvailable when _preparedReference is not null =>
                _updateBusy ? UpdateDialogState.Installing : UpdateDialogState.Ready,
            UpdateCheckState.UpdateAvailable => UpdateDialogState.Available,
            _ => UpdateDialogState.Idle
        };
        var currentVersion = _updateCheck?.CurrentVersion ?? _projectPlan?.ProjectVersion ?? "—";
        UpdateDialog.Present(state, currentVersion, _updateCheck?.Update?.Version, _settings.CheckUpdate.Status,
            _updateOperationStatus, _exitInProgress);
        RefreshUpdateNotes();
    }

    // Progress<T> posts its callback, so an operation can end before the last one runs. The log reads what was
    // reported, saved here as it arrives; the display still updates through the callback.
    private sealed class DownloadProgress(Action<EngineProgress> display) : IProgress<EngineProgress>
    {
        private readonly IProgress<EngineProgress> _display = new Progress<EngineProgress>(display);
        private volatile string _downloaded = string.Empty;

        internal string Downloaded => _downloaded;

        public void Report(EngineProgress value)
        {
            if (value.Phase == "download") {
                _downloaded = AppLogger.Field("downloaded", $"{value.Bytes}/{value.Total}");
            }
            _display.Report(value);
        }
    }
}
