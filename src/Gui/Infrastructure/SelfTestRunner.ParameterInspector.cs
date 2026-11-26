using System.Text.Json.Nodes;
using ProjectModel;
using Gui.Views;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyParameterInspector(string testDirectory, string projectDirectory)
    {
        var none = new Dictionary<(string, string), InputDraft>();
        var simple = ProjectPlanModule.Open(projectDirectory, Path.Combine(testDirectory, "inspector-simple.json"));
        var rows = ParameterInspector.Flatten(ParameterInspector.Derive(simple.GetConfiguration("RealTask"), none)).ToList();
        Require(rows.Select(row => (row.Option.Name, row.Depth, row.IsGlobal)).SequenceEqual(new[] {
            ("ServerRange", 0, true), ("Mode", 0, false), ("Nested", 1, false)
        }), "检查器行必须先全局后任务，按声明顺序深度优先。");
        var server = rows[0];
        Require(server.Kind == ProjectOptionKind.Input && server.Label == "Server range"
            && server.Inputs.Select(input => input.Label).SequenceEqual(new[] { "Server", "Retry count", "Enabled" })
            && server.Inputs[0].AccessibleName == "Server range · Server"
            && server.Description.StartsWith("A long option description", StringComparison.Ordinal),
            "多输入选项必须归为一行，并保留选项标签、说明和各输入标签。");
        Require(rows[1].Kind == ProjectOptionKind.Select && rows[1].Label == "Mode"
            && rows[2].Kind == ProjectOptionKind.Switch && rows[2].Label == "Nested",
            "无 label 的 select/switch 行应回退到选项名。");

        var directory = CreateComplexInspectorFixture(testDirectory);
        var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        project.AddItem(Protocol.PlanItemKind.MaaTask, "StressTask");
        project.AddItem(Protocol.PlanItemKind.MaaTask, "ThirdTask");
        List<InspectorRow> Rows(IReadOnlyDictionary<(string, string), InputDraft> drafts) => ParameterInspector.Flatten(
            ParameterInspector.Derive(project.GetConfiguration("StressTask"), drafts)).ToList();
        rows = Rows(none);
        Require(rows.Select(row => (row.Option.Name, row.Depth)).SequenceEqual(new[] {
            ("ServerRange", 0), ("Mode", 0), ("Nested", 1), ("Level1", 0), ("Level2", 1), ("Level3", 2)
        }), "选中 case 的子选项必须紧跟父行，三级嵌套深度依次递增。");
        Require(rows[3].Label == "Level1" && rows[5].Label == "Keyword" && rows[5].Inputs.Single().Label == "Keyword",
            "标签回退必须为选项 label、第一个输入 label、选项名。");
        Require(rows[3].CaseDescription == "Goes one level deeper" && rows[1].CaseDescription is null,
            "选中 case 有说明时才给出 case 说明。");

        project.SetInputValue("ServerRange", "retry_count", "3");
        project.SetInputValue("ServerRange", "server_range", "979");
        project.SetSelectedCase("Mode", "Default");
        var drafts = new Dictionary<(string, string), InputDraft> {
            [("ServerRange", "enabled")] = new("maybe", "必须是 true 或 false")
        };
        rows = Rows(drafts);
        var inputs = rows[0].Inputs;
        Require(rows[0].IsInvalid && inputs[2].Draft?.Text == "maybe" && inputs[2].SavedValue == "true"
            && inputs[2].DisplayValue == "maybe" && !rows[1].IsInvalid,
            "草稿状态必须给出草稿文本、提示与已保存值。");

        project.SetSelectedCase("Level1", "Shallow");
        rows = Rows(none);
        Require(rows.Select(row => row.Option.Name).SequenceEqual(new[] {
            "ServerRange", "Mode", "Nested", "Level1"
        }) && rows[3].CaseDescription is null, "切换 case 后子行必须消失，case 说明随之消失。");
        project.SetSelectedCase("Level1", "Deep");
        rows = Rows(none);
        Require(rows.Count == 6, "切回默认 case 后子行恢复。");
    }

    private static void VerifyPlanItemHeaders(AppLogger logger, string testDirectory)
    {
        var directory = CreateComplexInspectorFixture(Path.Combine(testDirectory, "plan-headers"));
        var interfacePath = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        var plain = json["task"]![0]!.DeepClone();
        plain["name"] = "PlainTask";
        plain["label"] = "Plain task";
        plain["description"] = "";
        plain["option"] = new JsonArray();
        json["global_option"] = new JsonArray();
        json["task"]!.AsArray().Add(plain);
        File.WriteAllText(interfacePath, json.ToJsonString());
        var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        project.AddItem(Protocol.PlanItemKind.MaaTask, "StressTask");
        project.AddItem(Protocol.PlanItemKind.MaaTask, "PlainTask");
        WithPlanWindow(logger, testDirectory, project, (window, items) => {
            SetExpandedTask(window, null);
            InvokeEditor(window, "RenderTaskPlan");
            string NameOf(System.Windows.DependencyObject element) =>
                System.Windows.Automation.AutomationProperties.GetName(element);
            System.Windows.Controls.Button[] Buttons() => ConfigurationDescendants(items)
                .OfType<System.Windows.Controls.Button>().ToArray();
            void Click(string name) => Buttons().First(button => NameOf(button) == name).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string[] Expanded() => Buttons().Select(NameOf)
                .Where(name => name.EndsWith("，已展开", StringComparison.Ordinal)).ToArray();
            int InspectorRows() => ConfigurationDescendants(items).OfType<ParameterRowPanel>().Count();

            Require(Expanded().Length == 0 && InspectorRows() == 0 && Buttons().All(button => NameOf(button) is not
                ("全部折叠" or "展开参数" or "收起参数")), "执行计划中不应有全部折叠或独立的展开/收起按钮。");
            Click("Real task，已折叠");
            Require(Expanded().SequenceEqual(new[] { "Real task，已展开" }) && InspectorRows() == 2,
                "点击标题区必须展开该卡片的参数检查器。");
            Click("Stress task，已折叠");
            Require(Expanded().SequenceEqual(new[] { "Stress task，已展开" }) && InspectorRows() == 5,
                "展开另一张卡片时必须收起之前的卡片。");
            Click("从执行计划移除 Plain task");
            Require(Expanded().SequenceEqual(new[] { "Stress task，已展开" })
                && !project.SelectedTaskNames.Contains("PlainTask"), "移除按钮不得切换展开。");
            Require(Buttons().Any(button => NameOf(button) == "从执行计划移除 Real task"),
                "有参数的任务折叠时也必须提供移除按钮。");
            Click("从执行计划移除 Real task");
            Require(Expanded().SequenceEqual(new[] { "Stress task，已展开" })
                && !project.SelectedTaskNames.Contains("RealTask"), "直接移除有参数的任务时应保留其他任务的展开状态。");
            project.AddItem(Protocol.PlanItemKind.MaaTask, "PlainTask");
            InvokeEditor(window, "RenderTaskPlan");
            Click("Plain task，没有可配置的参数");
            Require(Expanded().SequenceEqual(new[] { "Stress task，已展开" }), "无参数任务的标题区不得展开空白区域。");
            var description = ConfigurationDescendants(items)
                .OfType<System.Windows.Controls.FlowDocumentScrollViewer>().Single();
            // This fixture renders the controls without showing their window.
            Require(description.Visibility == System.Windows.Visibility.Visible && description.Document.Blocks.Count > 0
                && Expanded().SequenceEqual(new[] { "Stress task，已展开" })
                && Buttons().All(button => NameOf(button) != "查看 Stress task 的任务描述"),
                "任务说明应随任务卡展开，收在同一个详情区。");
            Click("Stress task，已展开");
            Require(Expanded().Length == 0 && InspectorRows() == 0
                && !ConfigurationDescendants(items).OfType<System.Windows.Controls.FlowDocumentScrollViewer>().Any(),
                "再次点击已展开的标题区必须同时收起说明和参数。");

            Click("Stress task，已折叠");
            window.SetPendingOperation(PendingOperation.PreparingEnvironment);
            var rows = ConfigurationDescendants(items).OfType<ParameterRowPanel>().ToArray();
            var elements = rows.SelectMany(ConfigurationDescendants).Distinct().ToArray();
            var editors = elements.OfType<System.Windows.Controls.Control>()
                .Where(control => control is System.Windows.Controls.TextBox or System.Windows.Controls.ComboBox
                    or System.Windows.Controls.ListBox or Wpf.Ui.Controls.ToggleSwitch).ToArray();
            var notes = elements.OfType<System.Windows.Controls.TextBlock>()
                .Where(text => text.Text == "Goes one level deeper").ToArray();
            Require(editors.Length == 5 && editors.All(editor => !editor.IsEnabled) && notes.Length == 1
                && notes.All(note => note.IsEnabled), "锁定时参数控件禁用，说明保持可读。");
            window.SetPendingOperation(PendingOperation.None);
            Click("从执行计划移除 Stress task");
            Require(!project.SelectedTaskNames.Contains("StressTask") && Expanded().Length == 0 && InspectorRows() == 0,
                "从展开的详情区移除任务后应收起参数检查器。");
        });
    }

    // The inspector at the three reference window widths: stacked when narrow, two columns otherwise, never
    // scrolling sideways. Set BUNSHIN_TASK_SCREENSHOTS to keep the rendered windows for a visual check.
    private static void VerifyParameterInspectorWindow(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "inspector-window"), (window, _) => {
            var project = ProjectOf(window);
            project.AddItem(Protocol.PlanItemKind.MaaTask, "StressTask");
            project.AddItem(Protocol.PlanItemKind.MaaTask, "ThirdTask");
            project.SetInputValue("ServerRange", "server_range", "979");
            project.SetInputValue("ServerRange", "retry_count", "5");
            EndTour(window);
            SetExpandedTask(window, "StressTask");
            InvokeEditor(window, "RenderTaskPlan");
            var items = (System.Windows.DependencyObject)window.TaskPlanEditor.FindName("PlanItemsPanel");
            var notes = ConfigurationDescendants(items).OfType<System.Windows.Controls.TextBlock>().ToArray();
            Require(notes.Any(note => note.Text == "Number of retry attempts")
                && notes.Any(note => note.Text.Contains("−2147483648～2147483647", StringComparison.Ordinal)
                    && note.Text.Contains("项目格式", StringComparison.Ordinal))
                && notes.All(note => !note.Text.StartsWith("默认：", StringComparison.Ordinal)),
                "输入说明必须直接可见，显式值偏离默认时也不显示默认提示。");
            var planScroll = (System.Windows.Controls.ScrollViewer)window.TaskPlanEditor.FindName("PlanScroll");
            foreach (var width in new[] { 920, 1180, 1440 }) {
                window.Width = width;
                window.Height = width == 920 ? 640 : width == 1180 ? 760 : 900;
                PumpOnboarding();
                var rows = ConfigurationDescendants(items).OfType<ParameterRowPanel>().ToArray();
                Require(rows.Length == 6 && rows.All(LabelsAndValuesFit)
                    && planScroll.ExtentWidth <= planScroll.ViewportWidth + PixelTolerance,
                    $"{width} 宽度下参数标签与值应各自完整可见、互不重叠，执行计划无需横向滚动。");
                SaveWindowScreenshot(window, $"inspector-{width}.png");
            }
        }, AddComplexInspectorOptions);

        static bool LabelsAndValuesFit(ParameterRowPanel row)
        {
            System.Windows.Rect Bounds(System.Windows.UIElement element) => element.TransformToAncestor(row)
                .TransformBounds(new System.Windows.Rect(element.RenderSize));
            var label = Bounds(row.Children[0]);
            var value = Bounds(row.Children[1]);
            var overlap = System.Windows.Rect.Intersect(label, value);
            return (overlap.IsEmpty || overlap.Width <= PixelTolerance || overlap.Height <= PixelTolerance)
                && new[] { label, value }.All(bounds => bounds.Left >= -PixelTolerance && bounds.Top >= -PixelTolerance
                    && bounds.Right <= row.ActualWidth + PixelTolerance && bounds.Bottom <= row.ActualHeight + PixelTolerance);
        }
    }

    // Set BUNSHIN_TASK_SCREENSHOTS to a directory to keep rendered windows for a visual check.
    private static void SaveWindowScreenshot(MainWindow window, string fileName)
    {
        var screenshots = Environment.GetEnvironmentVariable("BUNSHIN_TASK_SCREENSHOTS");
        if (string.IsNullOrEmpty(screenshots)) {
            return;
        }
        Directory.CreateDirectory(screenshots);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,
            (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(screenshots, fileName));
        encoder.Save(stream);
    }

    private static void VerifyParameterRowLayout()
    {
        Require(ParameterRowPanel.CalculateLabelWidth(307, 0) is null
            && ParameterRowPanel.CalculateLabelWidth(308, 0) == 114
            && ParameterRowPanel.CalculateLabelWidth(1200, 1) == 98, "宽布局为标签和控件两列，窄布局上下堆叠。");
        Require(ParameterInputsPanel.CalculateColumns(140, 3) == 1 && ParameterInputsPanel.CalculateColumns(300, 3) == 2
            && ParameterInputsPanel.CalculateColumns(900, 3) == 3, "多输入在宽度不足时换行。");

        var description = new System.Windows.Controls.TextBlock {
            Text = string.Concat(Enumerable.Repeat("A long description that must wrap. ", 12)),
            TextWrapping = System.Windows.TextWrapping.Wrap
        };
        var value = new System.Windows.Controls.Border { Height = 32 };
        var row = new ParameterRowPanel { Depth = 1, Children = { description, value } };
        System.Windows.Rect Arrange(double width)
        {
            row.Measure(new System.Windows.Size(width, double.PositiveInfinity));
            row.Arrange(new System.Windows.Rect(0, 0, width, row.DesiredSize.Height));
            return System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(value);
        }
        System.Windows.Rect LabelSlot() =>
            System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(description);
        var wide = Arrange(600);
        Require(!row.IsStacked && wide.X >= LabelSlot().Width && wide.Right <= 600
            && description.ActualHeight > value.Height, "宽编辑区为两列，长说明在标签列内换行，没有横向溢出。");
        var narrow = Arrange(240);
        Require(row.IsStacked && narrow.X == 0 && narrow.Y >= description.ActualHeight && LabelSlot().Width == 240,
            "窄编辑区标签与控件上下堆叠，且对齐。");
    }

    private static string CreateComplexInspectorFixture(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "inspector-complex"));
        AddComplexInspectorOptions(directory);
        return directory;
    }

    // Adds a task with multi-level nesting, a described case and blank labels, and one sharing an option.
    private static void AddComplexInspectorOptions(string directory)
    {
        var interfacePath = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        var tasks = json["task"]!.AsArray();
        foreach (var (name, label, options) in new[] {
            ("StressTask", "Stress task", new[] { "Mode", "Level1" }), ("ThirdTask", "Third task", new[] { "Mode" })
        }) {
            var task = tasks[0]!.DeepClone();
            task["name"] = name;
            task["label"] = label;
            task["option"] = new JsonArray(options.Select(option => (JsonNode)option).ToArray());
            tasks.Add(task);
        }
        var definitions = json["option"]!.AsObject();
        definitions["ServerRange"]!["inputs"]![1]!["description"] = "Number of retry attempts";
        definitions["ServerRange"]!["inputs"]![1]!["verify"] = "^(?:1|3|5|8)$";
        definitions["Level1"] = JsonNode.Parse("""
            {"type": "select", "label": "", "default_case": "Deep", "cases": [
              {"name": "Deep", "description": "Goes one level deeper", "option": ["Level2"]},
              {"name": "Shallow"}]}
            """);
        definitions["Level2"] = JsonNode.Parse("""
            {"type": "switch", "default_case": "Yes", "cases": [
              {"name": "Yes", "option": ["Level3"]}, {"name": "No"}]}
            """);
        definitions["Level3"] = JsonNode.Parse("""
            {"type": "input", "label": "", "inputs": [
              {"name": "keyword", "label": "Keyword", "default": "", "pipeline_type": "string"}],
             "pipeline_override": {"Keyword": {"text": "{keyword}"}}}
            """);
        File.WriteAllText(interfacePath, json.ToJsonString());
    }
}
