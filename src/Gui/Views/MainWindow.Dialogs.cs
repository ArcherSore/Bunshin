using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Gui.Infrastructure;
using Clipboard = System.Windows.Clipboard;

namespace Gui.Views;

public partial class MainWindow
{
    private enum PromptKind { Notice, EndEnvironment, Exit }

    private sealed record WindowPrompt(string Title, string Message, Exception? Error = null, bool OfferLogs = false,
        string? ConfirmLabel = null, PromptKind Kind = PromptKind.Notice, string? CopyText = null,
        bool ShowWhileClosing = false)
    {
        internal TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Shown { get; set; }
    }

    private readonly List<WindowPrompt> _windowPrompts = [];
    private WindowPrompt? _visiblePrompt;
    private IInputElement? _promptPreviousFocus;

    private bool CanShowWindowPrompt => IsVisible && WindowState != WindowState.Minimized;

    private void InitializeWindowPrompts()
    {
        PromptDialog.Accepted += (_, _) => CompleteWindowPrompt(accepted: true);
        PromptDialog.Dismissed += (_, _) => CompleteWindowPrompt(accepted: false);
        PromptDialog.LogsRequested += (_, _) => TryOpenLogsDirectory();
        PreviewGotKeyboardFocus += WindowPrompt_PreviewGotKeyboardFocus;
    }

    internal Task<bool> ConfirmExitAsync() => QueueWindowPrompt(new("确认退出",
        "桌面分身仍在运行。退出会先停止正在运行的任务，再结束桌面分身和里面的游戏。",
        ConfirmLabel: "退出", Kind: PromptKind.Exit));

    internal Task<bool> ConfirmEndEnvironmentAsync() => QueueWindowPrompt(new("结束桌面分身",
        "会先停止正在运行的任务，再结束桌面分身和里面的游戏。",
        ConfirmLabel: "结束", Kind: PromptKind.EndEnvironment));

    internal Task<bool> ShowErrorPromptAsync(string title, string message, Exception error, bool offerLogs,
        bool showWhileClosing = false)
    {
        if (_lifecycle.Orchestration.Lifecycle.ExitCommitted) return Task.FromResult(false);
        if (!_exitInProgress || showWhileClosing) {
            if (!CanShowWindowPrompt) {
                return Task.FromResult(ShowErrorBalloon(new(title, message, true, title, error)));
            }
            if (_presentation.ShowError is { } present) {
                present(title, error, message);
                return Task.FromResult(true);
            }
        }
        return QueueWindowPrompt(new(title, message, error, offerLogs, ShowWhileClosing: showWhileClosing));
    }

    private bool ShowErrorBalloon(RunTerminalNotification notice)
    {
        NotificationRequested?.Invoke(this, notice);
        return _presentation.ShowBalloon?.Invoke(notice) == true;
    }

    private Task<bool> QueueWindowPrompt(WindowPrompt prompt)
    {
        _windowPrompts.Add(prompt);
        RefreshWindowPrompts();
        return prompt.Answer.Task;
    }

    private void RefreshWindowPrompts()
    {
        if (!CanShowWindowPrompt) DispatchUnshownErrorsToTray();

        // Keep confirmations ahead of routine notices, and let a new notice appear over an older one.
        var next = CanShowWindowPrompt ? _windowPrompts.Where(CanPresentPrompt).Reverse().MaxBy(prompt => prompt.Kind) : null;
        if (next is { Kind: PromptKind.Notice } && ForcedStartOverlay.Visibility == Visibility.Visible) next = null;
        if (ReferenceEquals(next, _visiblePrompt)) return;

        _visiblePrompt = next;
        if (next is null) {
            PromptOverlay.Visibility = Visibility.Collapsed;
            RemoveModalWindowHook();
            FocusAfterWindowPrompt();
        } else {
            RememberWindowPromptFocus();
            PromptDialog.ShowPrompt(next.Title, next.Message, next.Error?.ToString(), next.OfferLogs, next.ConfirmLabel);
            next.Shown = true;
            PromptOverlay.Visibility = Visibility.Visible;
            EnsureModalWindowHook();
            ReevaluateOnboarding();
            PromptDialog.FocusDefault();
        }
    }

