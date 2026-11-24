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
    private RunStartAttempt CreateConfigurationAttempt(ProjectPlanModule project, Guid configurationId) =>
        project.CreateRunStartAttempt(configurationId, inputErrors: TaskPlanEditor.InvalidInputs(configurationId));

    // A missing id must not throw: a configuration deleted in the meantime only loses its name in the log.
    private string? ConfigurationName(Guid id) =>
        _projectPlan?.Configurations.FirstOrDefault(item => item.Id == id)?.Name;

    private void ApplyScheduleHeaderStatus(StatusTone tone)
    {
        var name = ActiveConfigurationName;
        (string Text, string? Brush) status = tone switch {
            StatusTone.Busy => ($"桌面分身{RuntimeStatusText.Text} · {name}", "Brush.Warning"),
            StatusTone.Run => ($"桌面分身运行中 · {name}", "Brush.Success"),
            StatusTone.Bad => ($"桌面分身{RuntimeStatusText.Text} · {name}", "Brush.Error"),
            StatusTone.Ok => ("桌面分身已就绪", "Brush.Success"),
            _ => ("桌面分身未启动", null)
        };
        Header.SetScheduleStatus(status.Text, status.Brush);
    }
}
