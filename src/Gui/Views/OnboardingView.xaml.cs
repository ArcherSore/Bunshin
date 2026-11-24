using System.Windows;
using System.Windows.Automation;
using System.Windows.Shapes;

namespace Gui.Views;

public partial class OnboardingView : System.Windows.Controls.UserControl
{
    internal const double CardWidth = 300;
    internal const int StepCount = 4;

    public OnboardingView() => InitializeComponent();

    internal void ShowStep(int index, string title, string description)
    {
        OnboardingCounter.Text = $"新手指引 · {index + 1} / {StepCount}";
        AutomationProperties.SetName(OnboardingCounter, $"新手指引，第 {index + 1} 步，共 {StepCount} 步");
        AutomationProperties.SetName(OnboardingPopover, title);
        OnboardingTitle.Text = title;
        OnboardingDescription.Text = description;
        OnboardingPrevious.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        OnboardingNext.Content = index == StepCount - 1 ? "开始使用" : "下一步";
        AutomationProperties.SetName(OnboardingNext, (string)OnboardingNext.Content);
        for (var dot = 0; dot < ProgressDots.Children.Count; dot++) {
            ((Ellipse)ProgressDots.Children[dot]).Tag = dot == index ? "Current" : null;
        }
    }

    private void BlockDrop(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }
}
