using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Gui.Views;
using Point = System.Windows.Point;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyRuntimePanelCollapse(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "runtime-panel-collapse");
        RunOnboardingScenario(logger, directory, (window, _) => {
            EndTour(window);
            var panel = Named<FrameworkElement>(window, "RuntimeSidebar");
            Require(panel.IsVisible, "首页默认显示运行面板。");
            ClickOnboarding(window, "RuntimePanelToggleButton");
            Require(!panel.IsVisible, "页眉按钮应收起运行面板。");
        });
        RunOnboardingScenario(logger, directory, (window, _) => {
            EndTour(window);
            var panel = Named<FrameworkElement>(window, "RuntimeSidebar");
            Require(!panel.IsVisible, "重启后应保持运行面板收起。");
            ClickOnboarding(window, "SettingsNavigationItem");
            Require(!Named<FrameworkElement>(window, "RuntimePanelToggleButton").IsVisible,
                "设置页应隐藏运行面板按钮。");
            ClickOnboarding(window, "HomeNavigationItem");
            Require(!panel.IsVisible, "返回首页应保持运行面板收起。");
            ClickOnboarding(window, "RuntimePanelToggleButton");
            Require(panel.IsVisible, "同一个页眉按钮应重新展开运行面板。");
        });
    }

    private static void VerifyRuntimePanelSections(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "runtime-panel-sections");
        RunOnboardingScenario(logger, directory, (window, _) => {
            EndTour(window);
            var panel = Named<FrameworkElement>(window, "RuntimeSidebar");
            var desktop = Named<FrameworkElement>(window, "RuntimeControlCard");
            var activity = Named<FrameworkElement>(window, "ActivitySectionToggle");
            var log = Named<FrameworkElement>(window, "HomeLogListBox");
            Require(log.IsVisible && Top(desktop, panel) < Top(activity, panel),
                "运行动态初次使用应展开并位于桌面分身下方。");
            ClickOnboarding(window, "ActivitySectionToggle");
            Require(!log.IsVisible && activity.IsVisible, "收起区块后应保留其展开入口。");

            var grip = Named<Thumb>(window, "ActivitySectionGrip");
            grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            grip.RaiseEvent(new DragDeltaEventArgs(0, -Top(activity, panel)) { RoutedEvent = Thumb.DragDeltaEvent });
            grip.RaiseEvent(new DragCompletedEventArgs(0, -Top(activity, panel), false) {
                RoutedEvent = Thumb.DragCompletedEvent
            });
            PumpUntil(() => Top(activity, panel) < Top(desktop, panel), "拖动区块标题的手柄应改变显示顺序。");
        });
        RunOnboardingScenario(logger, directory, (window, _) => {
            EndTour(window);
            var panel = Named<FrameworkElement>(window, "RuntimeSidebar");
            var activity = Named<FrameworkElement>(window, "ActivitySectionToggle");
            var log = Named<FrameworkElement>(window, "HomeLogListBox");
            Require(!log.IsVisible && Top(activity, panel) < Top(Named<FrameworkElement>(window, "RuntimeControlCard"), panel),
                "重启后应恢复区块顺序和折叠状态。");
            ClickOnboarding(window, "ActivitySectionToggle");
            Require(log.IsVisible, "用户应能再次展开运行动态。");
        });

        static double Top(FrameworkElement element, FrameworkElement parent) =>
            element.TransformToAncestor(parent).Transform(new Point()).Y;
    }

    private static void VerifyRuntimeSidebarResize(AppLogger logger, string testDirectory)
    {
        var persisted = 0.0;
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "sidebar-resize"), (window, directory) => {
            EndTour(window);
            var home = Named<Grid>(window, "HomeView");
            var column = Named<ColumnDefinition>(window, "RuntimeSidebarColumn");
            var splitter = Named<Thumb>(window, "RuntimeSidebarSplitter");
            double pointerX = 0;
            var console = Named<FrameworkElement>(window, "RuntimeSidebar");
            var control = Named<FrameworkElement>(window, "RuntimeControlCard");
            var log = Named<FrameworkElement>(window, "HomeLogListBox");
            var controlTop = control.TransformToAncestor(home).Transform(new Point()).Y;
            var controlHeight = control.ActualHeight;
            var preference = Path.Combine(directory, "config", "runtime-sidebar-width.txt");
            double? Saved() => File.Exists(preference) ? double.Parse(File.ReadAllText(preference),
                System.Globalization.CultureInfo.InvariantCulture) : null;
            Require(Saved() is null && Math.Abs(column.Width.Value - Math.Min(
                    MainWindow.SidebarWidth(LayoutInformation.GetLayoutSlot(home).Width),
                    column.MaxWidth)) < 1, "首次使用应保持默认侧栏宽度，且不写入偏好。");

            // Small screens start the sidebar at its minimum; widen it first so it can move both ways.
            Drag(-60);
            Require(Saved() == column.Width.Value, "拖动结束后应保存侧栏宽度。");
            var saved = Saved();
            var originalWidth = console.ActualWidth;
            StartDrag();
            MoveDrag(20);
            Require(Math.Abs(console.ActualWidth - (originalWidth - 20)) < 1, "拖动尚未结束时，侧栏应实时变窄。");
            Require(Saved() == saved, "拖动过程中不应持续写入侧栏宽度。");
            MoveDrag(-40);
            Require(Math.Abs(console.ActualWidth - (originalWidth + 20)) < 1, "反向拖动应实时加宽侧栏。");
            EndDrag();
            Require(Saved() == column.Width.Value && Saved() != saved, "拖动结束后应保存最终宽度。");
            AssertLayout();

            // Window resizing must clamp the displayed width without discarding the user's wider preference.
            Drag(-10000);
            var widest = console.ActualWidth;
            Require(Math.Abs(widest - Math.Min(540, column.MaxWidth)) < 1,
                "右侧栏应有最大宽度边界。");
            window.Width = 920;
            window.Height = 640;
            PumpOnboarding();
            Require(home.ColumnDefinitions[0].ActualWidth >= 420 - PixelTolerance && console.ActualWidth < 540,
                $"窄窗口应限制侧栏宽度并保留任务区最小宽度：任务区={home.ColumnDefinitions[0].ActualWidth}，"
                + $"侧栏={console.ActualWidth}，主页={home.ActualWidth}，上界={column.MaxWidth}，窗口={window.ActualWidth}。");
            AssertLayout();
            ClickOnboarding(window, "TaskShelfHeaderButton");
            var badge = Named<FrameworkElement>(window, "AddedTasksBadge");
            Named<TextBlock>(window, "AddedTasksText").Text = "已添加 1 项";
            badge.Visibility = Visibility.Visible;
            PumpOnboarding();
            var shelf = Named<FrameworkElement>(window, "TaskShelfContent");
            var search = Named<FrameworkElement>(window, "TaskSearchBox");
            var badgeBounds = badge.TransformToAncestor(shelf).TransformBounds(new Rect(badge.RenderSize));
            var searchBounds = search.TransformToAncestor(shelf).TransformBounds(new Rect(search.RenderSize));
            Require(badgeBounds.Right <= searchBounds.Left + PixelTolerance
                && searchBounds.Right <= shelf.ActualWidth + PixelTolerance,
                "最窄任务区中可用任务标题、新增提示和搜索框不能重叠或溢出。");
            ClickOnboarding(window, "TaskShelfHeaderButton");
            ClickOnboarding(window, "SettingsNavigationItem");
            Require(column.Width.Value == 0 && splitter.Visibility == Visibility.Collapsed,
                "设置页应隐藏侧栏与分隔器。");
            window.Width = 1440;
            PumpOnboarding();
            ClickHome(window);
            Require(Math.Abs(console.ActualWidth - widest) < 1, "返回首页且空间恢复后应恢复用户宽度。");

            Drag(widest - 340 + 35);
            Require(Math.Abs(console.ActualWidth - 340) < PixelTolerance, "运行面板应有最小宽度边界。");
            AssertLayout();

            StartDrag();
            MoveDrag(78);
            Require(console.IsVisible && Math.Abs(console.ActualWidth - 340) < PixelTolerance,
                "到达折叠阈值之前，应保持最小宽度。");
            MoveDrag(4);
            Require(!console.IsVisible && splitter.IsVisible, "越过折叠阈值后应隐藏面板并保留拖宽边缘。");
            MoveDrag(-78);
            Require(!console.IsVisible, "反向拖动未达到展开阈值时，应保持折叠。");
            MoveDrag(-4);
            Require(console.IsVisible && Math.Abs(console.ActualWidth - 340) < PixelTolerance,
                "在同一次拖动中反向越过阈值，应展开到最小宽度。");
            EndDrag();

            ClickOnboarding(window, "RuntimePanelToggleButton");
            Require(!console.IsVisible && splitter.IsVisible, "页眉收起面板后应保留拖宽边缘。");
            Drag(-82);
            Require(console.IsVisible && Math.Abs(console.ActualWidth - 340) < PixelTolerance,
                "从右侧边缘向左拖动应展开页眉收起的面板。");
            Drag(82);
            Require(!console.IsVisible, "松手后应保留拖动折叠状态。");
            ClickOnboarding(window, "RuntimePanelToggleButton");
            Require(console.IsVisible, "页眉按钮应可展开拖动折叠的面板。");
            Drag(-80);
            var preferred = console.ActualWidth;
            foreach (var width in new[] { 920, 1180, 1440 }) {
                window.Width = width;
                window.Height = 640;
                PumpOnboarding();
                ClickOnboarding(window, "SettingsNavigationItem");
                ClickHome(window);
                Require(Math.Abs(console.ActualWidth - preferred) < 1,
                    "窗口缩放和首页/设置切换不应覆盖仍可容纳的用户宽度。");
                AssertLayout();
            }

            KeyDown(System.Windows.Input.Key.Left);
            Require(Math.Abs(console.ActualWidth - (preferred + 10)) < 1,
                "左右方向键应可调整运行面板宽度。");
            Require(Saved() != column.Width.Value, "方向键调整过程中不应写入侧栏宽度。");
            splitter.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, splitter, null) {
                RoutedEvent = Keyboard.LostKeyboardFocusEvent
            });
            Require(Saved() == column.Width.Value, "方向键调整后离开分隔器应保存宽度。");
            preferred = console.ActualWidth;
            StartDrag();
            MoveDrag(30);
            KeyDown(System.Windows.Input.Key.Escape);
            EndDrag();
            ClickOnboarding(window, "SettingsNavigationItem");
            ClickHome(window);
            Require(Math.Abs(console.ActualWidth - preferred) < 1, "取消拖拽后应保留原宽度。");

            Drag(20);
            AssertLayout();
            persisted = Saved()!.Value;

            void StartDrag()
            {
                pointerX = splitter.TranslatePoint(new Point(), window).X;
                splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            }

            void MoveDrag(double change)
            {
                pointerX += change;
                var offset = pointerX - splitter.TranslatePoint(new Point(), window).X;
                splitter.RaiseEvent(new DragDeltaEventArgs(offset, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                PumpOnboarding();
            }

            void EndDrag() => splitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false) {
                RoutedEvent = Thumb.DragCompletedEvent
            });

            void Drag(double change)
            {
                StartDrag();
                MoveDrag(change);
                EndDrag();
            }

            void KeyDown(System.Windows.Input.Key key)
            {
                splitter.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                PumpOnboarding();
            }

            void AssertLayout()
            {
                Require(Math.Abs(control.TransformToAncestor(home).Transform(new Point()).Y - controlTop) < 1
                    && Math.Abs(control.ActualHeight - controlHeight) < 1, "运行控制顶部位置和布局应保持稳定。");
                Require(log.ActualHeight > 0 && log.TransformToAncestor(console)
                    .Transform(new Point(log.ActualWidth, log.ActualHeight)).Y <= console.ActualHeight + PixelTolerance,
                    $"日志应填充剩余高度且不溢出侧栏：日志高度={log.ActualHeight}，侧栏高度={console.ActualHeight}。");
                Require(home.ColumnDefinitions[0].ActualWidth >= 420 - PixelTolerance
                    && home.ActualWidth <= LayoutInformation.GetLayoutSlot(home).Width + PixelTolerance
                    && console.TransformToAncestor(home).Transform(new Point(console.ActualWidth, 0)).X
                        <= home.ActualWidth + PixelTolerance, "左右栏必须处于主页边界内。");
            }
        });
        VerifyRuntimeSidebarRestore(logger, testDirectory, persisted);
    }

    // A restart restores the saved width through the same clamp as window resizing; bad files fall back to default.
    private static void VerifyRuntimeSidebarRestore(AppLogger logger, string testDirectory, double persisted)
    {
        var saved = persisted.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (name, content) in new[] {
            ("restore", saved), ("restore-wide", "9999"), ("restore-narrow", "1"), ("restore-invalid", "宽")
        }) {
            RunOnboardingScenario(logger, Path.Combine(testDirectory, $"sidebar-{name}"), (window, directory) => {
                var home = Named<Grid>(window, "HomeView");
                var column = Named<ColumnDefinition>(window, "RuntimeSidebarColumn");
                var expected = name == "restore-invalid"
                    ? Math.Min(MainWindow.SidebarWidth(LayoutInformation.GetLayoutSlot(home).Width),
                        column.MaxWidth)
                    : Math.Clamp(double.Parse(content, System.Globalization.CultureInfo.InvariantCulture),
                        column.MinWidth, column.MaxWidth);
                Require(Math.Abs(column.Width.Value - expected) < PixelTolerance
                    && File.ReadAllText(Path.Combine(directory, "config", "runtime-sidebar-width.txt")) == content,
                    $"启动时应在宽度边界内恢复保存的侧栏宽度（{name}），无效值使用默认宽度，且不改写偏好。");
            }, project => {
                Directory.CreateDirectory(Path.Combine(project, "config"));
                File.WriteAllText(Path.Combine(project, "config", "runtime-sidebar-width.txt"), content);
            });
        }
    }
}
