using System.Windows;
using System.Windows.Automation;
using Gui.Views;
using Updates;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyWindowShell(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "window-shell");
        var update = new TaskCompletionSource<EngineCheckResult>();
        var download = new TaskCompletionSource<string>();
        var clock = new ScheduleTestClock();
        RunOnboardingScenario(logger, directory, (window, _) => {
            window.Width = 1180;
            window.UpdateLayout();
            var sidebar = Named<ShellSidebar>(window, "Sidebar");
            var header = Named<ShellHeader>(window, "Header");
            var titleBar = Named<Wpf.Ui.Controls.TitleBar>(header, "TitleBar");
            Require(Math.Abs(sidebar.ActualWidth - 288) < PixelTolerance
                && Math.Abs(header.ActualHeight - 44) < PixelTolerance, "默认应显示完整侧栏和 44 DIP 页眉。");

            var project = ProjectOf(window);
            var id = project.ActiveConfigurationId;
            var schedule = project.AddSchedulePolicy(id, "每天");
            var next = new TimeOnly(20, 39);
            project.UpdateSchedulePolicy(id, schedule with { Times = [next] });
            var activeName = project.Configurations.Single(configuration => configuration.Id == id).Name;
            foreach (var (name, view, title) in new[] {
                ("Schedule", "ScheduleView", "定时"), ("Help", "HelpView", "帮助"),
                ("Settings", "SettingsView", "设置"), ("Home", "HomeView", activeName)
            }) {
                var item = Named<RailItem>(sidebar, name + "NavigationItem");
                ClickOnboarding(window, name + "NavigationItem");
                Require(item.IsActive && Named<UIElement>(window, view).IsVisible && titleBar.Title == title,
                    $"{name} 应显示对应页面、页眉标题和选中状态。");
            }
            Require(AutomationProperties.GetName(Named<RailItem>(sidebar, "ScheduleNavigationItem"))
                .Contains("20:39", StringComparison.Ordinal), "定时入口应显示下次运行时间。");

            ClickOnboarding(window, "UpdateNavigationItem");
            Require(Named<UIElement>(window, "UpdateOverlay").IsVisible, "侧栏更新入口应打开更新弹窗。");
            var checking = SettingsActionFor(window, "update.check").ExecuteAsync();
            var marker = Named<UIElement>(sidebar, "UpdateNavigationBadge");
            var progress = Named<UIElement>(sidebar, "UpdateNavigationLoading");
            Require(!marker.IsVisible && progress.IsVisible, "检查更新时应有加载指示。");
            update.SetResult(new EngineCheckResult("1.0.0", new EngineUpdate("2.0.0", "notes", "opaque")));
            PumpUntil(() => checking.IsCompleted, "更新检查应完成。");
            Require(marker.IsVisible && !progress.IsVisible, "发现新版本时应显示更新标记。");
            ClickOnboarding(window, "DownloadUpdateButton");
            Require(!marker.IsVisible && progress.IsVisible, "下载更新时应显示进度标记。");
            download.SetCanceled();
            PumpUntil(() => marker.IsVisible, "取消下载后应恢复新版本标记。");
            ClickOnboarding(window, "CloseUpdateButton");

            ClickOnboarding(window, "SidebarToggleButton");
            Require(sidebar.ActualWidth < 100, "侧栏按钮应收起到图标宽度。");
            Require(Named<UIElement>(sidebar, "UpdateNavigationCornerDot").IsVisible,
                "收起侧栏后仍应看到新版本标记。");
        }, checkForUpdate: _ => update.Task, prepareUpdate: (_, _, _) => download.Task, scheduleClock: clock);

        RunOnboardingScenario(logger, directory, (window, _) => {
            window.Width = 1180;
            window.UpdateLayout();
            var sidebar = Named<ShellSidebar>(window, "Sidebar");
            Require(sidebar.ActualWidth < 100, "重开窗口后应恢复侧栏收起状态。");
            ClickOnboarding(window, "SidebarToggleButton");
            Require(Math.Abs(sidebar.ActualWidth - 288) < PixelTolerance, "再次点击按钮应展开侧栏。");
            window.Width = 920;
            window.UpdateLayout();
            Require(sidebar.ActualWidth < 100, "窄窗口应保留内容宽度并收起侧栏。");
            var home = Named<FrameworkElement>(window, "HomeView");
            var width = home.ActualWidth;
            ClickOnboarding(window, "SidebarToggleButton");
            Require(Math.Abs(sidebar.ActualWidth - 288) < PixelTolerance
                && Math.Abs(home.ActualWidth - width) < PixelTolerance, "窄窗口可展开侧栏且无须挤占首页。");
            ClickOnboarding(window, "HomeNavigationItem");
            Require(sidebar.ActualWidth < 100 && home.IsVisible, "窄窗口选择页面后应隐藏侧栏浮层。");
        });
    }
}
