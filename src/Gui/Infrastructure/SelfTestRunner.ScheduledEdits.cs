using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Gui.Views;
using Protocol;
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
            project.AddTask("Plain");
            SetOnboardingField(window, "_expandedTaskName", "RealTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            var editor = ConfigurationDescendants(Named<DependencyObject>(window, "PlanItemsPanel"))
                .OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Recipient");
            editor.Text = " ";
            editor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, editor, null) {
                RoutedEvent = Keyboard.LostKeyboardFocusEvent
            });
            ScheduledRunDisposition Start()
            {
                var start = window.TryStartScheduledRunAsync(id);
                PumpUntil(() => start.IsCompleted, "定时配置校验和模拟准备必须返回。");
                return start.Result.Disposition;
            }
            Require(Start() == ScheduledRunDisposition.Invalid, "当前可执行任务中的无效输入应阻止定时执行。");
            var other = project.CreateConfiguration();
            Require(Start() == ScheduledRunDisposition.Invalid && project.ActiveConfigurationId == other,
                "非当前配置中可以执行到的无效输入也应阻止切换。");
            project.ActivateConfiguration(id);
            project.SetSelectedCase("Mode", "Minimal");
            Require(Start() == ScheduledRunDisposition.Admitted, "已离开的分支中的旧草稿不应阻止定时执行。");
            project.SetSelectedCase("Mode", "Default");
            project.SetItemEnabled(PlanItemKind.MaaTask, "RealTask", false);
            Require(Start() == ScheduledRunDisposition.Admitted, "临时取消执行的任务中的草稿不得影响其他任务。");
            project.RemoveTask("RealTask");
            Require(Start() == ScheduledRunDisposition.Admitted, "被移除任务的草稿不得影响剩余任务。");
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
            RunOnboardingScenario(logger, Path.Combine(directory, $"locked-{rename}"), (window, projectDirectory) => {
                CompleteOnboardingStartup(window);
                EndTour(window);
                var project = ProjectOf(window);
                var id = project.ActiveConfigurationId;
                project.RenameConfiguration(id, "日常");
                project.SetInputValue("Recipient", "recipient", "saved");
                SetOnboardingField(window, "_expandedTaskName", "RealTask");
                InvokeOnboarding(window, "RenderTaskPlan");
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
                window.AutoRunFailed += (_, message) => failures.Add(message);
                using (File.Open(Path.Combine(projectDirectory, "config", "configurations.json"),
                    FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    var start = window.TryStartScheduledRunAsync(id);
                    Require(start.IsCompletedSuccessfully && start.Result.Disposition == ScheduledRunDisposition.Invalid
                        && failures.Count == 1 && failures[0].Contains("日常"), "编辑保存失败应立即在无人值守入口返回并提醒。");
                }
                var next = window.TryStartScheduledRunAsync(id);
                PumpUntil(() => next.IsCompleted, "配置恢复可写后，仍在编辑的值应能保存并用于本次启动。");
                Require(next.Result.Disposition == ScheduledRunDisposition.Admitted && (rename
                    ? project.Configurations.Single(c => c.Id == id).Name == "changed"
                    : project.CreateRunStartAttempt(id).Plan.Items.Single().PipelineOverride.GetRawText()
                        .Contains("changed")), "保存恢复后应使用用户当前的编辑值。");
            }, beforeLoad: AddRequiredInput,
                prepareEnvironment: (_, _, _) => throw new InvalidOperationException("模拟准备结束"));
        }
    }
}
