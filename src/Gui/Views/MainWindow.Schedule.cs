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
            CreateConfigurationAttempt(project, configurationId);
            return null;
        } catch (Exception exception) {
            return exception.Message;
        }
    }

    // Unfixed invalid drafts block the configuration like they block Start, without saving them.
    private RunStartAttempt CreateConfigurationAttempt(ProjectPlanModule project, Guid configurationId)
    {
        var drafts = _invalidInputDrafts.Where(pair => pair.Key.Item1 == configurationId)
            .ToDictionary(pair => (pair.Key.Item2, pair.Key.Item3), pair => pair.Value.Message);
        return project.CreateRunStartAttempt(configurationId, inputErrors: drafts);
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
