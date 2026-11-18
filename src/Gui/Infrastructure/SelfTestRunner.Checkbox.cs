using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Windows.Controls;
using Protocol;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using ProjectModel;
using Gui.Views;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyCaseRecovery(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "case-recovery"));
        var path = Path.Combine(directory, "interface.json");
        var pi = JsonNode.Parse(File.ReadAllText(path))!;
        pi["task"]![0]!["option"] = new JsonArray("EscortContinueS");
        var option = JsonNode.Parse("""
            {"type":"select","label":"护送操作","default_case":"Yes","cases":[
              {"name":"Yes","label":"继续"},{"name":"Stop","label":"停止"}]}
            """)!;
        pi["option"]!["EscortContinueS"] = option;
        File.WriteAllText(path, pi.ToJsonString());
        var configPath = Path.Combine(directory, "config.json");
        var messages = new List<string>();
        var project = ProjectPlanModule.Open(directory, configPath, log: messages.Add);
        project.SetSelectedCase("EscortContinueS", "Yes");
        option["default_case"] = "Stop";
        File.WriteAllText(path, pi.ToJsonString());

        void Check(string name, string label)
        {
            project = ProjectPlanModule.Open(directory, configPath, project, messages.Add);
            var editor = project.GetConfiguration("RealTask").TaskOptions.Single();
            Require(editor.Cases.Single(item => item.Name == editor.SelectedCase).Label == label
                && project.CreateRunStartAttempt().Plan.Items.Single().ResolvedOptions
                    .GetProperty("EscortContinueS").GetString() == name, "编辑器与计划必须采用同一有效选择。");
        }

        Check("Yes", "继续");
        option["cases"]!.AsArray().RemoveAt(0);
        File.WriteAllText(path, pi.ToJsonString());
        Check("Stop", "停止");
        Check("Stop", "停止");
        Require(messages.Count(message => message.Contains("EscortContinueS") && message.Contains("Yes")
            && message.Contains("Stop")) == 1, "重载、重绘和重新创建计划只应记录一次具体的回退。");
    }

    private static void VerifyCheckboxRecovery(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "checkbox-recovery"));
        AddCheckboxOptions(directory);
        var configPath = Path.Combine(directory, "config.json");
        var project = ProjectPlanModule.Open(directory, configPath);
        var partial = project.ActiveConfigurationId;
        project.SetSelectedCases("Features", ["Normal", "Combo"]);
        Guid AddConfig()
        {
            var id = project.CreateConfiguration();
            project.ActivateConfiguration(id);
            project.AddTask("RealTask");
            return id;
        }
        var retired = AddConfig();
        project.SetSelectedCases("Features", ["Combo"]);
        var empty = AddConfig();
        project.SetSelectedCases("Features", []);
        var missing = AddConfig();

        var path = Path.Combine(directory, "interface.json");
        var pi = JsonNode.Parse(File.ReadAllText(path))!;
        var option = pi["option"]!["Features"]!;
        option["min_count"] = 0;
        option["default_case"] = new JsonArray("Charged");
        option["cases"]!.AsArray().RemoveAt(2);
        File.WriteAllText(path, pi.ToJsonString());
        var messages = new List<string>();
        project = ProjectPlanModule.Open(directory, configPath, log: messages.Add);
        void Check(Guid configuration, params string[] expected)
        {
            project.ActivateConfiguration(configuration);
            var editor = project.GetConfiguration("RealTask").TaskOptions.Single(item => item.Name == "Features");
            var values = project.CreateRunStartAttempt().Plan.Items.Single().ResolvedOptions.GetProperty("Features");
            Require(editor.SelectedCases.SequenceEqual(expected)
                && values.EnumerateArray().Select(item => item.GetString()).SequenceEqual(expected),
                "多选编辑器和计划必须保留有效勾选、显式空列表，在旧选择全部失效或缺键时采用新默认。");
        }
        Check(partial, "Normal");
        Check(retired, "Charged");
        Check(empty);
        Check(missing, "Charged");
        Require(messages.Count(message => message.Contains("Features") && message.Contains("Combo")) == 2,
            "部分或全部失效各应记录一次变化。");

        option["min_count"] = 2;
        File.WriteAllText(path, pi.ToJsonString());
        project = ProjectPlanModule.Open(directory, configPath, project, messages.Add);
        project.ActivateConfiguration(partial);
        var group = project.GetConfiguration("RealTask").TaskOptions.Single(item => item.Name == "Features");
        Require(group.SelectedCases.SequenceEqual(["Normal"]) && group.StartBlocker is { } countError
            && countError.Contains("2") && countError.Contains("1")
            && Throws<InvalidOperationException>(() => project.CreateRunStartAttempt()),
            "过滤后不足下限时应说明所需数量和当前数量，保留有效选择供用户补选。");
    }

    private static void VerifyChangedCheckboxLimit(AppLogger logger, string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "checkbox-limit"));
        AddCheckboxOptions(directory);
        var path = Path.Combine(directory, "interface.json");
        var pi = JsonNode.Parse(File.ReadAllText(path))!;
        pi["pretask"] = JsonNode.Parse("""[{"name":"Boot","exec":"boot-fixture","option":["Features"]}]""");
        var option = pi["option"]!["Features"]!;
        option["max_count"] = 3;
        option["label"] = "奖励类型";
        File.WriteAllText(path, pi.ToJsonString());
        var configPath = Path.Combine(directory, "config", "configurations.json");
        var project = ProjectPlanModule.Open(directory, configPath);
        project.AddPretask("Boot");
        project.SetSelectedCases("Features", ["Normal", "Charged", "Combo"]);
        project.SetInputValue("ServerRange", "server_range", "123");
        pi["option"]!["ServerRange"]!["inputs"]![0]!["verify"] = "^978-1012$";
        option["default_case"] = new JsonArray("Normal");
        option["max_count"] = 1;
        File.WriteAllText(path, pi.ToJsonString());
        project = ProjectPlanModule.Open(directory, configPath);
        var editingPath = Path.Combine(directory, "disabled-task.json");
        File.Copy(configPath, editingPath);
        var editing = ProjectPlanModule.Open(directory, editingPath);
        editing.SetItemEnabled(PlanItemKind.MaaTask, "RealTask", false);
        editing.SetSelectedCase("Mode", "Minimal");
        editing.SetSelectedCases("Features", ["Normal", "Charged"]);
        var reopened = ProjectPlanModule.Open(directory, editingPath, editing);
        var options = reopened.GetConfiguration("RealTask").TaskOptions;
        Require(options.Single(item => item.Name == "Mode").SelectedCase == "Minimal"
            && options.Single(item => item.Name == "Features").SelectedCases.Count == 2,
            "临时禁用的普通任务中原有输入失效时，单选和多选仍应可独立保存。");
        Require(Throws<InvalidOperationException>(() => project.CreateRunStartAttempt(startupOnly: true)),
            "上限降低后，计划必须等到用户决定取消哪些有效选择。");
        var preparations = 0;
        WithRuntimeControlWindow(logger, testDirectory, window => {
            var items = Named<System.Windows.Controls.Panel>(window, "PlanItemsPanel");
            SetFocus(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            var startup = ConfigurationDescendants(items).OfType<System.Windows.Controls.Button>()
                .Single(button => AutomationProperties.GetName(button) == "仅执行启动");
            startup.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
            ListBox? Group() => ConfigurationDescendants(items).OfType<ListBox>()
                .SingleOrDefault(list => AutomationProperties.GetName(list) == "奖励类型");
            PumpUntil(() => preparations > 0 || Group()?.IsKeyboardFocusWithin == true, "应在启动项内聚焦奖励类型。");
            var boot = ConfigurationDescendants(items).OfType<Border>().Single(card =>
                card.Tag is ProjectTaskChoice { Kind: PlanItemKind.Pretask, Name: "Boot" });
            Require(preparations == 0 && Group()?.SelectedItems.Count == 3 && ConfigurationDescendants(boot)
                .OfType<TextBlock>().Any(text => text.Text.Contains("1") && text.Text.Contains("3")
                    && text.Text.Contains("2")), "保留所有有效选择并说明上限、当前数量和应取消的数量。");
            Group()!.SelectedItems.RemoveAt(0);
            var saved = ProjectOf(window).GetConfiguration("Boot", PlanItemKind.Pretask);
            Require(saved.TaskOptions.Single().SelectedCases.Count == 2,
                "应能逐项取消过多的选择。");
            var group = Group()!;
            group.SelectedItems.Add(group.Items.Cast<ProjectCaseEditor>().Single(item => item.Name == "Normal"));
            Require(Group()!.SelectedItems.Count == 2 && !Group()!.SelectedItems.Cast<ProjectCaseEditor>()
                .Any(item => item.Name == "Normal"), "仍然超限时，新的勾选不能替用户取消已有选择。");
            Group()!.SelectedItems.RemoveAt(0);
            Require(ProjectOf(window).CreateRunStartAttempt(true).Plan.Items.Single().ResolvedOptions
                .GetProperty("Features").GetArrayLength() == 1, "修正数量后应能生成启动计划。");
        }, projectDirectory: directory, prepareEnvironment: (_, _, _) => {
            preparations++;
            return Task.FromCanceled(new CancellationToken(canceled: true));
        });
    }

    private static void VerifyCheckboxOptions(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "checkbox"));
        AddCheckboxOptions(directory);
        var configPath = Path.Combine(directory, "config.json");
        var project = ProjectPlanModule.Open(directory, configPath);
        ProjectOptionEditor Option(ProjectPlanModule plan, string name) =>
            plan.GetConfiguration("RealTask").TaskOptions.Single(option => option.Name == name);
        string[] MergeOrder() => project.CreateRunStartAttempt().Plan.Items.Single().PipelineOverride.EnumerateArray()
            .Where(item => item.TryGetProperty("FeatureOrder", out _))
            .Select(item => item.GetProperty("FeatureOrder").GetProperty("last").GetString()!).ToArray();

        var features = Option(project, "Features");
        var resolved = project.CreateRunStartAttempt().Plan.Items.Single().ResolvedOptions;
        Require(features is { Kind: ProjectOptionKind.Checkbox, MinCount: 1, MaxCount: 2, IsExplicit: false }
            && features.SelectedCases.SequenceEqual(["Normal", "Charged"])
            && features.ActiveChildren.Single().Name == "ChargeTime"
            && resolved.GetProperty("Features").EnumerateArray().Select(item => item.GetString())
                .SequenceEqual(["Normal", "Charged"])
            && resolved.GetProperty("Single").GetArrayLength() == 0,
            "checkbox 默认值必须按 case 声明顺序生效，已勾选 case 的子选项随之展开。");
        Require(MergeOrder().SequenceEqual(["normal", "charged", "short"]),
            "已勾选 case 的 override 必须按声明顺序合并，子选项紧随其 case。");

        project.SetSelectedCases("Features", ["Combo", "Normal"]);
        var saved = project.Configurations[0].ExplicitOptions["Features"].GetProperty("SelectedCases");
        Require(saved.EnumerateArray().Select(item => item.GetString()).SequenceEqual(["Normal", "Combo"])
            && MergeOrder().SequenceEqual(["normal", "combo"]) && Option(project, "Features").ActiveChildren.Count == 0,
            "保存的勾选与合并顺序必须与勾选先后无关。");
        Require(Throws<ArgumentException>(() => project.SetSelectedCases("Features", ["Normal", "Charged", "Combo"]))
            && Throws<ArgumentException>(() => project.SetSelectedCases("Features", ["Missing"]))
            && Throws<ArgumentException>(() => project.SetSelectedCase("Features", "Normal")),
            "checkbox 不得保存超过 max_count、不存在的 case 或 select 形式的值。");

        project.SetSelectedCases("Features", []);
        Require(Option(project, "Features").IsBelowMinimum
            && Option(ProjectPlanModule.Open(directory, configPath), "Features").SelectedCases.Count == 0
            && Throws<InvalidOperationException>(() => project.CreateRunStartAttempt()),
            "少于 min_count 的勾选可以保存，但不能开始运行。");

        var source = File.ReadAllText(Path.Combine(directory, "interface.json"));
        VerifyRejectedProjectInterface(testDirectory, source, "checkbox-limits", "需满足 min_count ≤ max_count",
            root => root["option"]!["Features"]!["min_count"] = 3);
        VerifyRejectedProjectInterface(testDirectory, source, "checkbox-default",
            "default_case 引用了不存在的 case：Missing",
            root => root["option"]!["Features"]!["default_case"] = new JsonArray("Missing"));
        VerifyRejectedProjectInterface(testDirectory, source, "checkbox-default-max", "default_case 超过 max_count 2",
            root => root["option"]!["Features"]!["default_case"] = new JsonArray("Normal", "Charged", "Combo"));
    }

    private static void VerifyCheckboxEditor(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "checkbox-editor"), (window, _) => {
            var project = ProjectOf(window);
            EndTour(window);
            SetOnboardingField(window, "_expandedTaskName", "RealTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            PumpOnboarding();
            SaveWindowScreenshot(window, "checkbox.png");
            var items = (System.Windows.DependencyObject)window.FindName("PlanItemsPanel");
            // Saving may rebuild the card, so each step looks the editor up again.
            ListBox Segments(string name) => ConfigurationDescendants(items).OfType<ListBox>()
                .Single(list => AutomationProperties.GetName(list) == name);
            ProjectCaseEditor Case(ListBox list, string name) =>
                list.Items.Cast<ProjectCaseEditor>().Single(item => item.Name == name);
            ProjectOptionEditor Option(string name) =>
                project.GetConfiguration("RealTask").TaskOptions.Single(option => option.Name == name);

            var features = Segments("Features");
            features.UpdateLayout();
            var normal = (ListBoxItem)features.ItemContainerGenerator.ContainerFromItem(Case(features, "Normal"));
            var combo = (ListBoxItem)features.ItemContainerGenerator.ContainerFromItem(Case(features, "Combo"));
            Require(features.SelectedItems.Count == 2 && Equals(normal.ToolTip, "Enable normal strike")
                && combo.ToolTip is null && AutomationProperties.GetName(normal) == "Normal strike",
                "多 case checkbox 必须显示为分段，有说明的 case 以 tooltip 显示说明。");

            features.SelectedItems.Add(Case(features, "Combo"));
            Require(Option("Features").SelectedCases.SequenceEqual(["Normal", "Charged"])
                && Segments("Features").SelectedItems.Count == 2, "达到 max_count 后多出的勾选必须被撤回。");
            Segments("Features").SelectedItems.Remove(Case(Segments("Features"), "Charged"));
            Segments("Features").SelectedItems.Remove(Case(Segments("Features"), "Normal"));
            var row = ConfigurationDescendants(items).OfType<ParameterRowPanel>()
                .Single(panel => ConfigurationDescendants(panel).Contains(Segments("Features")));
            Require(Option("Features").SelectedCases.Count == 0 && row.IsInvalid
                && ConfigurationDescendants(row).OfType<TextBlock>()
                    .Any(text => text.Text.Contains("0") && text.Text.Contains("1"))
                && ParameterInspector.ShowCases(Option("Features")) == "无",
                "少于 min_count 时必须保存并显示数量提示。");

            Segments("Tier").SelectedItems.Add(Case(Segments("Tier"), "Low"));
            Segments("Tier").SelectedItems.Add(Case(Segments("Tier"), "High"));
            Require(Option("Tier").SelectedCases.SequenceEqual(["High"]) && Segments("Tier").SelectedItems.Count == 1,
                "max_count 为 1 时，新勾选必须替换旧勾选。");

            Segments("Finish").SelectedItems.Add(Case(Segments("Finish"), "Enabled"));
            Require(Option("Single").SelectedCases.SequenceEqual(["Enabled"])
                && !ConfigurationDescendants(items).OfType<CheckBox>().Any(box => AutomationProperties.GetName(box)
                    == "Finish") && ParameterInspector.ShowCases(Option("Single")) == "Finish directly",
                "单 case checkbox 也必须显示为分段，摘要显示 case 标签。");
        }, AddCheckboxOptions);
    }

    // Adds checkbox options to RealTask: limits with a sub-option, max_count 1, and a single case.
    private static void AddCheckboxOptions(string directory)
    {
        var interfacePath = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        json["task"]![0]!["option"] = new JsonArray("Mode", "Features", "Tier", "Single");
        var definitions = json["option"]!.AsObject();
        definitions["Features"] = JsonNode.Parse("""
            {"type": "checkbox", "label": "Features", "min_count": 1, "max_count": 2,
             "default_case": ["Charged", "Normal"], "cases": [
              {"name": "Normal", "label": "Normal strike", "description": "Enable normal strike",
               "pipeline_override": {"FeatureOrder": {"last": "normal"}}},
              {"name": "Charged", "label": "Charged strike", "option": ["ChargeTime"],
               "pipeline_override": {"FeatureOrder": {"last": "charged"}}},
              {"name": "Combo", "label": "Combo strike", "pipeline_override": {"FeatureOrder": {"last": "combo"}}}]}
            """);
        definitions["ChargeTime"] = JsonNode.Parse("""
            {"type": "select", "label": "Charge time", "default_case": "Short", "cases": [
              {"name": "Short", "pipeline_override": {"FeatureOrder": {"last": "short"}}}, {"name": "Long"}]}
            """);
        definitions["Tier"] = JsonNode.Parse("""
            {"type": "checkbox", "label": "Tier", "max_count": 1, "cases": [
              {"name": "Low", "label": "≤1次"}, {"name": "High", "label": "≤3次"}]}
            """);
        definitions["Single"] = JsonNode.Parse("""
            {"type": "checkbox", "label": "Finish", "cases": [
              {"name": "Enabled", "label": "Finish directly", "description": "Skip the progress check"}]}
            """);
        File.WriteAllText(interfacePath, json.ToJsonString());
    }
}
