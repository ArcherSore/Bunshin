using System.Reflection;
using System.Windows;
using Gui.Views;
using ProjectModel;
using Protocol;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    // The schedule page edits one configuration's policies on a shared week timeline and saves each change at once.
    private static void VerifySchedulePage(AppLogger logger, string directory)
    {
        var clock = new ScheduleTestClock();
        clock.Advance(new TimeSpan(2, 6, 0, 18)); // Wednesday 2026-11-18 14:00 in China Standard Time.
        WithRuntimeControlWindow(logger, directory, window => {
            var project = ProjectOf(window);
            var daily = project.ActiveConfigurationId;
            var workdays = project.AddSchedulePolicy(daily, "工作日早晚");
            project.UpdateSchedulePolicy(daily, workdays with { Times = [new(8, 0), new(20, 0)] });
            var empty = project.CreateConfiguration();
            var evening = project.AddSchedulePolicy(empty, "晚间");
            project.UpdateSchedulePolicy(empty, evening with { Times = [new(20, 0)] });
            project.ActivateConfiguration(daily);

            ClickOnboarding(window, "ScheduleNavigationItem");
            var view = (ScheduleView)window.FindName("ScheduleView");
            Require(view.IsVisible && !((UIElement)window.FindName("HomeView")).IsVisible
                && ((RailItem)window.FindName("ScheduleNavigationItem")).IsActive,
                "点击定时应切换到定时页。");
            var preview = (SchedulePreview)Field(view, "_preview")!;
            var b = project.Configurations.Single(c => c.Id == empty);
            Require(preview.NextRun is { Winner.Id: var winner, Time: var at } && winner == daily
                && at == new DateTime(2026, 11, 18, 20, 0, 0), "下次运行应是今天 20:00 的可运行配置。");
            Require(preview.ListState(b).Outcome == ScheduleOutcome.Invalid
                && preview.Issues(b, b.SchedulePolicies[0]).Any(issue => issue.Error),
                "无法运行的配置应在列表和编辑区预告，原因来自开始任务的校验。");
            project.ActivateConfiguration(empty);
            project.AddItem(PlanItemKind.MaaTask, project.Tasks[0].Name);
            project.ActivateConfiguration(daily);
            view.Refresh();
            preview = (SchedulePreview)Field(view, "_preview")!;
            b = project.Configurations.Single(c => c.Id == empty);
            Require(preview.ListState(b).Outcome == ScheduleOutcome.Skip
                && preview.Issues(b, b.SchedulePolicies[0]).Any(issue => !issue.Error),
                "同时到点、排在后面的配置应在列表和编辑区预告跳过。");
            Require(view.ConfigurationList.Children.Count == 2, "列表应列出每份配置。");

            Policies(project, daily, out var policy);
            Call(view, "Timeline_EmptyClicked", DayOfWeek.Wednesday, new TimeOnly(21, 0));
            Require(Policies(project, daily, out policy).Count == 1 && !policy.Times.Contains(new(21, 0)),
                "在当前定时包含的星期点击空白处，应先询问范围而不是直接改动。");
            ChooseScope(view, 2);
            Require(Policies(project, daily, out policy).Count == 1 && policy.Times.Contains(new(21, 0)),
                "选择勾选的星期都加，应把时间加入这条定时。");
            Call(view, "Timeline_EmptyClicked", DayOfWeek.Saturday, new TimeOnly(10, 0));
            var added = Policies(project, daily, out _).Single(p => p.Id != policy.Id);
            Require(added.Days.SequenceEqual([DayOfWeek.Saturday]) && added.Times.SequenceEqual([new TimeOnly(10, 0)]),
                "在当前定时不包含的星期点击，应新建一条只含该时间的定时。");

            var pin = (Func<Guid, TimeOnly, TimelinePin>)((id, time) => view.Timeline.Pins.First(p =>
                p.PolicyId == id && p.Time == time));
            Call(view, "Timeline_PinClicked", pin(policy.Id, new(21, 0)));
            Call(view, "Timeline_PinMoved", pin(policy.Id, new(21, 0)), new TimeOnly(21, 5));
            Require(Policies(project, daily, out policy).Count == 2 && policy.Times.Contains(new(21, 5))
                && !policy.Times.Contains(new(21, 0)), "拖动时间点应改成新时间。");
            ((System.Windows.Controls.TextBox)Field(view, "_timeEditor")!).Text = "21:07";
            Call(view, "CommitSelectedTime");
            Require(Policies(project, daily, out policy).Count == 2 && policy.Times.Contains(new(21, 7)),
                "精确时间应可改到任意分钟。");
            view.PolicyName.Text = "  ";
            Call(view, "CommitName");
            Require(Policies(project, daily, out policy).Count == 2 && policy.Name == "工作日早晚"
                && view.PolicyName.Text == "工作日早晚", "清空名称后离开输入框应恢复原名称。");
            Call(view, "Timeline_DayClicked", DayOfWeek.Monday);
            Require(Policies(project, daily, out policy).Count == 2 && !policy.Days.Contains(DayOfWeek.Monday),
                "点击星期应把这一天移出当前定时。");
            Call(view, "Timeline_PinDoubleClicked", view.Timeline.Pins.First(p => p.PolicyId == policy.Id
                && p.Day == DayOfWeek.Wednesday && p.Time == new TimeOnly(8, 0)));
            ChooseScope(view, 1);
            var split = Policies(project, daily, out policy);
            Require(!policy.Days.Contains(DayOfWeek.Wednesday) && policy.Times.Contains(new(8, 0))
                && split.Any(p => p.Days.SequenceEqual([DayOfWeek.Wednesday]) && !p.Times.Contains(new(8, 0))
                    && p.Times.Contains(new(20, 0))),
                "双击删除时只删周三，应把周三拆成一条不含该时间的定时，其他星期不变。");

            Call(view, "Timeline_PinClicked", view.Timeline.Pins.First(p => p.ConfigurationId == empty));
            Require(view.Timeline.Pins.Where(p => p.Role != TimelinePinRole.Other).All(p => p.ConfigurationId == empty),
                "点击其他配置的时间点应切换到该配置。");
            view.StatusCapsule.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(((UIElement)window.FindName("HomeView")).IsVisible && !view.IsVisible,
                "点击状态胶囊应回到首页。");
        }, scheduleClock: clock);

        static IReadOnlyList<SchedulePolicy> Policies(ProjectPlanModule project, Guid id, out SchedulePolicy first)
        {
            var policies = project.GetSchedulePolicies(id);
            first = policies[0];
            return policies;
        }

        static void ChooseScope(ScheduleView view, int index)
        {
            var menu = (System.Windows.Controls.ContextMenu)Field(view, "_scopeMenu")!;
            ((System.Windows.Controls.MenuItem)menu.Items[index]).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            menu.IsOpen = false;
        }

        static object? Field(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

        static void Call(object target, string name, params object[] arguments) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    }
}
