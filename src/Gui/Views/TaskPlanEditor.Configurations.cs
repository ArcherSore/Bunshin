using System.Windows.Input;
using Gui.Infrastructure;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Gui.Views;

public sealed partial class TaskPlanEditor
{
    private bool _committingConfigurationInput;
    private Guid? _shownConfigurationId;

    internal Func<bool, bool>? CommitConfigurationName { get; set; }

    internal bool CommitFocusedConfigurationInput(bool unattended = false)
    {
        if (Keyboard.FocusedElement is WpfTextBox { Tag: OptionInputTag } editor) {
            return CommitOptionInput(editor, unattended);
        }
        return CommitConfigurationName?.Invoke(unattended) ?? true;
    }

    internal bool CanChangeConfiguration() =>
        Project is not null && Editable && CommitFocusedConfigurationInput();

    internal bool CreateEmptyConfiguration() => ChangeConfiguration(() => {
        var id = Project!.CreateConfiguration();
        _logger.Info($"已新建配置{AppLogger.Field("name", ConfigurationName(id))}");
    });

    internal bool CreatePresetConfiguration(string name) => ChangeConfiguration(() => {
        var id = Project!.CreateConfigurationFromPreset(name);
        _logger.Info($"已从预设新建配置{AppLogger.Field("preset", name)}"
            + AppLogger.Field("name", ConfigurationName(id)));
    });

    internal bool ActivateConfiguration(Guid id) => Project?.ActiveConfigurationId == id
        || ChangeConfiguration(() => {
            var from = Project!.ActiveConfigurationId;
            Project.ActivateConfiguration(id);
            _logger.Info($"已切换配置{AppLogger.Field("from", ConfigurationName(from))}"
                + AppLogger.Field("to", ConfigurationName(id)));
        });

    internal bool DuplicateConfiguration(Guid id) => ChangeConfiguration(() => {
        var copy = Project!.DuplicateConfiguration(id);
        _logger.Info($"已创建配置副本{AppLogger.Field("from", ConfigurationName(id))}"
            + AppLogger.Field("name", ConfigurationName(copy)));
    });

    internal void DeleteConfiguration(Guid id) => ChangeConfiguration(() => {
        var name = ConfigurationName(id);
        if (Project!.DeleteConfiguration(id)) {
            _logger.Info($"已删除配置{AppLogger.Field("name", name)}"
                + AppLogger.Field("active", ConfigurationName(Project.ActiveConfigurationId)));
        }
    });

    internal bool MoveConfiguration(Guid id, int index) =>
        ChangeConfiguration(() => Project!.MoveConfiguration(id, index), refreshOnly: true);

    internal bool RenameConfiguration(Guid id, string name, bool unattended)
    {
        if (!Editable || Project is not { } project) {
            return false;
        }
        if (string.IsNullOrWhiteSpace(name)) {
            return true;
        }
        try {
            var from = ConfigurationName(id);
            project.RenameConfiguration(id, name);
            if (ConfigurationName(id) != from) {
                _logger.Info($"已重命名配置{AppLogger.Field("from", from)}"
                    + AppLogger.Field("to", ConfigurationName(id)));
            }
            ConfigurationsRendered?.Invoke(this, project);
            return true;
        } catch (Exception exception) when (!unattended) {
            _editFailed("保存配置名称失败", exception);
            return false;
        }
    }

    private string? ConfigurationName(Guid id) =>
        Project?.Configurations.FirstOrDefault(item => item.Id == id)?.Name;

    // A scheduled start has already saved the activation, so the workspace only follows it.
    internal void RenderActivatedConfiguration(Guid configurationId) =>
        RenderConfigurationChange(_shownConfigurationId != configurationId);

    private void RenderConfigurationChange(bool switched)
    {
        if (switched) {
            _addedSinceShelfOpened.Clear();
            UpdateAddedTasks();
        }
        PlanEdited?.Invoke(this, EventArgs.Empty);
        _expandedTask = null;
        _dragTask = null;
        TaskDescriptionCloseRequested?.Invoke(this, EventArgs.Empty);
        RenderTaskPlan();
    }

    // An unattended activation supplies its own failure reporting, without a dialog nobody can answer.
    internal bool ChangeConfiguration(Action change, Action<Exception>? unattendedFailure = null,
        bool refreshOnly = false)
    {
        try {
            if (!CanChangeConfiguration()) {
                unattendedFailure?.Invoke(new InvalidOperationException("无法切换到所选配置。"));
                return false;
            }
            var project = Project!;
            var previous = project.ActiveConfigurationId;
            change();
            if (refreshOnly) {
                ConfigurationsRendered?.Invoke(this, project);
            } else {
                RenderConfigurationChange(project.ActiveConfigurationId != previous);
            }
            return true;
        } catch (Exception exception) {
            if (unattendedFailure is not null) {
                unattendedFailure(exception);
            } else {
                HandleProjectEditError("保存任务配置失败", exception);
            }
            return false;
        }
    }

    private bool CommitOptionInput(WpfTextBox textBox, bool unattended = false)
    {
        var text = GetInputText(textBox);
        if (_updatingOptionEditors || _committingConfigurationInput || !Editable
            || textBox.Tag is not OptionInputTag tag || tag.Submitted && text == tag.Value) {
            return true;
        }
        if (_invalidInputDrafts.TryGetValue(tag.Key, out var draft) && draft.Text == text) {
            return false;
        }
        _committingConfigurationInput = true;
        try {
            if (Project!.CheckInputValue(tag.OptionName, tag.InputName, text) is { } problem) {
                // A rejected value is a typo, not a failure: keep the text and explain it under the field.
                // The saved value is unchanged; the draft survives re-rendering until it is fixed or Esc restores it.
                var message = tag.PatternMessage ?? problem;
                _invalidInputDrafts[tag.Key] = new InputDraft(text, message);
                ShowOptionInputError(textBox, message);
                RefreshParameterDisplays();
                _logger.Info($"input 未通过校验，未保存：option={tag.OptionName}，input={tag.InputName}。 ");
                return false;
            }
            Project.SetInputValue(tag.ConfigurationId, tag.OptionName, tag.InputName, text);
            _invalidInputDrafts.Remove(tag.Key);
            textBox.Tag = tag with { Value = text, Submitted = true };
            PlanEdited?.Invoke(this, EventArgs.Empty);
            ShowOptionInputError(textBox, null);
            RefreshAfterOptionSaved(rebuildCards: false);
            return true;
        } catch (Exception exception) when (!unattended) {
            SetInputText(textBox, tag.Value);
            textBox.Tag = tag with { Submitted = true };
            _editFailed("保存 input option 失败", exception);
            ShowProjectValidationError(exception);
            return false;
        } finally {
            _committingConfigurationInput = false;
        }
    }
}
