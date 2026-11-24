using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Gui.Views;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
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
            Require(list.IsVisible && window.TaskPlanEditor.FindName("ConfigurationTabs") is null,
                "任务配置应从执行计划上方移到侧栏。");

            window.SetPendingOperation(PendingOperation.PreparingEnvironment);
            window.UpdateLayout();
            Require(list.IsVisible && scroll.ScrollableHeight > 0 && add.IsVisible && !add.IsEnabled
                && badge.IsVisible && !menu.IsEnabled, "锁定时应保留列表和说明、禁用管理操作。");
            var priorOffset = scroll.VerticalOffset;
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) {
                RoutedEvent = UIElement.MouseWheelEvent
            });
            PumpUntil(() => scroll.VerticalOffset > priorOffset, "锁定时列表应可用滚轮滚动。");
            ClickConfiguration(window, last);
            Require(list.ActiveConfigurationId == first, "锁定时不能切换配置。");

            window.SetPendingOperation(PendingOperation.None);
            ClickConfiguration(window, last);
            PumpUntil(() => scroll.VerticalOffset >= scroll.ScrollableHeight - PixelTolerance,
                "激活列表末尾的配置时应滚动显示它。");
            var lastRow = ConfigurationRows(window).Last();
            var longName = lastRow.NameLabel;
            Require(lastRow.SelectButton.IsActive && longName.TextTrimming == TextTrimming.CharacterEllipsis
                && lastRow.ActualWidth <= scroll.ViewportWidth + PixelTolerance && add.IsVisible,
                "长名字应省略显示，当前行与新建入口应保持可见。");

            typeof(TaskPlanEditor).GetMethod("ShowProjectValidationError", flags)!
                .Invoke(window.TaskPlanEditor, [new InvalidDataException("配置校验失败")]);
            ClickConfiguration(window, first);
            Require(list.IsVisible && list.ActiveConfigurationId == first, "配置校验失败时应仍能切换配置。");
            var drafts = (System.Collections.IDictionary)typeof(TaskPlanEditor).GetField("_invalidInputDrafts", flags)!
                .GetValue(window.TaskPlanEditor)!;
            drafts[(first, "ServerRange", "server_range")] = new InputDraft("978-,", "格式错误");
            ClickConfiguration(window, last);
            Require(list.IsVisible && list.ActiveConfigurationId == last, "无效参数草稿应允许切换配置。");

            typeof(MainWindow).GetMethod("ShowProjectUnavailableState", flags)!
                .Invoke(window, ["项目无法加载", "detail"]);
            Require(!list.IsVisible && !menu.IsVisible && window.Sidebar.Home.IsVisible,
                "项目无法加载时仅保留返回首页入口。");
        });
    }
}
