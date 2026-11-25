using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Gui.Views;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifySidebarConfigurationOrder(AppLogger logger, string directory)
    {
        var clock = new ScheduleTestClock();
        var starts = new List<Guid>();
        Guid first = default;
        Guid second = default;
        WithRuntimeControlWindow(logger, directory, window => {
            var project = ProjectOf(window);
            first = project.ActiveConfigurationId;
            project.AddSchedulePolicy(first, "同一时刻");
            second = project.CreateConfiguration();
            project.AddItem(Protocol.PlanItemKind.MaaTask, "RealTask");
            project.AddSchedulePolicy(second, "同一时刻");
            project.ActivateConfiguration(first);
            InvokeEditor(window, "RenderTaskPlan");
            window.MoveSidebarConfiguration(second, 0);
            Require(ConfigurationRows(window).Select(row => row.Id).SequenceEqual([second, first])
                && project.Configurations.Select(configuration => configuration.Id).SequenceEqual([first, second]),
                "移动侧栏配置应只改变列表显示顺序。");
        }, scheduleClock: clock);
        WithRuntimeControlWindow(logger, directory, window => {
            Require(ConfigurationRows(window).Select(row => row.Id).SequenceEqual([second, first]),
                "重开后应恢复侧栏配置显示顺序。");
            var project = ProjectOf(window);
            var added = project.CreateConfiguration();
            project.ActivateConfiguration(first);
            InvokeEditor(window, "RenderTaskPlan");
            Require(ConfigurationRows(window).Select(row => row.Id).SequenceEqual([second, first, added]),
                "未保存过排序的新配置应出现在列表末尾。");
            clock.Advance(TimeSpan.FromMinutes(1));
            var tick = window.CheckScheduledRunsAsync();
            PumpUntil(() => tick.IsCompleted, "同一分钟的定时机会应已处理。");
            tick.GetAwaiter().GetResult();
            Require(starts.SequenceEqual([first]), "调整侧栏显示顺序后，同刻定时仍应由原配置获准。");
        }, scheduleClock: clock, prepareEnvironment: (project, _, _) => {
            starts.Add(project.ActiveConfigurationId);
            throw new InvalidOperationException("模拟准备结束");
        });
    }

    private static void VerifyConfigurationHeader(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "configuration-header"), (window, _) => {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var project = ProjectOf(window);
            EndTour(window);
            for (var index = 0; index < 35; index++) {
                project.CreateConfiguration();
            }
            var first = project.Configurations[0].Id;
            var last = project.Configurations[^1].Id;
            project.RenameConfiguration(last, new string('长', 100));
            project.ActivateConfiguration(first);
            InvokeEditor(window, "RenderTaskPlan");
            var list = window.Sidebar.ConfigurationList;
            var add = window.Sidebar.NewConfigurationItem;
            var scroll = list.ConfigurationScroll;
            var badge = (FrameworkElement)window.TaskPlanEditor.FindName("ConfigurationLockBadge");
            var menu = Named<UIElement>(window.Header, "ConfigurationMenuButton");
            var name = Named<UIElement>(window.Header, "ConfigurationNameButton");
            Require(list.IsVisible && window.TaskPlanEditor.FindName("ConfigurationTabs") is null,
                "任务配置应从执行计划上方移到侧栏。");

            window.SetPendingOperation(PendingOperation.PreparingEnvironment);
            window.UpdateLayout();
            Require(list.IsVisible && scroll.ScrollableHeight > 0 && add.IsVisible && !add.IsEnabled
                && badge.IsVisible && !menu.IsEnabled && !name.IsEnabled, "锁定时应保留列表和说明、禁用管理操作。");
            var priorOffset = scroll.VerticalOffset;
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) {
                RoutedEvent = UIElement.MouseWheelEvent
            });
            PumpUntil(() => scroll.VerticalOffset > priorOffset, "锁定时列表应可用滚轮滚动。");
            ClickConfiguration(window, last);
            Require(list.ActiveConfigurationId == first, "锁定时不能切换配置。");

            window.SetPendingOperation(PendingOperation.None);
            ClickConfiguration(window, last);
            var lastRow = ConfigurationRows(window).Last();
            PumpUntil(() => {
                var top = lastRow.TranslatePoint(new System.Windows.Point(), scroll).Y;
                return top >= -PixelTolerance && top + lastRow.ActualHeight <= scroll.ViewportHeight + PixelTolerance;
            }, "激活列表末尾的配置时应滚动显示整行。");
            var longName = lastRow.NameLabel;
            Require(lastRow.SelectButton.IsActive && longName.TextTrimming == TextTrimming.CharacterEllipsis
                && lastRow.ActualWidth <= scroll.ViewportWidth + PixelTolerance && add.IsVisible,
                "长名字应省略显示，当前行与新建入口应保持可见。");

            typeof(TaskPlanEditor).GetMethod("ShowProjectValidationError", flags)!
                .Invoke(window.TaskPlanEditor, [new InvalidDataException("配置校验失败")]);
            ClickConfiguration(window, first);
            Require(list.IsVisible && list.ActiveConfigurationId == first, "配置校验失败时应仍能切换配置。");
            System.Windows.Controls.TextBox ServerInput() => ConfigurationDescendants(window.TaskPlanEditor)
                .OfType<System.Windows.Controls.TextBox>()
                .Single(input => System.Windows.Automation.AutomationProperties.GetName(input) == "Server range · Server");
            SetExpandedTask(window, "RealTask");
            InvokeEditor(window, "RenderTaskPlan");
            var server = ServerInput();
            server.Text = "978-,";
            server.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, server, lastRow.SelectButton) {
                RoutedEvent = Keyboard.LostKeyboardFocusEvent
            });
            var canEdit = list.CanEdit;
            list.CanEdit = () => false;
            try {
                var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                };
                lastRow.SelectButton.RaiseEvent(press);
                Require(!press.Handled, "管理动作无法提交无效草稿时，另一个配置行仍应接收点击。");
            } finally {
                list.CanEdit = canEdit;
            }
            ClickConfiguration(window, last);
            Require(list.IsVisible && list.ActiveConfigurationId == last, "无效参数草稿应允许切换配置。");
            ClickConfiguration(window, first);
            SetExpandedTask(window, "RealTask");
            InvokeEditor(window, "RenderTaskPlan");
            Require(ServerInput().Text == "978-," && project.Configurations[0].ExplicitOptions.Count == 0,
                "返回原配置应恢复未保存的草稿且不改变已保存选项。");

            typeof(MainWindow).GetMethod("ShowProjectUnavailableState", flags)!
                .Invoke(window, ["项目无法加载", "detail"]);
            Require(!list.IsVisible && !menu.IsVisible && window.Sidebar.Home.IsVisible,
                "项目无法加载时仅保留返回首页入口。");
        });
    }
}
