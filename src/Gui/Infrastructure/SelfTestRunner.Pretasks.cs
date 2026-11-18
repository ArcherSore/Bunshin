using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Gui.ChildSession;
using ProjectModel;
using Protocol;
using Gui.Views;
using Gui.Worker;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static Button PlanHeader(Border card) => ConfigurationDescendants(card).OfType<Button>()
        .Single(b => b.Tag is ProjectTaskChoice && b.Style == card.FindResource("PlanHeaderButtonStyle"));

    private static void ToggleRealTask(Panel plan)
    {
        var card = ConfigurationDescendants(plan).OfType<Border>()
            .Single(card => card.Tag is ProjectTaskChoice { Kind: PlanItemKind.MaaTask, Name: "RealTask" });
        PlanHeader(card).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static void VerifyRunValidationSummary(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "run-validation-summary");
        var root = CreatePretaskFixture(directory, """
            [{"name":"Boot","label":"启动游戏","exec":"boot-fixture","option":["LaunchInput"]}]
            """);
        AddSecretAndPathInputs(root);
        EditInterface(root, pi => {
            var login = pi["option"]!["Login"]!;
            login["label"] = "登录密码";
            login["inputs"]![0]!["verify"] = "^$";
            login["inputs"]![0]!["pattern_msg"] = "请修改登录密码";
            var fields = pi["option"]!["LaunchInput"]!["inputs"]!;
            foreach (var (index, label) in new[] { (0, "角色"), (1, "区服") }) {
                fields[index]!["label"] = label;
                fields[index]!["verify"] = "^1$";
                fields[index]!["pattern_msg"] = $"请填写{label}";
            }
        });
        var path = Path.Combine(root, "config", "configurations.json");
        ProjectPlanModule.Open(root, path).AddItem(PlanItemKind.Pretask, "Boot");
        var preparations = 0;
        WithRuntimeControlWindow(logger, directory, window => {
            var panel = Named<Panel>(window, "PlanItemsPanel");
            var banner = Named<TextBlock>(window, "ProjectValidationText");
            T Field<T>(string label) where T : FrameworkElement => ConfigurationDescendants(panel)
                .OfType<T>().Single(field => AutomationProperties.GetName(field) == label);
            Border Card(string name) => ConfigurationDescendants(panel).OfType<Border>()
                .Single(card => card.Tag is ProjectTaskChoice task && task.Name == name);
            bool Marked(string name) => AutomationProperties.GetItemStatus(Card(name)).Length != 0;
            void Submit(Button button)
            {
                Require(button.IsEnabled && button.IsVisible && button.Focus(), "应能使用任务页启动入口。");
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }

            SetFocus(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            if (!ConfigurationDescendants(Card("RealTask")).OfType<Wpf.Ui.Controls.PasswordBox>().Any()) {
                ToggleRealTask(panel);
            }
            var password = Field<Wpf.Ui.Controls.PasswordBox>("登录密码");
            Require(password.Focus(), "应能编辑普通任务的密码。");
            password.Password = SecretInputValue;
            Submit(Named<Button>(window, "PrepareEnvironmentButton"));
            PumpUntil(() => ConfigurationDescendants(Card("Boot")).OfType<TextBox>().Any(field =>
                field.IsKeyboardFocused && AutomationProperties.GetName(field).Contains("角色")),
                "即使普通任务已展开，也应按执行顺序定位到启动项的第一个错误字段。");
            Require(banner.IsVisible && new[] { "启动游戏", "请填写角色", "请填写区服", "Real task", "请修改登录密码" }
                    .All(message => banner.Text.Contains(message)) && !banner.Text.Contains(SecretInputValue)
                && Marked("Boot") && Marked("RealTask") && preparations == 0,
                "开始前应汇总每张受影响卡片及所有字段的改法，标记所有卡片并保护密码。");
            ToggleRealTask(panel);
            Require(Field<Wpf.Ui.Controls.PasswordBox>("登录密码").Password == SecretInputValue
                && Marked("Boot") && Marked("RealTask") && banner.Text.Contains("请填写角色"),
                "手动打开其他卡片后应保留草稿、卡片标记和启动问题汇总。");
            ProjectOf(window).SetItemEnabled(PlanItemKind.MaaTask, "RealTask", false);
            Submit(Named<Button>(window, "PrepareEnvironmentButton"));
            PumpUntil(() => Marked("Boot") && !Marked("RealTask"), "只勾选前置任务时应只标记前置任务。");
            Require(banner.Text.Contains("请填写角色") && banner.Text.Contains("请填写区服")
                && !banner.Text.Contains("登录密码") && preparations == 0, "只勾选前置任务时只应报告两个启动参数。");
            var role = Field<TextBox>("LaunchInput · 角色");
            PumpUntil(() => role.IsKeyboardFocused, "应定位到启动阶段首个无效字段。");
            role.Text = "1";
            Require(AutomationProperties.GetItemStatus(role).Length == 0 && banner.Text.Contains("角色")
                && !banner.Text.Contains("请填写角色"), "修正草稿后应撤下格式错误，启动汇总仍应指出待保存的字段。");
            Require(Field<TextBox>("LaunchInput · 区服").Focus() && !banner.Text.Contains("角色")
                && banner.Text.Contains("请填写区服"), "保存后只应留下仍有问题的区服字段。");
        }, prepareEnvironment: (_, _, _) => {
            preparations++;
            return Task.FromCanceled(new CancellationToken(true));
        }, projectDirectory: root);
    }

    private static void VerifyStartScopeWiring(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "start-scope");
        var projectDirectory = CreatePretaskFixture(directory, """
            [{"name":"Boot","exec":"boot-fixture","option":["LaunchInput"]}]
            """);
        AddRequiredInput(projectDirectory);
        var interfacePath = Path.Combine(projectDirectory, "interface.json");
        var definition = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        definition["task"]!.AsArray().Add(new JsonObject { ["name"] = "ReadyTask", ["entry"] = "ReadyEntry" });
        File.WriteAllText(interfacePath, definition.ToJsonString());
        var configPath = Path.Combine(projectDirectory, "config", "configurations.json");
        var project = ProjectPlanModule.Open(projectDirectory, configPath);
        project.AddItem(PlanItemKind.Pretask, "Boot");
        project.SetInputValue("LaunchInput", "caption", "启动参数");
        project.AddItem(PlanItemKind.MaaTask, "ReadyTask");

        WithFakeWorker(logger, Path.Combine(directory, "worker"), (coordinator, record, pipeName) => {
            using var session = new SimulatedWts().CreateManager(logger);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WaitLimitMilliseconds));
            var pipe = Task.Run(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(pipeName, record, 0, timeout.Token))
                .GetAwaiter().GetResult();
            try {
                WithRuntimeControlWindow(logger, directory, window => {
                    // Publish a connected simulated session after the window's normal startup probe.
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(ChildSessionManager).GetMethod("UpdateState", flags)!
                        .Invoke(session, [ChildSessionState.ConnectedHidden, record.ChildSessionId, 1, "fixture"]);
                    var items = Named<Panel>(window, "PlanItemsPanel");
                    T FindControl<T>(string name) where T : UIElement => ConfigurationDescendants(items)
                        .OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

                    RunPlan Submit(Button button, string failure)
                    {
                        PumpUntil(() => button.IsEnabled, failure);
                        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Require(!ConfigurationEditable(window), failure);
                        var received = Task.Run(async () => {
                            var request = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                                pipe, ProtocolOperations.RunStart, timeout.Token);
                            await pipe.WriteAsync(WireEnvelope.Response(request.Operation, request.RequestId!.Value,
                                new RunStartResponse("accepted")), timeout.Token);
                            var start = ProtocolJson.Deserialize<RunStartRequest>(request.Data);
                            var snapshot = coordinator.Snapshot.WorkerSnapshot!;
                            var now = DateTime.UtcNow;
                            var items = start.Plan.Items.Select((item, index) => new PlanItemSnapshot(
                                item.PlanItemId, item.TaskName, item.TaskLabel, item.Entry,
                                item.ResolvedOptions, item.PipelineOverride,
                                index == 0 ? PlanItemState.Running : PlanItemState.Pending,
                                index == 0 ? now : null, null, null, null, null) { Kind = item.Kind }).ToArray();
                            var active = new RunSnapshot(start.RunId, start.PlanDigest, RunState.Running,
                                start.Plan.CreatedAtUtc, now, null, null, items[0].PlanItemId, 0,
                                start.Plan, items, null, null);
                            await pipe.WriteAsync(StateEvent(snapshot with {
                                StateRevision = snapshot.StateRevision + 1, CapturedAtUtc = now,
                                RunState = RunState.Running, ActiveRun = active
                            }), timeout.Token);
                            var end = DateTime.UtcNow;
                            var terminal = active with {
                                State = RunState.Succeeded, EndedAtUtc = end,
                                CurrentPlanItemId = null, CurrentPlanItemIndex = null,
                                Items = items.Select(item => item with {
                                    State = PlanItemState.Succeeded, StartedAtUtc = item.StartedAtUtc ?? end,
                                    EndedAtUtc = end
                                }).ToArray()
                            };
                            await pipe.WriteAsync(StateEvent(snapshot with {
                                StateRevision = snapshot.StateRevision + 2, CapturedAtUtc = end,
                                RunState = RunState.Idle, ActiveRun = null, LastRun = terminal
                            }), timeout.Token);
                            return start;
                        });
                        PumpUntil(() => received.IsCompleted, failure);
                        var submitted = received.GetAwaiter().GetResult();
                        PumpUntil(() => coordinator.Snapshot.WorkerSnapshot?.LastRun?.RunId == submitted.RunId
                            && ConfigurationEditable(window), "Worker 报告本次运行完成后应恢复编辑。");
                        return submitted.Plan;
                    }

                    void VerifyPretaskPlan(string failure, string expectedCaption = "启动参数")
                    {
                        var plan = Submit(Named<Button>(window, "StartTaskHeaderButton"), failure);
                        Require(plan.Items is [{ Kind: PlanItemKind.Pretask, TaskName: "Boot" }],
                            "只勾选前置任务时应只向 Worker 提交所选前置任务。");
                        var args = JsonNode.Parse(plan.Items[0].Program!.Arguments.Single());
                        Require((string?)args!["LaunchInput"]!["caption"] == expectedCaption,
                            "只勾选前置任务时应把前置任务填写的参数交给 Worker。");
                    }

                    SetFocus(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                    Click(window, "StartTaskHeaderButton");
                    Require(ConfigurationEditable(window), "完整运行应在提交前阻止缺少必填参数的任务。");
                    var recipient = FindControl<TextBox>("Recipient");
                    PumpUntil(() => recipient.IsKeyboardFocused, "完整运行应展开普通任务并定位到缺少的必填字段。");
                    recipient.Text = "  ";
                    var selection = FindControl<System.Windows.Controls.CheckBox>("执行 Real task");
                    selection.IsChecked = false;
                    selection.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var full = Submit(Named<Button>(window, "StartTaskHeaderButton"), "跳过无效任务后应可完整运行。");
                    Require(full.Items is [{ Kind: PlanItemKind.Pretask, TaskName: "Boot" },
                        { Kind: PlanItemKind.MaaTask, TaskName: "ReadyTask" }], "完整运行应提交前置任务及其余勾选的普通任务。");

                    Require(FindControl<TextBox>("Recipient").Text == "  ",
                        "完整运行后，被取消勾选的任务仍应保留原草稿。");
                    definition["task"]![1]!["option"] = new JsonArray("LaunchInput");
                    var caption = definition["option"]!["LaunchInput"]!["inputs"]![0]!;
                    caption["pipeline_type"] = "int";
                    caption["default"] = "1";
                    File.WriteAllText(interfacePath, definition.ToJsonString());
                    Click(window, "StartTaskHeaderButton");
                    bool Marked(string name)
                    {
                        var card = ConfigurationDescendants(items).OfType<Border>()
                            .Single(item => item.Tag is ProjectTaskChoice task && task.Name == name);
                        return AutomationProperties.GetItemStatus(card).Length > 0;
                    }
                    var typed = FindControl<TextBox>("LaunchInput · caption");
                    PumpUntil(() => typed.IsKeyboardFocused, "应定位到执行顺序中第一个旧输入不合法的参数。");
                    Require(Marked("Boot") && Marked("ReadyTask") && typed.Text == "启动参数",
                        "字段改为整数后，前置任务和普通任务都应阻止相同的已存旧值。");
                    typed.Text = "仍无法转换";
                    ProjectOf(window).SetItemEnabled(PlanItemKind.MaaTask, "ReadyTask", false);
                    Click(window, "StartTaskHeaderButton");
                    typed = FindControl<TextBox>("LaunchInput · caption");
                    PumpUntil(() => typed.IsKeyboardFocused, "只勾选前置任务时也应定位未通过整数校验的草稿。");
                    Require(Marked("Boot") && !Marked("ReadyTask") && typed.Text == "仍无法转换",
                        "只勾选前置任务时应保留非法草稿，只标记参与本次运行的前置任务。");
                    typed.Text = "007";
                    VerifyPretaskPlan("修正整数后，前置程序应收到包含前导零的原文。", "007");
                    typed = FindControl<TextBox>("LaunchInput · caption");
                    Require(typed.Focus(), "前置任务成功提交后应能继续编辑。");
                    typed.Text = "仍无法转换";

                    caption["pipeline_type"] = "string";
                    File.WriteAllText(interfacePath, definition.ToJsonString());
                    VerifyPretaskPlan("规则放宽后仍应提交已保存的前置参数。", "007");
                    var draft = FindControl<TextBox>("LaunchInput · caption");
                    Require(draft.Text == "仍无法转换" && AutomationProperties.GetItemStatus(draft).Length == 0
                        && ConfigurationDescendants(items).OfType<TextBlock>()
                            .Any(note => note.IsVisible && note.Text.Contains("007") && note.Text.Contains("Esc")),
                        "有效的旧草稿仍应显示实际执行的已保存值，并提供 Esc 还原提示。");

                    File.WriteAllText(interfacePath, "{");
                    Click(window, "StartTaskHeaderButton");
                    Require(Named<UIElement>(window, "ProjectEmptyStatePanel").IsVisible
                        && Named<Button>(window, "RetryProjectLoadButton").IsVisible
                        && !Named<Button>(window, "StartTaskHeaderButton").IsEnabled,
                        "已连接时加载失败，任务页必须提供重新加载且不得继续提交旧计划。");
                }, session: session, coordinator: coordinator, projectDirectory: projectDirectory);
            } finally {
                timeout.Cancel();
                Task.Run(async () => await pipe.DisposeAsync()).GetAwaiter().GetResult();
            }
        }, runtimeProfileDigest: project.RuntimeProfileDigest);
    }

    private static void VerifyPretaskGuiWiring(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "pretask-gui"), (window, directory) => {
            EndTour(window);
            var project = ProjectOf(window);
            var catalog = Named<Panel>(window, "AvailableTasksPanel");
            var planPanel = Named<Panel>(window, "PlanItemsPanel");
            void Click(Button button)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpOnboarding();
            }
            Button ButtonNamed(DependencyObject root, string name) => ConfigurationDescendants(root)
                .OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
            Border[] Cards() => ConfigurationDescendants(planPanel).OfType<Border>()
                .Where(card => card.Tag is ProjectTaskChoice).ToArray();

            Click(Named<Button>(window, "TaskShelfHeaderButton"));
            Require(Cards().Length == 1, "未添加的启动项不能占用计划卡片。");
            foreach (var name in new[] { "RealTask", "SecondBoot" }) {
                Click(ConfigurationDescendants(catalog).OfType<Button>().Single(button =>
                    button.Tag is ProjectTaskChoice { Kind: PlanItemKind.Pretask } task && task.Name == name));
            }
            void Capture(string suffix)
            {
                var output = Environment.GetEnvironmentVariable("BUNSHIN_TASK_SCREENSHOTS");
                if (string.IsNullOrWhiteSpace(output)) {
                    return;
                }
                Directory.CreateDirectory(output);
                foreach (var width in new[] { 920, 1440 }) {
                    window.Width = width;
                    window.Height = width == 920 ? 640 : 900;
                    PumpOnboarding();
                    SaveScreenshot(window, Path.Combine(output, $"startup-{suffix}-{width}.png"));
                }
            }
            Click(Named<Button>(window, "TaskShelfHeaderButton"));
            Capture("collapsed");
            Require(Cards().Select(card => ((ProjectTaskChoice)card.Tag).Kind)
                    .SequenceEqual(new[] { PlanItemKind.Pretask, PlanItemKind.Pretask, PlanItemKind.MaaTask })
                && Cards().Select(card => ((ProjectTaskChoice)card.Tag).Name)
                    .SequenceEqual(new[] { "RealTask", "SecondBoot", "RealTask" }),
                "前置任务应集中显示在前，普通任务与前置任务允许同名。");
            bool HasDragHandle(Border card) => ConfigurationDescendants(card).OfType<Button>()
                .Any(button => button.IsVisible && button.IsEnabled
                    && button.Cursor == System.Windows.Input.Cursors.SizeAll);
            Require(Cards().All(HasDragHandle), "每一张已添加的卡片都应支持组内排序。");

            var preCard = Cards()[0];
            Click(PlanHeader(preCard));
            preCard = Cards()[0];
            Capture("expanded");
            var account = ConfigurationDescendants(preCard).OfType<TextBox>()
                .Single(editor => AutomationProperties.GetName(editor) == "启动账号");
            Require(!ConfigurationDescendants(preCard).OfType<TextBox>()
                .Any(editor => AutomationProperties.GetName(editor).StartsWith("Server range")),
                "前置参数编辑不应混入普通任务的全局参数。");
            account.Text = "account-text";
            Require(AutomationProperties.GetItemStatus(account).Length > 0,
                "前置任务的整数输入应在编辑时提示非法文本。");
            account.Text = "007";
            account.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, account, null) {
                RoutedEvent = Keyboard.LostKeyboardFocusEvent
            });
            Require(project.Configurations[0].ExplicitOptions["LoginAccount"].GetProperty("Inputs")
                .GetProperty("uin").GetString() == "007" && AutomationProperties.GetItemStatus(account).Length == 0,
                "前置任务的整数通过校验后应清除错误并保留输入的前导零。");
            foreach (var (index, label, expected) in new[] {
                (0, "启动并登录", "前置登录说明"), (2, "Real task", "A long task description")
            }) {
                Click(ButtonNamed(Cards()[index], $"查看 {label} 的任务描述"));
                var document = Named<FlowDocumentScrollViewer>(window, "TaskDescriptionViewer").Document;
                var text = new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd).Text;
                Require(Named<TextBlock>(window, "TaskDescriptionDrawerTitle").Text == label
                    && text.Contains(expected, StringComparison.Ordinal),
                    "同名的前置任务与普通任务应打开各自的说明。");
                Click(Named<Button>(window, "TaskDescriptionCloseButton"));
            }
            Click(PlanHeader(Cards()[2]));
            InvokeOnboarding(window, "ReplayOnboarding");
            PumpOnboarding();
            ClickOnboarding(window, "OnboardingNext");
            PumpOnboarding();
            Require(ConfigurationDescendants(Cards()[0]).OfType<TextBox>()
                .Any(editor => AutomationProperties.GetName(editor) == "启动账号"),
                "回放指引应展开位于最前的前置任务。");
            EndTour(window);
            PumpOnboarding();
            Require(ConfigurationDescendants(Cards()[2]).OfType<TextBox>().Any()
                && !ConfigurationDescendants(Cards()[0]).OfType<TextBox>().Any(),
                "回放结束应按种类恢复原先展开的普通同名任务。");

            System.Windows.Controls.CheckBox Selection(int index) => ConfigurationDescendants(Cards()[index])
                .OfType<System.Windows.Controls.CheckBox>().Single();
            void Select(int index, bool enabled)
            {
                var selection = Selection(index);
                selection.IsChecked = enabled;
                selection.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpOnboarding();
            }
            var configPath = Path.Combine(directory, "config", "configurations.json");
            var saved = File.ReadAllText(configPath);
            Require(Enumerable.Range(0, 3).All(index => Selection(index).IsChecked == true),
                "所有计划项默认勾选。");
            Select(0, false);
            Require(project.CreateRunStartAttempt(project.ActiveConfigurationId).Plan.Items
                .Select(item => (item.Kind, item.TaskName))
                .SequenceEqual(new[] { (PlanItemKind.Pretask, "SecondBoot"), (PlanItemKind.MaaTask, "RealTask") })
                && File.ReadAllText(configPath) != saved && project.SelectedItemCount == 3,
                "关闭启动项应保存配置，且不能跳过同名普通任务。");
            InvokeOnboarding(window, "RenderTaskPlan");
            Require(Selection(0).IsChecked == false, "重绘任务栏不能重置勾选状态。");
            InvokeOnboarding(window, "LoadProject");
            project = ProjectOf(window);
            Require(Selection(0).IsChecked == false && project.EnabledItems.Count == 2,
                "LoadProject 重载应保留勾选状态和过滤后的计划。");
            var originalConfiguration = project.ActiveConfigurationId;
            project.CreateConfiguration();
            project.AddItem(PlanItemKind.Pretask, "RealTask");
            Require(project.IsItemEnabled(PlanItemKind.Pretask, "RealTask"), "新配置不继承其他配置的勾选状态。");
            project.ActivateConfiguration(originalConfiguration);
            Require(project.SelectedPretaskNames.Contains("RealTask"), "切换配置应保留各自的启动选择。");
            Require(ProjectPlanModule.Open(directory, configPath).EnabledItems.Count == 2,
                "重新打开项目应保留启动开关。");
            foreach (var kind in new[] { PlanItemKind.Pretask, PlanItemKind.MaaTask }) {
                project.SetItemEnabled(kind, "RealTask", false);
                var reopened = ProjectPlanModule.Open(directory, configPath);
                Require(!reopened.IsItemEnabled(kind, "RealTask") && reopened.SelectedItemCount == 3,
                    "取消勾选后重新打开配置必须保留任务及其禁用状态。");
                project.RemoveItem(kind, "RealTask");
                project.AddItem(kind, "RealTask");
                Require(project.IsItemEnabled(kind, "RealTask"), "删除后重新添加任务必须恢复勾选。");
            }
            project.MoveTask("RealTask", 0, PlanItemKind.Pretask);
            InvokeOnboarding(window, "RenderTaskPlan");
            Select(0, false);
            Select(1, false);
            Select(2, false);
            Require(project.EnabledItems.Count == 0 && !Named<Button>(window, "StartTaskHeaderButton").IsEnabled,
                "全部取消后不得启动空计划。");
            try {
                project.CreateRunStartAttempt(project.ActiveConfigurationId);
                throw new InvalidDataException("全部取消后仍生成了运行计划。");
            } catch (InvalidOperationException) {
            }
            for (var index = 0; index < 3; index++) {
                Select(index, true);
            }

            var started = DateTime.UtcNow.AddSeconds(-5);
            var plan = project.CreateRunStartAttempt(project.ActiveConfigurationId).Plan;
            var states = new[] { PlanItemState.Succeeded, PlanItemState.Running, PlanItemState.Pending };
            PlanItemSnapshot Snapshot(RunPlanItem item, PlanItemState state, DateTime? startedAt, DateTime? endedAt) =>
                new(item.PlanItemId, item.TaskName, item.TaskLabel, item.Entry, item.ResolvedOptions,
                    item.PipelineOverride, state, startedAt, endedAt, null, null, null) { Kind = item.Kind };
            var run = new RunSnapshot(Guid.NewGuid(), "digest", RunState.Running, started, started, null, null,
                plan.Items[1].PlanItemId, 1, plan, plan.Items.Select((item, index) => Snapshot(item, states[index],
                    index < 2 ? started : null, index == 0 ? started : null)).ToArray(), null, null);
            var record = new WorkerAdmissionRecord(Guid.NewGuid(), "token", 7, Environment.ProcessId,
                project.RuntimeProfileDigest, DateTime.UtcNow);
            var idle = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
            void Observe(WorkerSnapshot worker)
            {
                SetOnboardingField(window, "_sessionSnapshot",
                    new ChildSessionSnapshot(ChildSessionState.ConnectedHidden, 7, 1, "fixture"));
                SetOnboardingField(window, "_workerSnapshot",
                    new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true, worker, ""));
                InvokeOnboarding(window, "RefreshRuntimeControls");
                PumpOnboarding();
            }
            var planText = ConfigurationDescendants(planPanel).OfType<TextBlock>().Select(text => text.Text).ToArray();
            Observe(idle with { RunState = RunState.Running, ActiveRun = run });
            Require(ConfigurationDescendants(planPanel).OfType<TextBlock>().Select(text => text.Text)
                .SequenceEqual(planText), "任务配置区不能随运行状态展示进度、耗时或执行结果。");
            Require(!Selection(0).IsEnabled && !Selection(2).IsEnabled
                && Named<TextBlock>(window, "RuntimeStatusTitle").Text == "第二个前置"
                && Named<Button>(window, "StopTaskHeaderButton").IsEnabled,
                "运行期间勾选被锁定，控制面板显示当前前置任务并可停止。");
            var lastWorker = idle with { LastRun = run with { State = RunState.Succeeded } };
            var kinds = project.SelectedItems.Select(item => (item.Kind, item.Name)).ToArray();
            Require(RunProgress.Derive(lastWorker, kinds) is not null
                && RunProgress.Derive(lastWorker, kinds.Select(item => (PlanItemKind.MaaTask, item.Name))
                    .ToArray()) is null, "Last Run 匹配必须包含种类，不能只看任务名称。");

            Observe(idle);
            Select(0, false);
            var filtered = project.CreateRunStartAttempt(project.ActiveConfigurationId).Plan;
            var filteredRun = run with {
                Plan = filtered, CurrentPlanItemId = filtered.Items[0].PlanItemId, CurrentPlanItemIndex = 0,
                Items = filtered.Items.Select((item, index) => Snapshot(item,
                    index == 0 ? PlanItemState.Running : PlanItemState.Pending,
                    index == 0 ? started : null, null)).ToArray()
            };
            Observe(idle with { RunState = RunState.Running, ActiveRun = filteredRun });
            Require(Named<TextBlock>(window, "RuntimeStatusTitle").Text == "第二个前置"
                && Named<TextBlock>(window, "RunProgressCaption").Text == "运行中 · 第 1 / 2 项",
                "跳过前置项后，控制面板应显示实际运行项和过滤后的进度。");
            Observe(idle);
            Select(0, true);
            Click(ButtonNamed(Cards()[2], "从执行计划移除 Real task"));
            Require(project.SelectedTaskNames.Count == 0 && project.SelectedItemCount == 2
                && Named<Button>(window, "StartTaskHeaderButton").IsEnabled,
                "只选择前置任务时仍可开始，移除普通同名任务不得影响前置任务。");
            var onlyPre = project.CreateRunStartAttempt(project.ActiveConfigurationId).Plan;
            var starting = run with {
                State = RunState.Starting, CurrentPlanItemId = onlyPre.Items[0].PlanItemId, CurrentPlanItemIndex = 0,
                Plan = onlyPre, Items = onlyPre.Items.Select((item, index) => index == 0
                    ? Snapshot(item, PlanItemState.Starting, started, null)
                    : Snapshot(item, PlanItemState.Pending, null, null)).ToArray()
            };
            Observe(idle with { RunState = RunState.Starting, ActiveRun = starting });
            Require(Named<Button>(window, "StopTaskHeaderButton").IsEnabled,
                "前置任务启动阶段也必须提供停止，不能等待游戏控制器创建。");
            Observe(idle);
            Click(ButtonNamed(Cards()[0], "从执行计划移除 启动并登录"));
            Require(project.SelectedPretaskNames.SequenceEqual(new[] { "SecondBoot" }),
                "前置任务的移除入口必须更新当前配置。");
        }, beforeLoad: directory => {
            var path = Path.Combine(directory, "interface.json");
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            root["pretask"] = JsonNode.Parse("""
                [
                  {"name":"RealTask","label":"启动并登录","description":"前置登录说明",
                   "exec":"python","option":["LoginAccount"]},
                  {"name":"SecondBoot","label":"第二个前置","exec":"python"}
                ]
                """);
            root["option"]!["LoginAccount"] = JsonNode.Parse("""
                {"type":"input","label":"启动账号","inputs":[
                  {"name":"uin","label":"账号","default":"123","pipeline_type":"int"}
                ]}
                """);
            File.WriteAllText(path, root.ToJsonString());
        });
    }

    private static void VerifyStartupWithRetiredTaskCase(AppLogger logger, string testDirectory)
    {
        const string configuration = "已保存的配置";
        var preparations = 0;
        MainWindow? current = null;
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "retired-task-case"), (window, _) => {
            current = window;
            var failures = 0;
            window.NotificationRequested += (_, _) => failures++;
            var full = window.AutoRunOnLaunchAsync();
            PumpUntil(() => full.IsCompleted, "普通任务的旧选项恢复后，完整运行应进入准备。");
            Require(full.GetAwaiter().GetResult() && failures == 0 && preparations == 1,
                "已移除的普通任务选项按当前默认恢复，无人值守启动应继续。");
        }, beforeLoad: directory => {
            var path = Path.Combine(directory, "interface.json");
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            root["pretask"] = JsonNode.Parse("""[{"name":"Boot","exec":"boot-fixture"}]""");
            File.WriteAllText(path, root.ToJsonString());
            var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config", "configurations.json"));
            project.RenameConfiguration(project.ActiveConfigurationId, configuration);
            project.AddItem(PlanItemKind.Pretask, "Boot");
            project.SetSelectedCase("Mode", "Minimal");

            var cases = root["option"]!["Mode"]!["cases"]!.AsArray();
            cases.Remove(cases.Single(item => item!["name"]!.GetValue<string>() == "Minimal"));
            File.WriteAllText(path, root.ToJsonString());
        }, recoveryCompleted: true, launchCommand: new LaunchCommand(AutoStart: true, Instance: configuration),
            prepareEnvironment: (_, _, _) => {
                preparations++;
                current!.SetExitInProgress(true);
                return Task.CompletedTask;
            });
    }

    private static void VerifyStartupDraftAfterProjectReload(AppLogger logger, string testDirectory)
    {
        foreach (var cold in new[] { false, true }) {
            foreach (var pretaskField in new[] { false, true }) {
                VerifySavedInputAfterUpgrade(logger, Path.Combine(testDirectory, $"upgrade-{cold}-{pretaskField}"),
                    cold, pretaskField, string.Empty);
            }
        }
        VerifySavedInputAfterUpgrade(logger, Path.Combine(testDirectory, "old-text"), false, false, "旧账号");
        VerifyReloadedStartupInput(logger, Path.Combine(testDirectory, "missing"), editDraft: false);
        VerifyReloadedStartupInput(logger, Path.Combine(testDirectory, "draft"), editDraft: true);
    }

    private static void VerifySavedInputAfterUpgrade(AppLogger logger, string directory, bool cold, bool pretaskField,
        string saved)
    {
        var root = CreatePretaskFixture(directory, """[{"name":"Boot","label":"游戏登录","exec":"boot-fixture"}]""");
        AddRequiredInput(root);
        var path = Path.Combine(root, "interface.json");
        var pi = JsonNode.Parse(File.ReadAllText(path))!;
        var input = pi["option"]!["Recipient"]!["inputs"]![0]!;
        input["verify"] = ".*";
        input["default"] = "默认账号";
        const string hint = "请填写当前账号";
        input["pattern_msg"] = hint;
        File.WriteAllText(path, pi.ToJsonString());
        var config = Path.Combine(root, "config", "configurations.json");
        var project = ProjectPlanModule.Open(root, config);
        project.AddItem(PlanItemKind.Pretask, "Boot");
        WithPlanWindow(logger, directory, project, (window, panel) => {
            SetOnboardingField(window, "_expandedTaskName", "RealTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            var editor = ConfigurationDescendants(panel).OfType<TextBox>()
                .Single(box => AutomationProperties.GetName(box) == "Recipient");
            editor.Text = saved;
            editor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, editor, null) {
                RoutedEvent = Keyboard.LostKeyboardFocusEvent
            });
        });
        var explicitInputs = JsonNode.Parse(File.ReadAllText(config))!["Configurations"]![0]!["ExplicitOptions"]!;
        Require((string?)explicitInputs["Recipient"]?["Inputs"]?["recipient"] == saved,
            "旧版编辑器应写入显式输入键，包括合法空串。");
        void Upgrade()
        {
            input["verify"] = "^当前.+$";
            if (pretaskField) {
                pi["pretask"]![0]!["option"] = new JsonArray("Recipient");
            }
            File.WriteAllText(path, pi.ToJsonString());
        }
        if (cold) {
            Upgrade();
        }
        var preparations = 0;
        WithRuntimeControlWindow(logger, directory, window => {
            var items = Named<Panel>(window, "PlanItemsPanel");
            var button = Named<Button>(window, "PrepareEnvironmentButton");
            SetFocus(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            Require(button.IsVisible && button.IsEnabled && button.Focus(), "启动入口应能定位需要修改的参数。");
            if (!cold) {
                Upgrade();
            }
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            bool FocusedField()
            {
                var name = pretaskField ? "Boot" : "RealTask";
                var card = ConfigurationDescendants(items).OfType<Border>()
                    .Single(control => control.Tag is ProjectTaskChoice task && task.Name == name);
                return ConfigurationDescendants(card).OfType<TextBox>().Any(field => field.IsKeyboardFocused
                    && field.Text == saved && AutomationProperties.GetItemStatus(field).Contains(hint));
            }
            PumpUntil(() => preparations > 0 || FocusedField(), "新版规则应展开本次运行的任务卡并聚焦错误字段。");
            Require(preparations == 0, "无效的已保存输入必须在准备环境之前阻止启动。");
        }, prepareEnvironment: (_, _, _) => {
            preparations++;
            return Task.FromCanceled(new CancellationToken(true));
        }, projectDirectory: root);
    }

    private static void VerifyReloadedStartupInput(AppLogger logger, string directory, bool editDraft)
    {
        var projectDirectory = CreatePretaskFixture(directory, """[{"name":"Boot","exec":"boot-fixture"}]""");
        AddRequiredInput(projectDirectory);
        var configPath = Path.Combine(projectDirectory, "config", "configurations.json");
        var project = ProjectPlanModule.Open(projectDirectory, configPath);
        project.AddItem(PlanItemKind.Pretask, "Boot");
        if (editDraft) {
            project.SetInputValue("Recipient", "recipient", "Kakashi");
        }

        var preparations = 0;
        WithRuntimeControlWindow(logger, directory, window => {
            var items = Named<Panel>(window, "PlanItemsPanel");
            T Find<T>(string name) where T : UIElement => ConfigurationDescendants(items).OfType<T>()
                .Single(element => AutomationProperties.GetName(element) == name);
            SetFocus(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            if (editDraft) {
                ToggleRealTask(items);
                var editor = Find<TextBox>("Recipient");
                PumpUntil(() => editor.IsLoaded, "普通任务展开后，参数输入框应加载。");
                Require(editor.Text == "Kakashi" && editor.Focus(), "应能编辑普通任务已保存的有效参数。");
                editor.Text = "  ";
            }
            var startup = Named<Button>(window, "PrepareEnvironmentButton");
            ProjectOf(window).SetItemEnabled(PlanItemKind.MaaTask, "RealTask", false);
            Require(startup.IsEnabled && startup.Focus() && ProjectOf(window).CreateRunStartAttempt().Plan.Items is
                [{ TaskName: "Boot", Program.Arguments.Count: 0 }], "重载前 Boot 不使用该字段，应能开始。");

            var path = Path.Combine(projectDirectory, "interface.json");
            var definition = JsonNode.Parse(File.ReadAllText(path))!;
            definition["pretask"]![0]!["option"] = new JsonArray("Recipient");
            File.WriteAllText(path, definition.ToJsonString());
            startup.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            bool InvalidInputFocusedInBoot()
            {
                var boot = ConfigurationDescendants(items).OfType<Border>()
                    .Single(card => card.Tag is ProjectTaskChoice { Kind: PlanItemKind.Pretask, Name: "Boot" });
                return ConfigurationDescendants(boot).OfType<TextBox>().Any(field => field.IsKeyboardFocused
                    && field.Text == (editDraft ? "  " : "") && AutomationProperties.GetName(field) == "Recipient");
            }
            PumpUntil(() => preparations > 0 || InvalidInputFocusedInBoot(), "重载后应在 Boot 内定位到无效输入。");
            Require(preparations == 0, "重载后 Boot 缺少有效必填值，必须在准备环境之前阻止启动。");
        }, prepareEnvironment: (_, _, _) => {
            preparations++;
            return Task.FromCanceled(new CancellationToken(canceled: true));
        }, projectDirectory: projectDirectory);
    }
}
