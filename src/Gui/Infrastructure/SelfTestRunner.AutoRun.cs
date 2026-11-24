using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using ProjectModel;
using Gui.Views;
using Protocol;
using Gui.Worker;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyAutoRunPlanner()
    {
        var daily = new AutoRunTarget(Guid.NewGuid(), "日常");
        var weekly = new AutoRunTarget(Guid.NewGuid(), "周常");
        var twin = new AutoRunTarget(Guid.NewGuid(), "日常");
        AutoRunTarget[] configurations = [daily, weekly];
        AutoRunDecision Decide(LaunchCommand command, Guid? configured = null, bool onManualLaunch = false,
            AutoRunTarget[]? all = null, bool canStart = true) =>
            AutoRunPlanner.Decide(command, configured, onManualLaunch, all ?? configurations, canStart);
        bool Skipped(LaunchCommand command, Guid? configured = null, bool onManualLaunch = false,
            AutoRunTarget[]? all = null, bool canStart = true) =>
            Decide(command, configured, onManualLaunch, all, canStart).ConfigurationId is null;
        var manual = new LaunchCommand();
        var boot = new LaunchCommand(AutoStart: true);

        Require(Skipped(manual, daily.Id), "手动启动且未开启“手动启动时也自动执行”时不得执行。");
        Require(Decide(manual, daily.Id, onManualLaunch: true).ConfigurationId == daily.Id, "手动启动开启后应执行所选配置。");
        Require(Decide(boot, daily.Id).ConfigurationId == daily.Id, "开机自启应执行所选配置。");
        Require(Skipped(boot) && Skipped(manual, null, onManualLaunch: true), "没有目标配置时不得执行。");
        Require(Skipped(boot, Guid.NewGuid()) && Skipped(manual, Guid.NewGuid(), onManualLaunch: true),
            "所选配置已不存在时不得执行，也不得改跑别的配置。");

        Require(Decide(boot with { Instance = "周常" }, daily.Id).ConfigurationId == weekly.Id,
            "--instance 必须优先于设置里选的配置。");
        Require(Decide(boot with { Instance = weekly.Id.ToString() }, daily.Id).ConfigurationId == weekly.Id,
            "--instance 也接受配置 ID。");
        Require(Skipped(boot with { Instance = "不存在" }, daily.Id), "--instance 找不到时不得回退到设置里的配置。");
        AutoRunTarget[] twins = [daily, twin, weekly];
        Require(Skipped(boot with { Instance = "日常" }, weekly.Id, all: twins), "配置重名时按名称指定必须拒绝，不能挑第一个。");
        Require(Decide(boot with { Instance = twin.Id.ToString() }, all: twins).ConfigurationId == twin.Id,
            "重名时仍可用 ID 精确指定。");
        Require(Decide(manual with { Instance = "周常" }, daily.Id, onManualLaunch: true).ConfigurationId == daily.Id,
            "--instance 只对 --autostart 生效，手动启动仍用设置里的配置。");

        Require(Skipped(boot, daily.Id, canStart: false), "运行控制栏不允许准备或开始时必须跳过。");

        // A launch that asked for an auto-run and did not get one says why, and the reasons tell the cases apart.
        string?[] reasons = [
            Decide(boot with { Instance = "不存在" }, daily.Id).SkipReason, Decide(boot).SkipReason,
            Decide(boot, daily.Id, canStart: false).SkipReason];
        Require(reasons.All(reason => !string.IsNullOrWhiteSpace(reason))
            && reasons.Distinct().Count() == reasons.Length,
            "被跳过的自动执行必须给出能区分情形的原因。");
        Require(Decide(manual, daily.Id).SkipReason is null && Decide(boot, daily.Id).SkipReason is null,
            "没有请求自动执行，或已经执行时，不应有跳过原因。");

        // The runtime decides, not the configuration that is about to be replaced: an environment that is ready
        // while the active configuration has no tasks or is invalid still allows the other one to start.
        RuntimeControlState Controls(RuntimeAction action, bool editable = true) => new(
            action, editable, null, new DesktopToggle(DesktopAction.Show, false, DesktopStatus.NotRunning), true);
        foreach (var allowed in new RuntimeAction[] {
            new RuntimeAction.Prepare(true), new RuntimeAction.Start(true),
            new RuntimeAction.Start(false, StartBlocker.NoTasks),
            new RuntimeAction.Start(false, StartBlocker.ConfigurationInvalid),
            new RuntimeAction.Retry(RetryTarget.Start, true),
            new RuntimeAction.Retry(RetryTarget.Start, false, StartBlocker.NoTasks)
        }) {
            Require(AutoRunPlanner.RuntimeAllows(Controls(allowed)), $"运行环境允许开始时不能被当前配置挡住：{allowed}");
        }
        foreach (var refused in new RuntimeAction[] {
            new RuntimeAction.Prepare(false), new RuntimeAction.Start(false, StartBlocker.RuntimeNotReady),
            new RuntimeAction.Start(false, StartBlocker.CommandsUnavailable), new RuntimeAction.Stop(true),
            new RuntimeAction.Recover(true, RecoveryReason.WorkerLost),
            new RuntimeAction.Retry(RetryTarget.Prepare, true),
            new RuntimeAction.Retry(RetryTarget.Start, false, StartBlocker.RuntimeNotReady)
        }) {
            Require(!AutoRunPlanner.RuntimeAllows(Controls(refused)), $"运行环境不允许开始时必须跳过：{refused}");
        }
        Require(!AutoRunPlanner.RuntimeAllows(Controls(new RuntimeAction.Prepare(true), editable: false)),
            "配置被锁定（已有 Run 或运行环境忙）时必须跳过。");
    }


    // Two configurations, "日常" active and "周常" second, each with the project's first task unless left empty.
    private static Action<string> TwoConfigurations(bool dailyHasTask = true, bool weeklyHasTask = true) =>
        projectDirectory => {
            Directory.CreateDirectory(Path.Combine(projectDirectory, "config"));
            var project = ProjectPlanModule.Open(
                projectDirectory, Path.Combine(projectDirectory, "config", "configurations.json"));
            var daily = project.ActiveConfigurationId;
            var task = project.SelectedTaskNames[0];
            project.RenameConfiguration(daily, "日常");
            project.ActivateConfiguration(project.CreateConfiguration());
            project.RenameConfiguration(project.ActiveConfigurationId, "周常");
            if (weeklyHasTask) {
                project.AddItem(PlanItemKind.MaaTask, task);
            }
            project.ActivateConfiguration(daily);
            if (!dailyHasTask) {
                project.RemoveItem(PlanItemKind.MaaTask, task);
            }
        };

    private static void VerifyAutoRunOnLaunch(AppLogger logger, string testDirectory)
    {
        var boot = new LaunchCommand(AutoStart: true, QuitAfterRun: true);
        var prepared = new List<string>();
        var failures = new List<string>();
        MainWindow? current = null;
        var desktopShown = new List<bool>();
        Task PrepareThenFail(ProjectPlanModule project, Func<MaaFrameworkOptions> options, bool showDesktop)
        {
            prepared.Add(project.Configurations.Single(item => item.Id == project.ActiveConfigurationId).Name);
            desktopShown.Add(showDesktop);
            throw new InvalidOperationException("模拟准备失败");
        }
        // Prepares for good, but an exit is requested meanwhile, so the Run is never started and nothing failed.
        Task PrepareThenExit(ProjectPlanModule project, Func<MaaFrameworkOptions> options, bool showDesktop)
        {
            prepared.Add(project.Configurations.Single(item => item.Id == project.ActiveConfigurationId).Name);
            current!.SetExitInProgress(true);
            return Task.CompletedTask;
        }
        // Runs one launch through the window's own startup, then asks it to act on the launch.
        void Launch(string name, LaunchCommand command, string? target, bool onManualLaunch,
            Action<MainWindow, bool> verify, Action<MainWindow, string>? beforeAutoRun = null,
            Action<string>? fixture = null,
            Func<ProjectPlanModule, Func<MaaFrameworkOptions>, bool, Task>? prepare = null)
        {
            prepared.Clear();
            desktopShown.Clear();
            failures.Clear();
            RunOnboardingScenario(logger, Path.Combine(testDirectory, name), (window, directory) => {
                CompleteOnboardingStartup(window);
                var page = SettingsPageFor(window).Sections.SelectMany(section => section.Items).ToArray();
                var choice = page.Single(item => item.Definition.ChoiceKey == "autorun.configuration").Choice!;
                if (target is not null) {
                    choice.Selected = choice.Options.Single(option => option.Name == target);
                }
                page.Single(item => item.Definition.SettingKey == "autorun.onManualLaunch").Toggle!.Value
                    = onManualLaunch;
                current = window;
                window.NotificationRequested += (_, n) => failures.Add(n.Title + " / " + n.Message);
                beforeAutoRun?.Invoke(window, directory);
                var run = window.AutoRunOnLaunchAsync();
                PumpUntil(() => run.IsCompleted, "自动执行必须结束，失败时不能停在模态框上。");
                verify(window, run.Result);
            }, beforeLoad: fixture ?? TwoConfigurations(), launchCommand: command,
                prepareEnvironment: prepare ?? PrepareThenFail);
        }

        Launch("manual", new LaunchCommand(), "周常", false, (_, ran) =>
            Require(!ran && prepared.Count == 0, "手动启动且未开启开关时不得自动执行。"));
        Launch("boot", boot, "周常", false, (window, ran) => {
            Require(ran && prepared.SequenceEqual(["周常"]), "开机自启必须先激活所选配置再准备并开始。");
            Require(desktopShown.SequenceEqual([false]), "自动执行准备运行环境时不得弹出分身窗口。");
            Require(failures is [{ } failure] && failure.StartsWith("自动执行未能完成 / ") && failure.Contains("模拟准备失败"),
                "自动执行开始前失败必须报告，而不是弹模态框。");
            Require(ProjectOf(window).ActiveConfigurationId == ProjectOf(window).Configurations
                .Single(item => item.Name == "周常").Id, "激活的配置必须保存下来。");
        });
        Launch("exit-requested", boot, "周常", false, (_, ran) =>
            Require(ran && prepared.SequenceEqual(["周常"]) && failures.Count == 0,
                "准备时用户要求退出，应结束自动执行并避免报告任务失败。"),
            prepare: PrepareThenExit);
        Launch("manual-flag", new LaunchCommand(), "周常", true, (_, ran) =>
            Require(ran && prepared.SequenceEqual(["周常"]), "开启“手动启动时也自动执行”后手动启动应自动执行。"));
        Launch("instance", boot with { Instance = "周常" }, "日常", false, (_, ran) =>
            Require(ran && prepared.SequenceEqual(["周常"]), "--instance 必须优先于设置里的配置。"));
        Launch("unknown-instance", boot with { Instance = "不存在" }, "日常", false, (_, ran) =>
            Require(!ran && prepared.Count == 0 && failures is [{ } failure]
                && failure.StartsWith("自动执行未能完成 / ") && failure.Contains("不存在"),
                "--instance 找不到时必须跳过并提示一次，不回退到其他配置。"));
        Launch("duplicate-instance", boot with { Instance = "日常" }, null, false, (_, ran) =>
            Require(!ran && prepared.Count == 0 && failures.Count == 1,
                "--instance 名称重复时必须跳过并提示一次。"),
            fixture: directory => {
                TwoConfigurations()(directory);
                var project = ProjectPlanModule.Open(
                    directory, Path.Combine(directory, "config", "configurations.json"));
                project.RenameConfiguration(project.Configurations.Single(item => item.Name == "周常").Id, "日常");
            });
        Launch("not-startable", boot, "周常", false, (_, ran) =>
            Require(!ran && prepared.Count == 0 && failures.Count == 1, "运行控制栏不允许开始时必须跳过并提示一次。"),
            (window, _) => window.SetExitInProgress(true));
        Launch("not-startable-manual-flag", new LaunchCommand(), "周常", true, (_, ran) =>
            Require(!ran && prepared.Count == 0 && failures.Count == 0, "设置触发的自动执行被跳过时不提示。"),
            (window, _) => window.SetExitInProgress(true));
        Launch("autostart-without-configuration", boot, null, false, (_, ran) =>
            Require(!ran && prepared.Count == 0 && failures.Count == 0, "没有选择自动执行的配置时不提示。"));
        Launch("empty-active-configuration", boot, "周常", false, (_, ran) =>
            Require(ran && prepared.SequenceEqual(["周常"]), "目标配置有效时，原来激活的配置没有任务不能挡住它。"),
            fixture: TwoConfigurations(dailyHasTask: false));
        Launch("target-without-tasks", boot, "周常", false, (_, ran) =>
            Require(ran && prepared.Count == 0 && failures.Count == 1,
                "目标配置没有任务时必须报告失败，不能白白创建桌面分身。"),
            fixture: TwoConfigurations(weeklyHasTask: false));
        var twoConfigurations = TwoConfigurations();
        Launch("unreadable-configurations", boot with { Instance = "周常" }, null, false, (_, ran) =>
            Require(ran && prepared.Count == 0 && failures.Count == 1,
                "任务配置读不出来时必须报告失败，不能悄悄跳过，也不能运行占位的配置。"),
            fixture: directory => {
                twoConfigurations(directory);
                File.WriteAllText(Path.Combine(directory, "config", "configurations.json"), "{ not json");
            });
        FileStream? locked = null;
        Launch("switch-refused", boot, "周常", false, (window, ran) => {
            locked?.Dispose();
            Require(ran && prepared.Count == 0 && failures.Count == 1,
                "无法切换到目标配置时必须报告失败，不能弹模态框，也不能改跑原来的配置。");
            Require(ProjectOf(window).Configurations.Single(item => item.Name == "日常").Id
                == ProjectOf(window).ActiveConfigurationId, "切换失败后仍是原配置。");
        }, (_, directory) => locked = new FileStream(
            Path.Combine(directory, "config", "configurations.json"),
            FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    private static void VerifyUnattendedStartupDialogs(AppLogger logger, string testDirectory)
    {
        static Visibility OverlayOf(MainWindow window, string name) => Named<UIElement>(window, name).Visibility;
        void Welcome(string directory) => SetWelcome(directory, new JsonArray("公告"), "项目标题");
        foreach (var command in new[] { new LaunchCommand(), new LaunchCommand(AutoStart: true) }) {
            var unattended = command.AutoStart;
            var name = unattended ? "boot" : "manual";
            RunOnboardingScenario(logger, Path.Combine(testDirectory, name), (window, directory) => {
                CompleteOnboardingStartup(window);
                InvokeOnboarding(window, "ReevaluateOnboarding");
                PumpOnboarding(500);
                Require(window.IsLoaded && window.IsVisible != unattended,
                    "开机自启必须完成加载但不显示窗口；手动启动显示窗口。");
                var recorded = File.Exists(Path.Combine(directory, "config", "welcome-shown.txt"))
                    || File.Exists(Path.Combine(directory, "config", "onboarding.txt"));
                Require(unattended
                    ? OverlayOf(window, "ProjectReader") == Visibility.Collapsed
                        && OverlayOf(window, "OnboardingOverlay") == Visibility.Collapsed && !recorded
                    : OverlayOf(window, "ProjectReader") == Visibility.Visible,
                    unattended ? "开机自启时不得弹出公告或新手指引，也不得记为已读。" : "手动启动仍须弹出公告。");
            }, beforeLoad: Welcome, launchCommand: command);
        }
    }
}
