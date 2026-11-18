using ProjectModel;

namespace Gui.Views;

public partial class MainWindow
{
    // The schedule page predicts a configuration that cannot run with the same check a scheduled start uses.
    private string? ScheduleInvalidReason(Guid configurationId)
    {
        if (_projectPlan is not { } project) {
            return "项目未加载";
        }
        try {
            CreateScheduledAttempt(project, configurationId);
            return null;
        } catch (Exception exception) {
            return exception.Message;
        }
    }

    // Unfixed invalid drafts block the configuration like they block Start, without saving them.
    private RunStartAttempt CreateScheduledAttempt(ProjectPlanModule project, Guid configurationId)
    {
        var drafts = _inputDrafts.Where(pair => pair.Key.Configuration == configurationId)
            .ToDictionary(pair => (pair.Key.Option, pair.Key.Input), pair => pair.Value);
        return project.CreateRunStartAttempt(configurationId, inputDrafts: drafts);
    }

    // The capsule repeats the home page's run status in four words; the console itself stays on the home page.
    private void ApplyScheduleCapsule(StatusTone tone)
    {
        var name = ActiveConfigurationName;
        var (text, scheduleTone) = tone switch {
            StatusTone.Busy => ($"{RuntimeStatusText.Text} · {name}", ScheduleRuntimeTone.Busy),
            StatusTone.Run => ($"运行中 · {name}", ScheduleRuntimeTone.Run),
            StatusTone.Bad => ($"失败 · {name}", ScheduleRuntimeTone.Failed),
            _ => ("空闲", ScheduleRuntimeTone.Idle)
        };
        ScheduleView.SetRuntimeStatus(text, scheduleTone);
    }
}
