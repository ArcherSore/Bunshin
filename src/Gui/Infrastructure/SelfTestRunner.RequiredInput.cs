using System.Text.Json.Nodes;
using System.Windows.Automation;
using ProjectModel;
using Gui.Views;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    // An input whose default fails its own verify is a field the user must fill in: it loads, other parameters still
    // save, and only Start is blocked until a valid value is saved.
    private static void VerifyRequiredInputDefault(AppLogger logger, string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "required-input"));
        AddRequiredInput(directory);
        var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        ProjectOptionEditor Recipient() =>
            project.GetConfiguration("RealTask").TaskOptions.Single(option => option.Name == "Recipient");
        project.SetSelectedCase("Mode", "Minimal");
        Require(Recipient().Inputs.Single().Error == "Fill in the recipient"
            && Recipient().StartBlocker == "Fill in the recipient"
            && Throws<InvalidOperationException>(() => project.CreateRunStartAttempt())
            && project.CheckInputValue("Recipient", "recipient", " ") is not null,
            "默认值未通过 verify 时项目仍应加载、其他参数可保存，但不能开始运行，也不能保存空白值。");
        project.SetInputValue("Recipient", "recipient", "Kakashi");
        Require(Recipient().StartBlocker is null && project.CreateRunStartAttempt().Plan.Items.Single()
            .PipelineOverride.GetRawText().Contains("\"to\":\"Kakashi\"", StringComparison.Ordinal),
            "填写合法值后必须可以开始运行，并替换进 pipeline_override。");

        RunOnboardingScenario(logger, Path.Combine(testDirectory, "required-input-window"), (window, _) => {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            EndTour(window);
            SetOnboardingField(window, "_expandedTaskName", "RealTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            var items = (System.Windows.DependencyObject)window.FindName("PlanItemsPanel");
            var editor = ConfigurationDescendants(items).OfType<System.Windows.Controls.TextBox>()
                .Single(box => AutomationProperties.GetName(box) == "Recipient");
            ParameterRowPanel Row() => ConfigurationDescendants(items).OfType<ParameterRowPanel>()
                .Single(panel => ConfigurationDescendants(panel).Contains(editor));
            bool Note() => ConfigurationDescendants(Row()).OfType<System.Windows.Controls.TextBlock>()
                .Any(text => text.Text == "Fill in the recipient");
            Require(Row().IsInvalid && Note() && editor.BorderBrush == window.FindResource("Brush.Error")
                && typeof(MainWindow).GetMethod("RevealInvalidParameter", flags)!.Invoke(window, [false]) is not null,
                "未填写的必填输入必须标红、显示 pattern_msg，并阻止开始。");

            editor.Text = "Kakashi";
            typeof(MainWindow).GetMethod("CommitOptionInput", flags)!.Invoke(window, [editor, false]);
            Require(!Row().IsInvalid && !Note()
                && editor.ReadLocalValue(System.Windows.Controls.Control.BorderBrushProperty)
                    == System.Windows.DependencyProperty.UnsetValue,
                "保存合法值后红色标记、提示与边框必须消失。");
        }, AddRequiredInput);
    }

    private static void VerifyUpgradedInputEditors(AppLogger logger, string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "upgraded-input-editors"));
        var path = Path.Combine(directory, "interface.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        root["task"]![0]!["option"] = new JsonArray("Profile");
        root["option"]!["Profile"] = JsonNode.Parse("""
            {"type":"input","inputs":[{"name":"name","default":"guest"},{"name":"code","password":true},
              {"name":"alias","default":"guest"}]}
            """);
        File.WriteAllText(path, root.ToJsonString());
        var config = Path.Combine(directory, "config.json");
        var previous = ProjectPlanModule.Open(directory, config);
        previous.SetInputValue("Profile", "name", "");
        previous.SetInputValue("Profile", "code", SecretInputValue);
        var inputs = root["option"]!["Profile"]!["inputs"]!;
        inputs[0]!["verify"] = "^admin$";
        inputs[0]!["pattern_msg"] = "$name_hint";
        root["languages"] = new JsonObject { ["zh_cn"] = "zh.json" };
        File.WriteAllText(Path.Combine(directory, "zh.json"), """{"name_hint":"请填写管理员名称"}""");
        inputs[1]!["verify"] = "^[0-9]+$";
        inputs[1]!["pattern_msg"] = "$missing_hint";
        inputs[2]!["default"] = "新版默认";
        File.WriteAllText(path, root.ToJsonString());
        var project = ProjectPlanModule.Open(directory, config);
        WithPlanWindow(logger, testDirectory, project, (window, items) => {
            SetOnboardingField(window, "_expandedTaskName", "RealTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            System.Windows.Controls.TextBox Editor(string name) => ConfigurationDescendants(items)
                .OfType<System.Windows.Controls.TextBox>().Single(box => AutomationProperties.GetName(box) == name);
            var name = Editor("Profile · name");
            var code = (Wpf.Ui.Controls.PasswordBox)Editor("Profile · code");
            var alias = Editor("Profile · alias");
            string Status(System.Windows.Controls.TextBox editor) => AutomationProperties.GetItemStatus(editor);
            bool HasNote(string message) => ConfigurationDescendants(items).OfType<System.Windows.Controls.TextBlock>()
                .Any(note => note.Text == message);
            var codeHint = Status(code);
            Require(name.Text == "" && Status(name) == "请填写管理员名称" && HasNote(Status(name))
                && code.Password == SecretInputValue && codeHint.Any(c => c is >= '一' and <= '鿿')
                && !codeHint.Contains(SecretInputValue) && !codeHint.Contains("$missing_hint") && HasNote(codeHint)
                && alias.Text == "新版默认" && Status(alias).Length == 0,
                "首次打开新版字段应定位已存空串及非空旧值的错误，缺键字段跟随新默认值。");

            code.Password = "draft";
            name.Text = "admin";
            Require(Status(name).Length == 0 && !HasNote("请填写管理员名称") && Status(code).Length != 0,
                "输入时应立即更新本字段错误，修好一个字段不应清除另一字段的错误。");
            name.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, 0, name, null) {
                RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent
            });
            InvokeOnboarding(window, "RenderTaskPlan");
            var saved = ProjectPlanModule.Open(directory, config).GetConfiguration("RealTask")
                .TaskOptions.Single(option => option.Name == "Profile").Inputs;
            Require(saved[0].Value == "admin" && saved[0].Error is null && saved[1].Value == SecretInputValue
                && saved[1].Error is not null
                && ((Wpf.Ui.Controls.PasswordBox)Editor("Profile · code")).Password == "draft",
                "逐项保存后，另一字段的已存密码与尚未保存的草稿必须分别保留。");
        });
    }

    private static void AddRequiredInput(string directory)
    {
        var interfacePath = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        json["task"]![0]!["option"] = new JsonArray("Mode", "Recipient");
        json["option"]!["Recipient"] = JsonNode.Parse("""
            {"type": "input", "label": "Recipient", "inputs": [
              {"name": "recipient", "default": "", "pipeline_type": "string", "verify": ".*\\S.*",
               "pattern_msg": "Fill in the recipient"}],
             "pipeline_override": {"Gift": {"to": "{recipient}"}}}
            """);
        File.WriteAllText(interfacePath, json.ToJsonString());
    }
}
