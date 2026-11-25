using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Gui.Infrastructure;
using ProjectModel;
using WpfSize = System.Windows.Size;

namespace Gui.Views;

public partial class MainWindow
{
    private enum OnboardingStep { AddTask, TaskDetails, Run, ShowDesktop }

    private OnboardingPreferences? _onboardingPreferences;
    private bool _onboardingStartupCompleted;
    private bool _onboardingAutoEnded;
    private bool _onboardingActive;
    private bool _onboardingReplay;
    private bool _onboardingPaused;
    private bool _onboardingShown;
    private bool _onboardingChangingStep;
    private bool _onboardingClosed;
    private OnboardingStep _onboardingStep;
    private bool _onboardingOpenedTaskShelf;
    private ProjectTaskChoice? _onboardingTask;
    private CancellationTokenSource? _onboardingStartCancellation;
    private CancellationTokenSource? _onboardingStepCancellation;
    private DispatcherOperation? _onboardingLayoutOperation;
    private IInputElement? _onboardingPreviousFocus;
    private TaskPlanEditor.PlanView? _onboardingPreviousView;
    private readonly RectangleGeometry _onboardingHole = new();
    private Rect _onboardingTargetBounds = Rect.Empty;
    private Rect _onboardingWindowBounds = Rect.Empty;

    private void InitializeOnboarding()
    {
        OnboardingOverlay.OnboardingNext.Click += OnboardingNext_Click;
        OnboardingOverlay.OnboardingPrevious.Click += OnboardingPrevious_Click;
        OnboardingOverlay.OnboardingSkip.Click += OnboardingSkip_Click;
    }

    private bool IsOnboardingVisible => _onboardingActive && !_onboardingPaused
        && OnboardingOverlay.Visibility == Visibility.Visible;

    private bool OnboardingCommandsAvailable => CurrentControls.CommandsAvailable
        && CurrentRunProgress is not { Active: true };

    private bool CanPresentOnboarding => !_onboardingClosed && OnboardingCommandsAvailable
        && IsLoaded && IsVisible && WindowState != WindowState.Minimized
        && HomeView.Visibility == Visibility.Visible && _projectPlan is not null
        && TaskPlanEditor.Workspace.Visibility == Visibility.Visible && !IsGlobalModalOpen && !SettingsOpen;

    private void ReplayOnboarding()
    {
        if (_projectPlan is null || !OnboardingCommandsAvailable || IsGlobalModalOpen || _onboardingActive) {
            return;
        }
        _onboardingAutoEnded = true;
        CancelOnboardingStart();
        CloseSettings();
        StartOnboarding(replay: true);
    }

    private void StartOnboarding(bool replay)
    {
        try {
            _onboardingPreviousFocus = Keyboard.FocusedElement;
            _onboardingReplay = replay;
            _onboardingPreviousView = TaskPlanEditor.CaptureView();
            _onboardingActive = true;
            _onboardingPaused = false;
            _onboardingShown = false;
            _onboardingStep = OnboardingStep.AddTask;
            _settings.ReplayOnboarding.Status = string.Empty;
            SwitchSection(MainSection.Home);
            WindowOverlayRoot.LayoutUpdated += Onboarding_LayoutUpdated;
            PreviewGotKeyboardFocus += Onboarding_PreviewGotKeyboardFocus;
            SystemParameters.StaticPropertyChanged += Onboarding_SystemParametersChanged;
            _ = ShowOnboardingStepAsync(animate: false);
        } catch (Exception exception) {
            FailOnboarding(exception);
        }
    }