    private void RememberWindowPromptFocus()
    {
        if (_promptPreviousFocus is null && Keyboard.FocusedElement is DependencyObject target
            && WindowOverlayRoot.IsAncestorOf(target)) {
            _promptPreviousFocus = Keyboard.FocusedElement;
        }
    }

    private void FocusAfterWindowPrompt()
    {
        if (PromptOverlay.Visibility == Visibility.Visible) {
            if (CanShowWindowPrompt) PromptDialog.FocusDefault();
            return;
        }
        if (ForcedStartOverlay.Visibility == Visibility.Visible) {
            if (CanShowWindowPrompt) ForcedStartDialog.FocusDefault();
            return;
        }

        var previous = _promptPreviousFocus;
        _promptPreviousFocus = null;
        if (CanShowWindowPrompt) {
            if (previous is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
            else if (ProjectReader.Visibility == Visibility.Visible) ProjectReader.FocusReader();
            else if (UpdateOverlay.Visibility == Visibility.Visible) UpdateOverlay.Focus();
            else Sidebar.Home.Focus();
        }
        ReevaluateOnboarding();
    }

    private void CompleteWindowPrompt(bool accepted)
    {
        if (_visiblePrompt is not { } prompt || !_windowPrompts.Remove(prompt)) return;
        prompt.Answer.TrySetResult(prompt.ConfirmLabel is null || accepted);
        // Let the confirmation continuation start closing before another question can be displayed.
        _ = Dispatcher.InvokeAsync(RefreshWindowPrompts);
    }

    private void CancelWindowConfirmations()
    {
        foreach (var prompt in _windowPrompts.Where(prompt => prompt.Kind != PromptKind.Notice).ToArray()) {
            _windowPrompts.Remove(prompt);
            prompt.Answer.TrySetResult(false);
        }
        RefreshWindowPrompts();
    }

    private bool CanPresentPrompt(WindowPrompt prompt) => !_exitInProgress || prompt.Shown || prompt.ShowWhileClosing;

    private void DispatchUnshownErrorsToTray()
    {
        foreach (var prompt in _windowPrompts.Where(prompt => !prompt.Shown && prompt.Error is not null
            && CanPresentPrompt(prompt)).ToArray()) {
            _windowPrompts.Remove(prompt);
            prompt.Answer.TrySetResult(ShowErrorBalloon(new(prompt.Title, prompt.Message, true, prompt.Title, prompt.Error)));
        }
    }

    private void CloseWindowPrompts()
    {
        foreach (var prompt in _windowPrompts) prompt.Answer.TrySetResult(prompt.Shown && prompt.ConfirmLabel is null);
        _windowPrompts.Clear();
    }

    private void WindowPrompt_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var overlay = PromptOverlay.Visibility == Visibility.Visible ? PromptOverlay
            : ForcedStartOverlay.Visibility == Visibility.Visible ? ForcedStartOverlay : null;
        if (overlay is not null && e.NewFocus is DependencyObject target && target != overlay && !overlay.IsAncestorOf(target)) {
            e.Handled = true;
        }
    }

    private bool HandleWindowPromptKey(System.Windows.Input.KeyEventArgs e)
    {
        if (PromptOverlay.Visibility != Visibility.Visible) return false;
        if (e.Key == Key.Escape) {
            CompleteWindowPrompt(accepted: false);
            return true;
        }
        if (_visiblePrompt?.CopyText is { } copy && e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) {
            try {
                Clipboard.SetText(copy);
            } catch (ExternalException exception) {
                _logger.Warn("复制网址失败。", exception);
            }
            return true;
        }
        return false;
    }
}
