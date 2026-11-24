using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ProjectModel;
using Symbol = Wpf.Ui.Controls.SymbolRegular;
using WpfButton = System.Windows.Controls.Button;

namespace Gui.Views;

public partial class MainWindow
{
    private ConfigurationMenu? _configurationMenu;
    private IReadOnlyList<Guid> _sidebarConfigurationOrder = [];

    private void InitializeConfigurationList()
    {
        var list = Sidebar.ConfigurationList;
        _sidebarConfigurationOrder = _settings.ReadSidebarConfigurationOrder();
        TaskPlanEditor.CommitConfigurationName = list.CommitRename;
        list.CanEdit = TaskPlanEditor.CanChangeConfiguration;
        list.SaveRename = TaskPlanEditor.RenameConfiguration;
        list.ConfigurationRequested += (_, id) => {
            if (TaskPlanEditor.ActivateConfiguration(id)) {
                DismissSidebarOverlay();
                if (_section != MainSection.Home) {
                    SwitchSection(MainSection.Home);
                }
            }
        };
        list.MenuRequested += (_, request) => OpenConfigurationMenu(request.Id, request.Anchor);
        list.MoveRequested += (_, request) => MoveSidebarConfiguration(request.Id, request.Index);
        Sidebar.NewConfigurationRequested += (_, anchor) => OpenNewConfigurationMenu(anchor);
        Header.CanEditConfiguration = TaskPlanEditor.CanChangeConfiguration;
        Header.ConfigurationMenuRequested += (_, anchor) => {
            if (_projectPlan is { } project) {
                OpenConfigurationMenu(project.ActiveConfigurationId, anchor);
            }
        };
    }

    private IReadOnlyList<TaskConfiguration> SidebarConfigurations(ProjectPlanModule project)
    {
        if (_sidebarConfigurationOrder.Count == 0) return project.Configurations;
        var positions = _sidebarConfigurationOrder.Distinct().Select((id, index) => (id, index))
            .ToDictionary(position => position.id, position => position.index);
        return project.Configurations.OrderBy(configuration => positions.GetValueOrDefault(configuration.Id, int.MaxValue))
            .ToArray();
    }

    internal void MoveSidebarConfiguration(Guid id, int index)
    {
        if (_projectPlan is not { } project || !TaskPlanEditor.CanChangeConfiguration()) return;
        var order = SidebarConfigurations(project).Select(configuration => configuration.Id).ToList();
        var previous = order.IndexOf(id);
        if (previous < 0 || index < 0 || index >= order.Count || previous == index) return;
        order.RemoveAt(previous);
        order.Insert(index, id);
        if (_settings.TrySaveSidebarConfigurationOrder(order)) {
            _sidebarConfigurationOrder = order;
            RefreshSidebarSchedule();
        }
    }

    private void OpenNewConfigurationMenu(WpfButton anchor)
    {
        if (!TaskPlanEditor.CanChangeConfiguration() || _projectPlan is not { } project) {
            return;
        }
        var menu = new ConfigurationMenu { MinWidth = 254, HorizontalOffset = -1 };
        menu.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Brush.Surface.Popover");
        var empty = menu.Item("新建空配置", Symbol.Document16, action: () =>
            ShowNewConfiguration(TaskPlanEditor.CreateEmptyConfiguration()));
        empty.Name = "CreateEmptyConfigurationMenuItem";
        menu.Items.Add(empty);
        if (project.Presets.Count != 0) {
            var presets = menu.Item("从预设新建", Symbol.Apps16);
            presets.Name = "CreatePresetConfigurationMenuItem";
            var caption = menu.Item("项目预设");
            caption.Height = 24;
            caption.IsEnabled = false;
            presets.Items.Add(caption);
            foreach (var preset in project.Presets) {
                var item = menu.Item(preset.Label, action: () =>
                    ShowNewConfiguration(TaskPlanEditor.CreatePresetConfiguration(preset.Name)));
                AutomationProperties.SetName(item, $"从预设新建配置：{preset.Label}");
                item.ToolTip = preset.Description;
                presets.Items.Add(item);
            }
            menu.Items.Add(presets);
        }
        ShowConfigurationMenu(menu, anchor);
    }

    private void ShowNewConfiguration(bool changed)
    {
        if (changed) {
            DismissSidebarOverlay();
            if (_section != MainSection.Home) {
                SwitchSection(MainSection.Home);
            }
            Sidebar.Home.Focus();
        }
    }

    private void OpenConfigurationMenu(Guid id, WpfButton anchor)
    {
        if (!TaskPlanEditor.CanChangeConfiguration() || _projectPlan is not { } project
            || project.Configurations.FirstOrDefault(c => c.Id == id) is not { } configuration) {
            return;
        }
        var menu = new ConfigurationMenu { HorizontalOffset = -9 };
        var rename = menu.Item("重命名", Symbol.Edit16, "F2", () => {
            if (Sidebar.IsCollapsed) {
                ShowSidebar(collapsed: false);
            }
            Sidebar.ConfigurationList.BeginRename(id);
        });
        rename.Name = "RenameConfigurationMenuItem";
        menu.Items.Add(rename);
        var duplicate = menu.Item("创建副本", Symbol.Copy16, action: () =>
            ShowNewConfiguration(TaskPlanEditor.DuplicateConfiguration(id)));
        duplicate.Name = "DuplicateConfigurationMenuItem";
        menu.Items.Add(duplicate);
        var preview = CreateSidebarSchedulePreview(project);
        var schedule = preview.ListState(configuration);
        var editSchedule = menu.Item("设置定时", Symbol.Clock16, schedule.Outcome is not null ? schedule.Text : null, () => {
            DismissSidebarOverlay();
            ScheduleView.ShowConfiguration(id);
            SwitchSection(MainSection.Schedule);
        });
        editSchedule.Name = "ScheduleConfigurationMenuItem";
        menu.Items.Add(editSchedule);
        menu.Items.Add(new Separator());
        var delete = menu.Item("删除", Symbol.Delete16, action: () => TaskPlanEditor.DeleteConfiguration(id),
            destructive: true);
        delete.Name = "DeleteConfigurationMenuItem";
        delete.IsEnabled = project.Configurations.Count > 1;
        menu.Items.Add(delete);
        ShowConfigurationMenu(menu, anchor);
    }

    private void ShowConfigurationMenu(ConfigurationMenu menu, WpfButton anchor)
    {
        if (_configurationMenu is { } previous) {
            previous.IsOpen = false;
        }
        _configurationMenu = menu;
        anchor.ContextMenu = menu;
        menu.PlacementTarget = anchor;
        menu.Closed += (_, _) => {
            if (ReferenceEquals(_configurationMenu, menu)) {
                _configurationMenu = null;
            }
            if (ReferenceEquals(anchor.ContextMenu, menu)) {
                anchor.ContextMenu = null;
            }
        };
        menu.IsOpen = true;
    }

    private void ApplyConfigurationLockToShell(bool editable, LockReason? reason)
    {
        if (!editable && _configurationMenu is { } menu) {
            menu.IsOpen = false;
        }
        Sidebar.ApplyConfigurationLock(editable, reason);
        Header.AllowConfigurationMenu(editable);
    }
}