    private async Task ShowOnboardingStepAsync(bool animate)
    {
        CancelOnboardingStep();
        var cancellation = new CancellationTokenSource();
        _onboardingStepCancellation = cancellation;
        _onboardingChangingStep = true;
        try {
            if (animate && IsOnboardingVisible && SystemParameters.ClientAreaAnimation) {
                OnboardingOverlay.OnboardingPopover.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(60)));
                await Task.Delay(60, cancellation.Token);
            }
            RefreshOnboardingRuntimePanel();
            var details = _onboardingStep == OnboardingStep.TaskDetails;
            _onboardingTask = details ? TaskPlanEditor.TaskForOnboarding() : null;
            SetOnboardingTaskShelf(details && _onboardingTask is null);
            if (_onboardingTask is { } task) {
                TaskPlanEditor.ExpandPlanItem(task);
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, cancellation.Token);
            if (!CanPresentOnboarding) {
                PauseOnboarding();
                return;
            }
            if (_onboardingTask is not null) {
                var card = ResolveOnboardingTarget();
                // Keep the title and the description visible when parameters exceed the viewport.
                card.BringIntoView(new Rect(0, 0, card.ActualWidth,
                    Math.Min(card.ActualHeight, TaskPlanEditor.PlanViewportHeight)));
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            SetOnboardingText();
            OnboardingOverlay.Visibility = Visibility.Visible;
            _onboardingPaused = false;
            UpdateOnboardingGeometry(animate);
            _onboardingShown = true;
            _onboardingChangingStep = false;
            OnboardingOverlay.OnboardingNext.Focus();
            if (SystemParameters.ClientAreaAnimation) {
                OnboardingOverlay.OnboardingPopover.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            }
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
        } catch (Exception exception) {
            FailOnboarding(exception);
        } finally {
            if (_onboardingStepCancellation == cancellation) {
                _onboardingStepCancellation = null;
                _onboardingChangingStep = false;
            }
            cancellation.Dispose();
        }
    }

    private FrameworkElement ResolveOnboardingTarget() => _onboardingStep switch {
        OnboardingStep.AddTask => TaskPlanEditor.AddTaskButton,
        OnboardingStep.TaskDetails => _onboardingTask is { } task
            ? TaskPlanEditor.PlanItemCard(task)
                ?? throw new InvalidOperationException("新手指引找不到第一张任务卡。")
            : TaskPlanEditor.TaskShelf,
        OnboardingStep.Run => TaskPlanEditor.StartPlanButton,
        OnboardingStep.ShowDesktop => RuntimeSidebar.DesktopViewButton,
        _ => throw new InvalidOperationException("新手指引步骤无效。")
    };

    private void SetOnboardingTaskShelf(bool show)
    {
        if (show && TaskPlanEditor.TaskShelf.Visibility != Visibility.Visible) {
            TaskPlanEditor.ShowTaskShelfForOnboarding(true);
            _onboardingOpenedTaskShelf = true;
        } else if (!show && _onboardingOpenedTaskShelf) {
            TaskPlanEditor.ShowTaskShelfForOnboarding(false);
            _onboardingOpenedTaskShelf = false;
        }
    }

    private void SetOnboardingText()
    {
        var empty = _projectPlan!.SelectedItemCount == 0;
        var (title, description) = _onboardingStep switch {
            OnboardingStep.AddTask => ("配置自动化任务", "点“添加任务”选择要执行的任务，拖动可以调整执行顺序。左侧可以保存多套配置。"),
            OnboardingStep.TaskDetails when empty => ("查看说明并设置参数", "添加任务后，会在执行计划中出现任务。"
                + "点任务行，展开后可在顶部查看说明，并在下方调整参数。"),
            OnboardingStep.TaskDetails => ("查看说明并设置参数", "点任务行，展开后可在顶部查看说明，并在下方调整参数。"
                + "运行前先确认任务所需的游戏界面。"),
            OnboardingStep.Run => ("启动自动化", "配置完成后，点“运行”开始。需要时会先准备运行环境，任务开始后可以从这里停止。"),
            _ => ("观看桌面分身", "运行环境就绪后，点这里打开只读小窗。放大后可以允许操作，关闭小窗不会中断任务。")
        };
        OnboardingOverlay.ShowStep((int)_onboardingStep, title, description);
    }

    private Rect OnboardingBounds(FrameworkElement target)
    {
        if (!target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0
            || !WindowOverlayRoot.IsAncestorOf(target)) {
            throw new InvalidOperationException($"新手指引目标未完成布局：{target.Name}。");
        }
        return target.TransformToAncestor(WindowOverlayRoot)
            .TransformBounds(new Rect(new WpfSize(target.ActualWidth, target.ActualHeight)));
    }

    private Rect VisibleOnboardingBounds(FrameworkElement target, double padding = 0)
    {
        var bounds = OnboardingBounds(target);
        var clip = OnboardingClipBounds(target);
        bounds.Intersect(clip);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) {
            throw new InvalidOperationException("新手指引目标不在可见区域。");
        }
        bounds.Inflate(padding, padding);
        bounds.Intersect(clip);
        return bounds;
    }

    private Rect OnboardingClipBounds(FrameworkElement target)
    {
        var bounds = new Rect(new WpfSize(WindowOverlayRoot.ActualWidth, WindowOverlayRoot.ActualHeight));
        for (var parent = VisualTreeHelper.GetParent(target); parent != WindowOverlayRoot && parent is not null;
            parent = VisualTreeHelper.GetParent(parent)) {
            if (parent is FrameworkElement element && (element.ClipToBounds || element is ScrollContentPresenter)) {
                bounds.Intersect(OnboardingBounds(element));
            }
        }
        return bounds;
    }

    private void UpdateOnboardingGeometry(bool animate = false)
    {
        var target = ResolveOnboardingTarget();
        var bounds = VisibleOnboardingBounds(target, padding: 6);
        var window = new Rect(new WpfSize(WindowOverlayRoot.ActualWidth, WindowOverlayRoot.ActualHeight));
        var changed = bounds != _onboardingTargetBounds || window != _onboardingWindowBounds;
        if (changed) {
            var previous = _onboardingHole.Rect;
            _onboardingHole.BeginAnimation(RectangleGeometry.RectProperty, null);
            _onboardingHole.Rect = bounds;
            _onboardingHole.RadiusX = _onboardingHole.RadiusY = _onboardingStep == OnboardingStep.ShowDesktop ? 12 : 14;
            if (animate && SystemParameters.ClientAreaAnimation && !previous.IsEmpty && previous.Width > 0) {
                _onboardingHole.BeginAnimation(RectangleGeometry.RectProperty,
                    new RectAnimation(previous, bounds, TimeSpan.FromMilliseconds(200)) {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    });
            }
            _onboardingTargetBounds = bounds;
            _onboardingWindowBounds = window;
            OnboardingOverlay.OnboardingDim.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(window), _onboardingHole);
            OnboardingOverlay.OnboardingOutline.Data = _onboardingHole;
        }
        PlaceOnboardingPopover(window, bounds, OnboardingBounds(target));
    }

    private void PlaceOnboardingPopover(Rect window, Rect target, Rect control)
    {
        const double inset = 16;
        const double gap = 14;
        var topInset = Math.Max(inset, OnboardingBounds(ShellContent).Top + inset);
        var popover = OnboardingOverlay.OnboardingPopover;
        popover.Width = Math.Min(OnboardingView.CardWidth, Math.Max(1, window.Width - inset * 2));
        popover.MaxHeight = Math.Max(1, window.Height - topInset - inset);
        popover.Measure(new WpfSize(popover.Width, popover.MaxHeight));
        var size = popover.DesiredSize;
        var middle = target.Top + (target.Height - size.Height) / 2;
        var left = new Rect(target.Left - gap - size.Width, middle, size.Width, size.Height);
        var right = new Rect(target.Right + gap, middle, size.Width, size.Height);
        var bottom = new Rect(control.Right - size.Width, target.Bottom + gap, size.Width, size.Height);
        var top = new Rect(control.Right - size.Width, target.Top - gap - size.Height, size.Width, size.Height);
        Rect[] candidates = _onboardingStep switch {
            OnboardingStep.TaskDetails => [right, bottom, left, top],
            OnboardingStep.ShowDesktop => [
                new Rect(OnboardingBounds(RuntimeSidebar).Left - 52 - size.Width,
                    control.Top - 26, size.Width, size.Height),
                left, bottom, right, top
            ],
            _ => [bottom, left, right, top]
        };
        Rect chosen = Rect.Empty;
        var leastOverlap = double.MaxValue;
        foreach (var candidate in candidates) {
            var placed = new Rect(
                Math.Clamp(candidate.X, inset, Math.Max(inset, window.Width - inset - size.Width)),
                Math.Clamp(candidate.Y, topInset, Math.Max(topInset, window.Height - inset - size.Height)),
                size.Width, size.Height);
            var intersection = Rect.Intersect(placed, target);
            var overlap = intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
            if (overlap < leastOverlap) {
                leastOverlap = overlap;
                chosen = placed;
            }
            if (overlap == 0) {
                break;
            }
        }
        if (popover.RenderTransform is not TranslateTransform current || current.X != chosen.X || current.Y != chosen.Y) {
            popover.RenderTransform = new TranslateTransform(chosen.X, chosen.Y);
        }
    }

    private void Onboarding_LayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsOnboardingVisible || _onboardingChangingStep
            || _onboardingLayoutOperation is { Status: DispatcherOperationStatus.Pending }) {
            return;
        }
        _onboardingLayoutOperation = Dispatcher.BeginInvoke(() => {
            if (!IsOnboardingVisible || _onboardingChangingStep) {
                return;
            }
            try {
                UpdateOnboardingGeometry();
            } catch (Exception exception) {
                FailOnboarding(exception);
            }
        }, DispatcherPriority.Background);
    }


    private void Onboarding_SystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation) {
            _onboardingHole.BeginAnimation(RectangleGeometry.RectProperty, null);
            OnboardingOverlay.OnboardingPopover.BeginAnimation(OpacityProperty, null);
        }
    }

    private void OnboardingNext_Click(object sender, RoutedEventArgs e)
    {
        if (!IsOnboardingVisible || _onboardingChangingStep) {
            return;
        }
        if (_onboardingStep == OnboardingStep.ShowDesktop) {
            EndOnboarding(handled: true);
        } else {
            _onboardingStep++;
            _ = ShowOnboardingStepAsync(animate: true);
        }
    }

    private void OnboardingPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (IsOnboardingVisible && !_onboardingChangingStep && _onboardingStep > OnboardingStep.AddTask) {
            _onboardingStep--;
            _ = ShowOnboardingStepAsync(animate: true);
        }
    }

    private void OnboardingSkip_Click(object sender, RoutedEventArgs e) => EndOnboarding(handled: true);

    private void Onboarding_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsOnboardingVisible && e.NewFocus is DependencyObject target && target != OnboardingOverlay.OnboardingPopover
            && !OnboardingOverlay.OnboardingPopover.IsAncestorOf(target)) {
            e.Handled = true;
        }
    }

    private void FailOnboarding(Exception exception)
    {
        _logger.Warn($"新手指引已取消：mode={(_onboardingReplay ? "replay" : "auto")}，step={(int)_onboardingStep + 1}。",
            exception);
        _settings.ReplayOnboarding.Status = "暂时无法显示新手指引，请稍后重试。";
        EndOnboarding(handled: false);
    }

    private void EndOnboarding(bool handled)
    {
        var markHandled = handled && _onboardingActive && _onboardingShown && !_onboardingReplay;
        var restoreReplay = _onboardingActive && _onboardingReplay && !_onboardingClosed && !_exitInProgress;
        _onboardingAutoEnded = true;
        _onboardingActive = false;
        _onboardingChangingStep = false;
        CancelOnboardingStart();
        CancelOnboardingStep();
        _onboardingLayoutOperation?.Abort();
        WindowOverlayRoot.LayoutUpdated -= Onboarding_LayoutUpdated;
        PreviewGotKeyboardFocus -= Onboarding_PreviewGotKeyboardFocus;
        SystemParameters.StaticPropertyChanged -= Onboarding_SystemParametersChanged;
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        _onboardingTargetBounds = Rect.Empty;
        RefreshOnboardingRuntimePanel();
        SetOnboardingTaskShelf(false);
        try {
            if (restoreReplay) {
                TaskPlanEditor.RestoreView(_onboardingPreviousView!);
                SwitchSection(MainSection.Home);
            }
            if (!_onboardingClosed && !_exitInProgress) {
                if (_onboardingPreviousFocus is UIElement { IsVisible: true, IsEnabled: true, Focusable: true }
                    element) {
                    element.Focus();
                } else {
                    Sidebar.Home.Focus();
                }
            }
        } catch (Exception exception) {
            Keyboard.ClearFocus();
            _logger.Warn("恢复新手指引前的界面状态失败。", exception);
        } finally {
            _onboardingPreviousFocus = null;
        }
        if (markHandled && _onboardingPreferences?.MarkHandled() == false) {
            _settings.ReplayOnboarding.Status = "未能保存新手指引状态，下次启动可能再次显示。";
        }
        ReevaluateOnboarding();
    }

    private void CancelOnboardingStep()
    {
        _onboardingTask = null;
        _onboardingStepCancellation?.Cancel();
        _onboardingStepCancellation = null;
        _onboardingHole.BeginAnimation(RectangleGeometry.RectProperty, null);
        OnboardingOverlay.OnboardingPopover.BeginAnimation(OpacityProperty, null);
    }

    private void CancelOnboardingStart()
    {
        _onboardingStartCancellation?.Cancel();
        _onboardingStartCancellation = null;
    }

    private void PauseOnboarding()
    {
        CancelOnboardingStep();
        _onboardingPaused = true;
        _onboardingChangingStep = false;
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        RefreshOnboardingRuntimePanel();
        Keyboard.ClearFocus();
    }

    private void ReevaluateOnboarding()
    {
        if (_logger is null || _settings is null || _onboardingClosed) {
            return;
        }
        // Replay waits for runtime commands to be available, the same gate that lets a running tour continue.
        var commandsUnavailable = !OnboardingCommandsAvailable;
        _settings.ReplayOnboarding.IsEnabled = _projectPlan is not null && !commandsUnavailable
            && !_onboardingActive && !IsGlobalModalOpen;
        _settings.ReplayOnboarding.ToolTip = _projectPlan is null ? "项目加载后可查看新手指引"
            : commandsUnavailable ? "当前操作完成后可查看新手指引" : null;
        if (_onboardingActive) {
            if (!CanPresentOnboarding) {
                PauseOnboarding();
            } else if (_onboardingPaused) {
                _onboardingPaused = false;
                _ = ShowOnboardingStepAsync(animate: false);
            }
            return;
        }
        // A launch by the system has nobody to answer the tour; it is left for the next manual launch.
        var ready = !_launchCommand.AutoStart && !_onboardingAutoEnded && _onboardingStartupCompleted
            && CanPresentOnboarding
            && _onboardingPreferences?.ShouldOfferAutomatically == true && _projectPlan!.LoadWarning is null;
        if (!ready) {
            CancelOnboardingStart();
        } else if (_onboardingStartCancellation is null) {
            _ = StartOnboardingAfterLayoutAsync();
        }
    }

    private async Task StartOnboardingAfterLayoutAsync()
    {
        var cancellation = new CancellationTokenSource();
        _onboardingStartCancellation = cancellation;
        try {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, cancellation.Token);
            await Task.Delay(400, cancellation.Token);
            if (CanPresentOnboarding && !_onboardingAutoEnded && !_onboardingActive) {
                StartOnboarding(replay: false);
            }
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
        } catch (Exception exception) {
            FailOnboarding(exception);
        } finally {
            if (_onboardingStartCancellation == cancellation) {
                _onboardingStartCancellation = null;
            }
            cancellation.Dispose();
        }
    }
}
