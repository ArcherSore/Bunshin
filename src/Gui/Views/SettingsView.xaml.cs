using System.Windows;
using System.Windows.Controls;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Gui.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    // A section landed on from the list keeps this gap above it, and counts as current once it passes the line.
    private const double SectionTopGap = 20;
    private const double CurrentSectionLine = 34;
    private const double ContentBottomMargin = 32;

    private bool _followingScroll;
    internal event EventHandler? DismissRequested;

    public SettingsView()
    {
        InitializeComponent();
    }

    internal void FocusDefault() => CloseSettingsButton.Focus();

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, EventArgs.Empty);

    private void SettingsSection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_followingScroll) {
            ScrollToSection(SectionNavigation.SelectedIndex);
        }
    }

    private void SelectedSection_Click(object sender, WpfMouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: true }) {
            ScrollToSection(SectionNavigation.SelectedIndex);
        }
    }

    private void ScrollToSection(int index)
    {
        if (index >= 0 && SectionAt(index) is { } section) {
            SectionScroller.ScrollToVerticalOffset(SectionScroller.VerticalOffset + TopOf(section) - SectionTopGap);
        }
    }

    private void SectionScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SectionScroller)) { return; }
        var count = SectionList.Items.Count;
        if (SectionAt(count - 1) is not { } last) { return; }
        // The last section stays tall enough to reach the top, so every list entry has a place to land.
        last.MinHeight = Math.Max(0, e.ViewportHeight - SectionTopGap - ContentBottomMargin);
        if (SectionNavigation.SelectedIndex is >= 0 and var selected) {
            if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0) {
                ScrollToSection(selected);
                return;
            }
            if (e.VerticalChange == 0) {
                return;
            }
        }
        var current = Enumerable.Range(0, count)
            .LastOrDefault(index => SectionAt(index) is { } section && TopOf(section) <= CurrentSectionLine);
        if (SectionNavigation.SelectedIndex != current) {
            _followingScroll = true;
            SectionNavigation.SelectedIndex = current;
            SectionNavigation.ScrollIntoView(SectionNavigation.SelectedItem);
            _followingScroll = false;
        }
    }

    private FrameworkElement? SectionAt(int index)
        => SectionList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

    private double TopOf(FrameworkElement section) => section.TranslatePoint(default, SectionScroller).Y;
}
