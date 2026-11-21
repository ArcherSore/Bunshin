using System.Diagnostics;
using Gui.Views;
using Updates;

namespace Gui;

public partial class App
{
    internal async Task InstallUpdateAsync(string reference)
    {
        if (IsExiting || _mainWindow is null || _sessionManager is null
            || _workerCoordinator is null || _logger is null || _environment is null) { return; }
        await InstallAfterEnvironmentEndAsync(_environment, _mainWindow, _logger, async () => {
            var engine = new UpdateEngineClient(new ProcessStartInfo(
                Path.Combine(AppContext.BaseDirectory, UpdateEngineClient.FileName)) {
                WorkingDirectory = AppContext.BaseDirectory
            });
            await engine.InstallAsync(AppContext.BaseDirectory, reference, Environment.ProcessId, default);
        }, async () => {
            _logger.Info("实际 Update Engine 已 ready；GUI 即将退出。");
            _trayIcon?.Dispose();
            _trayIcon = null;
            _sessionManager.Dispose();
            await _workerCoordinator.DisposeAsync();
            _workerCoordinator = null;
            _mainWindow.AllowClose();
            _mainWindow.Close();
            _logger.Dispose();
        });
        Shutdown(0);
    }

    internal static async Task InstallAfterEnvironmentEndAsync(RuntimeEnvironment environment, MainWindow window,
        Infrastructure.AppLogger logger, Func<Task> install, Func<Task> close)
    {
        environment.Closing = true;
        window.SetExitInProgress(true);
        var handedOff = false;
        try {
            // Keep the environment gate through the engine acknowledgement and GUI teardown.
            await environment.EndAsync(inGate: async ended => {
                if (ended.Error is { } error)
                    throw new IOException($"{error.Message}已取消安装。请查看日志后重试。", error);
                await install();
                handedOff = true;
                window.CommitExit();
                await close();
            });
        } catch (Exception error) when (handedOff) {
            logger.Error("Engine 已接管，GUI 清理失败，继续退出。", error);
        } finally {
            if (!handedOff) {
                environment.Closing = false;
                window.SetExitInProgress(false);
            }
        }
    }
}
