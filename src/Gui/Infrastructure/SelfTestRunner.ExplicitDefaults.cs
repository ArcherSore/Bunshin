using System.Text.Json.Nodes;
using System.Windows.Automation;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Input;
using System.Windows.Interop;
using ProjectModel;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyExplicitDefaults(AppLogger logger, string testDirectory)
    {
        var directory = CreateProjectFixture(testDirectory);
        AddCheckboxOptions(directory);
        var path = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var inputs = new JsonArray();
        foreach (var name in new[] { "saved", "edited", "missing", "reset", "empty" }) {
            inputs.Add(new JsonObject {
                ["name"] = name, ["label"] = name, ["default"] = name == "empty" ? "" : "guest"
            });
        }
        json["global_option"] = new JsonArray("Login");
        json["option"]!["Login"] = new JsonObject { ["type"] = "input", ["label"] = "Login", ["inputs"] = inputs };
        File.WriteAllText(path, json.ToJsonString());
        var configPath = Path.Combine(directory, "config.json");
        var project = ProjectPlanModule.Open(directory, configPath);
        WithPlanWindow(logger, testDirectory, project, (window, panel) => {
            SetOnboardingField(window, "_expandedTaskName", "RealTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            var boxes = ConfigurationDescendants(panel).OfType<System.Windows.Controls.TextBox>().ToArray();
            System.Windows.Controls.TextBox Field(string name) =>
                boxes.Single(box => AutomationProperties.GetName(box) == $"Login · {name}");
            using var source = new HwndSource(new HwndSourceParameters("explicit-default"));
            void Submit(string name)
            {
                Field(name).RaiseEvent(new WpfKeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                });
            }
            void Blur(string name)
            {
                var box = Field(name);
                box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, box, null) {
                    RoutedEvent = Keyboard.LostKeyboardFocusEvent
                });
            }
            Blur("missing");
            Field("edited").Text = "temporary";
            Field("edited").Text = "guest";
            Blur("edited");
            Submit("saved");
            Submit("empty");
            Submit("reset");
            var list = ConfigurationDescendants(panel).OfType<System.Windows.Controls.ListBox>()
                .Single(item => AutomationProperties.GetName(item) == "Finish");
            list.SelectedIndex = 0;
            list.UnselectAll();
            project.RestoreOptionDefault(project.ActiveConfigurationId, "Login", "reset");
        });
        var stored = JsonNode.Parse(File.ReadAllText(configPath))!["Configurations"]![0]!["ExplicitOptions"]!;
        var fields = stored["Login"]!["Inputs"]!.AsObject();
        Require(fields.Count == 3 && (string?)fields["edited"] == "guest"
            && (string?)fields["saved"] == "guest" && (string?)fields["empty"] == ""
            && stored["Single"]!["SelectedCases"]!.AsArray().Count == 0,
            "等于默认的显式值、空串和空列表必须写盘，失焦和恢复默认的字段应缺键。");
        foreach (var input in inputs) {
            input!["default"] = "admin";
        }
        json["option"]!["Single"]!["default_case"] = new JsonArray("Enabled");
        json["option"]!["Tier"]!["default_case"] = new JsonArray("High");
        File.WriteAllText(path, json.ToJsonString());
        var reopened = ProjectPlanModule.Open(directory, configPath);
        var view = reopened.GetConfiguration("RealTask");
        var plan = reopened.CreateRunStartAttempt().Plan;
        (string, string)[] expected = [
            ("saved", "guest"), ("edited", "guest"), ("missing", "admin"), ("reset", "admin"), ("empty", "")
        ];
        foreach (var (name, value) in expected) {
            Require(view.GlobalOptions.Single().Inputs.Single(input => input.Name == name).Value == value
                && plan.ResolvedGlobalOptions.GetProperty("Login").GetProperty(name).GetString() == value,
                $"资源更新后编辑器和 Run Plan 应使用 {name} 字段的有效值。");
        }
        var choices = plan.Items.Single().ResolvedOptions;
        Require(view.TaskOptions.Single(option => option.Name == "Single").SelectedCases.Count == 0
            && view.TaskOptions.Single(option => option.Name == "Tier").SelectedCases.SequenceEqual(["High"])
            && choices.GetProperty("Single").GetArrayLength() == 0
            && choices.GetProperty("Tier")[0].GetString() == "High",
            "显式空列表应保持为空，缺键列表应取资源的新默认。");
    }
}
