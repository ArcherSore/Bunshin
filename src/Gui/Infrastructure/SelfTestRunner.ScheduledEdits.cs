using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Gui.Models;
using Gui.Views;
using Protocol;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyScheduledActivationSaveFailure(AppLogger logger, string directory, string root)
    {
        TwoConfigurations()(root);
        var wts = new SimulatedWts { Sessions = [7] };
        using var session = wts.CreateManager(logger);
        WithRuntimeControlWindow(logger, directory, window => {
            var project = ProjectOf(window);
            var original = project.ActiveConfigurationId;
            var target = project.Configurations.Single(c => c.Name == "周常");
            Require(original != target.Id && project.CreateRunStartAttempt(target.Id).Plan.Items.Count > 0,
                "目标配置应与当前配置不同且可执行。");
            wts.ReportedId = 7;
            var path = Path.Combine(root, "config", "configurations.json");
            // Allow ordinary reads and writes. Only replacing the destination needs delete sharing.
            using var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var saved = File.ReadAllBytes(path);
            var start = window.TryStartScheduledRunAsync(target.Id);
            PumpUntil(() => start.IsCompleted, "配置替换失败后应结束本次定时启动。");
            start.GetAwaiter().GetResult();
            var activity = window.LogLines.OfType<LogEntry>().Where(e => e.TaskLabel == "定时").ToList();
            Require(activity.Where(e => e.Level == LogLevel.Error).ToArray() is [var failure]
                && failure.Message.Contains(target.Name)
                && !activity.Any(e => e.Message.StartsWith("启动「")),
                "配置替换失败时，运行动态应显示一次目标配置失败且没有启动记录。");
            Require(wts.LogoffAttempts.Count == 0 && session.HasChildSession
                && window.TaskPlanEditor.ConfigurationTabs.SelectedItem is System.Windows.Controls.TabItem
                    { Tag: Guid selected } && selected == original && project.ActiveConfigurationId == original
                && saved.SequenceEqual(File.ReadAllBytes(path)),
                "配置替换失败必须保留当前标签和配置文件，且不注销旧分身。");
        }, session: session, captureProcesses: () => [],
            prepareEnvironment: (_, _, _) => throw new InvalidOperationException("不应准备新环境"));
    }

    private static void VerifyScheduledEdits(AppLogger logger, string directory)
    {
        RunOnboardingScenario(logger, directory, (window, _) => {
            CompleteOnboardingStartup(window);
            EndTour(window);
            var project = ProjectOf(window);
            var id = project.ActiveConfigurationId;
            var other = project.CreateConfiguration();
            project.AddItem(PlanItemKind.MaaTask, "Plain");
            project.ActivateConfiguration(id);
            project.AddItem(PlanItemKind.MaaTask, "Plain");
            OpenScheduledTask(window);
            var editor = ConfigurationDescendants(Named<DependencyObject>(window, "PlanItemsPanel"))
                .OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Recipient");
            window.Activate();
            RequireFocus(ReferenceEquals(Keyboard.Focus(editor), editor), "切换前应正在编辑原配置的参数。");
            editor.Text = " ";
            ScheduledRunDisposition Start(Guid target)
            {
                var start = window.TryStartScheduledRunAsync(target);
                PumpUntil(() => start.IsCompleted, "定时配置校验和模拟准备必须返回。");
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
            project.RemoveItem(PlanItemKind.MaaTask, "RealTask");
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

        foreach (var rename in new[] { false, true }) {
            var clock = new ScheduleTestClock();
            RunOnboardingScenario(logger, Path.Combine(directory, $"locked-{rename}"), (window, projectDirectory) => {
                CompleteOnboardingStartup(window);
                EndTour(window);
                var project = ProjectOf(window);
                var id = project.ActiveConfigurationId;
                var target = project.CreateConfiguration();
                project.AddItem(PlanItemKind.MaaTask, "RealTask");
                project.SetInputValue("Recipient", "recipient", "target");
                project.RenameConfiguration(target, "目标");
                project.AddSchedulePolicy(target, "工作日");
                var second = project.CreateConfiguration();
                project.AddItem(PlanItemKind.MaaTask, "RealTask");
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
                RequireFocus(ReferenceEquals(Keyboard.Focus(editor), editor), "模拟定时触发时用户应仍在编辑相关字段。");
                editor.Text = "changed";
                var failures = new List<string>();
                window.NotificationRequested += (_, n) => failures.Add(n.Title + " / " + n.Message);
                using (File.Open(Path.Combine(projectDirectory, "config", "configurations.json"),
                    FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    clock.Advance(TimeSpan.FromMinutes(1));
                    window.CheckScheduledRunsAsync().GetAwaiter().GetResult();
                    Require(project.ActiveConfigurationId == id && failures.Count == 1 && failures[0].Contains("日常"),
                        "保存当前配置失败时应保留编辑并只提醒一次，同刻的其余配置不能重试。");
                }
                var next = window.TryStartScheduledRunAsync(target);
                PumpUntil(() => next.IsCompleted, "配置恢复可写后，仍在编辑的值应能保存并用于本次启动。");
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
