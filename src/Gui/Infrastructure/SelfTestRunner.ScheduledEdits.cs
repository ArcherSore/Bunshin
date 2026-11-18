using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Gui.Views;
using Protocol;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyScheduledEdits(AppLogger logger, string directory)
    {
        RunOnboardingScenario(logger, directory, (window, _) => {
            CompleteOnboardingStartup(window);
            EndTour(window);
            var project = ProjectOf(window);
            var id = project.ActiveConfigurationId;
            var other = project.CreateConfiguration();
            project.AddTask("Plain");
            project.ActivateConfiguration(id);
            project.AddTask("Plain");
            OpenScheduledTask(window);
            var editor = ConfigurationDescendants(Named<DependencyObject>(window, "PlanItemsPanel"))
                .OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Recipient");
            window.Activate();
            Require(ReferenceEquals(Keyboard.Focus(editor), editor), "切换前应正在编辑原配置的参数。");
            editor.Text = " ";
            ScheduledRunDisposition Start(Guid target)
            {
                var start = window.TryStartScheduledRunAsync(target);
                PumpUntil(() => start.IsCompleted, "定时配置校验和模拟准备必须返回。");
                project = ProjectOf(window);
                return start.Result.Disposition;
            }
            Require(Start(other) == ScheduledRunDisposition.Admitted && project.ActiveConfigurationId == other,
                "原配置正在编辑的无效输入应保存为草稿，并允许定时配置启动。");
            Require(Start(id) == ScheduledRunDisposition.Invalid && project.ActiveConfigurationId == other,
                "非当前配置中可以执行到的无效输入也应阻止切换。");
            project.ActivateConfiguration(id);
            Require(Start(id) == ScheduledRunDisposition.Invalid, "当前可执行任务中的无效输入应阻止定时执行。");
            project.SetSelectedCase("Mode", "Minimal");
            Require(Start(id) == ScheduledRunDisposition.Admitted, "已离开的分支中的旧草稿不应阻止定时执行。");
            project.SetSelectedCase("Mode", "Default");
            project.SetItemEnabled(PlanItemKind.MaaTask, "RealTask", false);
            Require(Start(id) == ScheduledRunDisposition.Admitted, "临时取消执行的任务中的草稿不得影响其他任务。");
            project.RemoveTask("RealTask");
            Require(Start(id) == ScheduledRunDisposition.Admitted, "被移除任务的草稿不得影响剩余任务。");
        }, beforeLoad: projectDirectory => {
            AddRequiredInput(projectDirectory);
            var file = Path.Combine(projectDirectory, "interface.json");
            var definition = JsonNode.Parse(File.ReadAllText(file))!;
            definition["option"]!["Recipient"]!["inputs"]![0]!["default"] = "valid";
            definition["option"]!["Mode"]!["cases"]![0]!["option"] = new JsonArray("Recipient");
            definition["task"]![0]!["option"] = new JsonArray("Mode");
            var plain = definition["task"]![0]!.DeepClone();
            plain["name"] = "Plain";
            plain["option"] = new JsonArray();
            definition["task"]!.AsArray().Add(plain);
            File.WriteAllText(file, definition.ToJsonString());
        }, prepareEnvironment: (_, _, _) => throw new InvalidOperationException("模拟准备到此结束"));

        var prepared = false;
        RunOnboardingScenario(logger, Path.Combine(directory, "updated-target"), (window, projectDirectory) => {
            CompleteOnboardingStartup(window);
            EndTour(window);
            var project = ProjectOf(window);
            var a = project.ActiveConfigurationId;
            project.SetInputValue("Recipient", "recipient", "old");
            var b = project.CreateConfiguration();
            project.AddTask("RealTask");
            project.SetInputValue("Recipient", "recipient", "new");
            EditInterface(projectDirectory, root => {
                root["option"]!["Recipient"]!["inputs"]![0]!["verify"] = "^new$";
            });
            var run = window.TryStartScheduledRunAsync(a);
            PumpUntil(() => run.IsCompleted, "资源更新后应按目标配置重新校验。");
            Require(run.Result.Disposition == ScheduledRunDisposition.Invalid
                && ProjectOf(window).ActiveConfigurationId == b && !prepared,
                "活动配置 B 的参数有效时，A 在新版规则下失效的参数仍必须阻止定时启动并保留 B。");
        }, beforeLoad: AddRequiredInput, prepareEnvironment: (_, _, _) => {
            prepared = true;
            throw new InvalidOperationException("模拟准备结束");
        });

        foreach (var rename in new[] { false, true }) {
            var clock = new ScheduleTestClock();
            RunOnboardingScenario(logger, Path.Combine(directory, $"locked-{rename}"), (window, projectDirectory) => {
                CompleteOnboardingStartup(window);
                EndTour(window);
                var project = ProjectOf(window);
                var id = project.ActiveConfigurationId;
                var target = project.CreateConfiguration();
                project.AddTask("RealTask");
                project.SetInputValue("Recipient", "recipient", "target");
                project.RenameConfiguration(target, "目标");
                project.AddSchedulePolicy(target, "工作日");
                var second = project.CreateConfiguration();
                project.AddTask("RealTask");
                project.SetInputValue("Recipient", "recipient", "other");
                project.AddSchedulePolicy(second, "同刻");
                project.ActivateConfiguration(id);
                project.RenameConfiguration(id, "日常");
                project.SetInputValue("Recipient", "recipient", "saved");
                OpenScheduledTask(window);
                window.Activate();
                window.UpdateLayout();
                TextBox editor;
                if (rename) {
                    var tabs = Named<System.Windows.Controls.TabControl>(window, "ConfigurationTabs");
                    var tab = (System.Windows.Controls.TabItem)tabs.SelectedItem;
                    tab.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(tab), 0, Key.F2) { RoutedEvent = Keyboard.KeyDownEvent });
                    window.UpdateLayout();
                    editor = ConfigurationDescendants(tab).OfType<TextBox>().Single();
                } else {
                    editor = ConfigurationDescendants(Named<DependencyObject>(window, "PlanItemsPanel"))
                        .OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Recipient");
                }
                Require(ReferenceEquals(Keyboard.Focus(editor), editor), "模拟定时触发时用户应仍在编辑相关字段。");
                editor.Text = "changed";
                var failures = new List<string>();
                window.NotificationRequested += (_, n) => failures.Add(n.Title + " / " + n.Message);
                using (File.Open(Path.Combine(projectDirectory, "config", "configurations.json"),
                    FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    clock.Advance(TimeSpan.FromMinutes(1));
                    window.CheckScheduledRunsAsync().GetAwaiter().GetResult();
                    Require(ProjectOf(window).ActiveConfigurationId == id && failures is [{ } failure]
                        && failure.Contains("日常"),
                        "保存当前配置失败时应保留编辑并只提醒一次，同刻的其余配置不能重试。");
                }
                var next = window.TryStartScheduledRunAsync(target);
                PumpUntil(() => next.IsCompleted, "配置恢复可写后，仍在编辑的值应能保存并用于本次启动。");
                project = ProjectOf(window);
                Require(next.Result.Disposition == ScheduledRunDisposition.Admitted && (rename
                    ? project.Configurations.Single(c => c.Id == id).Name == "changed"
                    : project.CreateRunStartAttempt(id).Plan.Items.Single().PipelineOverride.GetRawText()
                        .Contains("changed")), "保存恢复后应使用用户当前的编辑值。");
            }, beforeLoad: AddRequiredInput, scheduleClock: clock,
                prepareEnvironment: (_, _, _) => throw new InvalidOperationException("模拟准备结束"));
        }
    }

    private static void OpenScheduledTask(MainWindow window)
    {
        window.UpdateLayout();
        var controls = ConfigurationDescendants(Named<DependencyObject>(window, "PlanItemsPanel")).ToList();
        if (controls.OfType<TextBox>().Any(box => AutomationProperties.GetName(box) == "Recipient")) {
            return;
        }
        var label = ProjectOf(window).SelectedItems.Single(task => task.Name == "RealTask").Label;
        var header = controls.OfType<Button>().Single(b => AutomationProperties.GetName(b).StartsWith(label));
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
    }
}
