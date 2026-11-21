using System.Diagnostics;
using System.Text.Json;
using Gui.ChildSession;
using Gui.Models;
using Gui.Views;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyScheduledIdleNotReady(AppLogger logger, string directory, string root)
    {
        using var timeout = new CancellationTokenSource(OnPoolLimit);
        var token = timeout.Token;
        using var process = Process.GetCurrentProcess();
        var sessionId = (uint)process.SessionId;
        var path = process.MainModule!.FileName;
        var worker = new WorkerProcessEntry(process.Id, sessionId, Path.GetFileName(path));
        var listed = false;
        var wts = new SimulatedWts();
        using var session = wts.CreateManager(logger);
        var pipeName = $"Bunshin.IdleNotReady.{Guid.NewGuid():N}";
        TwoConfigurations()(root);
        var configPath = Path.Combine(root, "config", "configurations.json");
        var project = ProjectPlanModule.Open(root, configPath);
        var launched = new TaskCompletionSource<WorkerAdmissionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new WorkerCoordinator(logger, Path.Combine(directory, "state"), path, pipeName,
            usePipeAcl: false, captureProcesses: () => listed ? [worker] : [],
            launchWorker: (id, instance, secret, _, _) => {
                listed = true;
                launched.SetResult(new(instance, secret, id, process.Id, project.RuntimeProfileDigest, DateTime.UtcNow));
                return Task.FromResult(new VerifiedChildSessionProcessLaunch((uint)process.Id, id, null, null));
            });
        try {
            WithRuntimeControlWindow(logger, directory, window => {
                var check = window.Dispatcher.InvokeAsync(() => ExerciseAsync(window)).Task.Unwrap();
                PumpUntil(() => check.IsCompleted, "空闲 NotReady 分身应允许定时换场和成功收尾。");
                check.GetAwaiter().GetResult();
            }, session: session, coordinator: coordinator, environment: CreateEnvironment(logger, session, coordinator),
                prepareEnvironment: (plan, options, _) => Prepare(plan, options()));
        } finally {
            timeout.Cancel();
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }

        Task Prepare(ProjectPlanModule plan, MaaFrameworkOptions options)
        {
            wts.ReportedId = sessionId;
            wts.Sessions.Add(sessionId);
            return coordinator.PrepareWorkerAsync(sessionId, plan, token, options);
        }

        async Task ExerciseAsync(MainWindow window)
        {
            var old = Prepare(project, new());
            var oldRecord = await launched.Task.WaitAsync(token);
            await using var oldPipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(pipeName, oldRecord, 0,
                token, initialState: WorkerState.NotReady);
            await old;

            launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var target = project.Configurations.Single(c => c.Name == "周常");
            var activatedBeforeLogoff = false;
            wts.DuringLogoff = _ => {
                listed = false;
                using var saved = JsonDocument.Parse(File.ReadAllBytes(configPath));
                activatedBeforeLogoff = saved.RootElement.GetProperty("ActiveConfigurationId").GetGuid() == target.Id
                    && window.Dispatcher.Invoke(() => window.TaskPlanEditor.ConfigurationTabs.SelectedItem
                        is System.Windows.Controls.TabItem
                        { Tag: Guid selected } && selected == target.Id
                        && window.LogLines.OfType<LogEntry>()
                            .Any(e => e.TaskLabel == "定时" && e.Message == $"启动「{target.Name}」"));
            };
            var start = window.TryStartScheduledRunAsync(target.Id);
            await Task.WhenAny(start, launched.Task).WaitAsync(token);
            Require(launched.Task.IsCompletedSuccessfully && wts.LogoffAttempts.SequenceEqual([sessionId]),
                "旧 Worker 空闲且 NotReady 时应注销旧分身并准备新的 Worker。");
            Require(activatedBeforeLogoff, "注销旧分身前，应已写盘、显示目标标签并记下启动动态。");
            wts.DuringLogoff = _ => listed = false;
            await oldPipe.DisposeAsync();
            var next = await launched.Task;
            await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(pipeName, next, 0, token);
            var request = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
            var payload = ProtocolJson.Deserialize<RunStartRequest>(request.Data);
            await pipe.WriteAsync(WireEnvelope.Response(request.Operation, request.RequestId!.Value,
                new RunStartResponse("accepted")), token);
            await start;

            var terminal = SucceededRun(payload);
            var idle = WorkerCoordinatorSelfTest.CreateSnapshot(next, 0) with {
                WorkerState = WorkerState.NotReady, StateRevision = 2, LastRun = terminal
            };
            await pipe.WriteAsync(StateEvent(idle), token);
            PumpUntil(() => window.LogLines.OfType<LogEntry>()
                .Any(e => e.TaskLabel == "定时" && e.Message == "全部完成，已关闭桌面分身"),
                "匹配的成功终态到达时，即使 Worker 已 NotReady，也应显示关闭分身的运行动态。");
            Require(!session.HasChildSession && wts.LogoffAttempts.SequenceEqual([sessionId, sessionId]),
                "成功收尾应再注销一次本次定时任务自己的分身。");
        }
    }
}
