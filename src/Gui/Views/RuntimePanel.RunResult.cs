using System.Windows;
using Gui.Settings;

namespace Gui.Views;

// The 本次运行 / 上次运行 section: how far a Run got and how each of its Plan Items ended.
public partial class RuntimePanel
{
    private sealed record RunOutcome(PlanItemProgress State, string Label, string Value);

    // A segment of the meter and a row of 全部任务; only the time of a running item changes while it is shown.
    private sealed class RunItemRow(PlanItemProgressView item) : SettingsObservable
    {
        private string _status = "";

        public PlanItemProgress State => item.State;
        public string Name => item.TaskLabel;
        public string Status { get => _status; private set => Set(ref _status, value); }

        internal void Tick(DateTime nowUtc)
        {
            var label = StateLabel(item.State);
            Status = RunProgress.Elapsed(item.StartedAtUtc, item.EndedAtUtc, nowUtc) is not { } elapsed ? label
                : item.State == PlanItemProgress.Succeeded ? RunProgress.FormatDuration(elapsed)
                : $"{label} · {RunProgress.FormatDuration(elapsed)}";
        }
    }

    private RunProgressView? _run;
    private RunItemRow[] _runRows = [];

    // The section exists only while there is a Run to describe.
    internal void ShowRunResult(RunProgressView? run)
    {
        RunResultSection.Visibility = run is null ? Visibility.Collapsed : Visibility.Visible;
        if (run is null) {
            _run = null;
            return;
        }
        RunResultSectionToggle.Content = run.Active ? "本次运行" : "上次运行";
        NameToggle(RunResultSectionToggle);
        RunResultCount.Text = run.Active
            ? $"{run.Processed} / {run.Total} 项完成" : $"{run.Succeeded} / {run.Total} 项成功";
        // Observations repeat; rows are rebuilt only when an item changed, so a tooltip over one survives.
        if (_run?.Items.SequenceEqual(run.Items) != true) {
            _runRows = run.Items.Select(item => new RunItemRow(item)).ToArray();
            RunResultMeter.ItemsSource = _runRows;
            RunResultOutcomes.ItemsSource = Outcomes(run).ToArray();
            RunResultAllCount.Text = run.Total.ToString();
            RunResultAllItems.ItemsSource = _runRows;
        }
        _run = run;
        UpdateRunTiming();
    }

    internal void UpdateRunTiming()
    {
        if (_run is not { } run) {
            return;
        }
        var now = DateTime.UtcNow;
        var total = RunProgress.FormatDuration(
            RunProgress.Elapsed(run.StartedAtUtc, run.EndedAtUtc, now) ?? TimeSpan.Zero);
        RunResultTime.Text = run.Active ? $"总用时 {total}" : $"{run.EndedAtUtc?.ToLocalTime():HH:mm} · 用时 {total}";
        foreach (var row in _runRows) {
            row.Tick(now);
        }
    }

    // About seven rows, fewer in a short panel, so the activity log below 全部任务 stays in view.
    private void FitRunList()
    {
        // A hidden list keeps its last height, which would skew the sum.
        if (!RunResultAllScroll.IsVisible) {
            return;
        }
        const double sevenRows = 196, oneRow = 28, activityRoom = 100;
        var others = DesktopSection.ActualHeight + ScheduleSection.ActualHeight
            + RunResultSection.ActualHeight - RunResultAllScroll.ActualHeight;
        // A collapsed activity log needs no more than its header.
        var below = ActivitySectionBody.IsVisible ? activityRoom : ActivitySection.ActualHeight;
        RunResultAllScroll.MaxHeight = Math.Clamp(Sections.ActualHeight - others - below, oneRow, sevenRows);
    }

    // Outcomes that need attention name their task; the rest only count.
    private static IEnumerable<RunOutcome> Outcomes(RunProgressView run)
    {
        foreach (var (state, named) in new[] {
            (PlanItemProgress.Failed, true), (PlanItemProgress.Stopped, true), (PlanItemProgress.Running, true),
            (PlanItemProgress.Succeeded, false), (PlanItemProgress.Pending, false), (PlanItemProgress.NotRun, false)
        }) {
            var items = run.Items.Where(item => item.State == state).ToArray();
            if (named) {
                foreach (var item in items) {
                    yield return new(state, StateLabel(state), item.TaskLabel);
                }
            } else if (items.Length > 0) {
                yield return new(state, StateLabel(state), items.Length.ToString());
            }
        }
    }

    private static string StateLabel(PlanItemProgress state) => state switch {
        PlanItemProgress.Succeeded => "成功",
        PlanItemProgress.Running => "进行中",
        PlanItemProgress.Failed => "失败",
        PlanItemProgress.Stopped => "已停止",
        PlanItemProgress.Pending => "待执行",
        _ => "未执行"
    };

    private void RunResultAllToggle_Click(object sender, RoutedEventArgs e)
    {
        var expand = RunResultAllScroll.Visibility != Visibility.Visible;
        RunResultAllScroll.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        RunResultAllChevron.Symbol = expand
            ? Wpf.Ui.Controls.SymbolRegular.ChevronDown16 : Wpf.Ui.Controls.SymbolRegular.ChevronRight16;
        System.Windows.Automation.AutomationProperties.SetName(RunResultAllToggle,
            expand ? "收起全部任务" : "展开全部任务");
    }
}
