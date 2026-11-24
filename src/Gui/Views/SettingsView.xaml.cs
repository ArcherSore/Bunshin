using System.Windows;
using System.Windows.Controls;

namespace Gui.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    // A section landed on from the list keeps this gap above it, and counts as current once it passes the line.
    private const double SectionTopGap = 40;
    private const double CurrentSectionLine = 60;
    private const double ContentBottomMargin = 80;

    internal event EventHandler<int>? CurrentSectionChanged;

    public SettingsView()
    {
        InitializeComponent();
    }

    internal void ScrollToSection(int index)
    {
        if (SectionAt(index) is not { } section) { return; }
        SectionScroller.ScrollToVerticalOffset(SectionScroller.VerticalOffset + TopOf(section) - SectionTopGap);
    }

    private void SectionScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SectionScroller)) { return; }
        var count = SectionList.Items.Count;
        if (SectionAt(count - 1) is not { } last) { return; }
        // The last section stays tall enough to reach the top, so every list entry has a place to land.
        last.MinHeight = Math.Max(0, e.ViewportHeight - SectionTopGap - ContentBottomMargin);
        var current = Enumerable.Range(0, count)
            .LastOrDefault(index => SectionAt(index) is { } section && TopOf(section) <= CurrentSectionLine);
        CurrentSectionChanged?.Invoke(this, current);
    }

    private FrameworkElement? SectionAt(int index)
        => SectionList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

    private double TopOf(FrameworkElement section) => section.TranslatePoint(default, SectionScroller).Y;
}
