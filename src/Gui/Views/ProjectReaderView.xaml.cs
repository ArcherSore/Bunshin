using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfSymbol = Wpf.Ui.Controls.SymbolRegular;

namespace Gui.Views;

internal enum ProjectReadingKind { Welcome, Announcement, License }

public partial class ProjectReaderView : System.Windows.Controls.UserControl
{
    private static readonly MarkdownStyle ReadingStyle = new(FontSize: 14, LineHeight: 22.4, Heading1Size: 20,
        HeadingLineHeight: 28, BrushKey: "Brush.Text.Muted", StrongBrushKey: "Brush.Text.Primary",
        LinkBrushKey: "Brush.Primary.Foreground", ParagraphSpacing: 10, StrongWeight: FontWeights.Medium,
        CompactLists: true);

    public ProjectReaderView() => InitializeComponent();

    internal event EventHandler? DismissRequested;

    internal void SetContent(ProjectReadingKind kind, string title, IReadOnlyList<string> contents, Action<Uri> openLink)
    {
        ReaderTitle.Text = title;
        AutomationProperties.SetName(this, title);
        WelcomeIcon.Visibility = kind == ProjectReadingKind.Welcome ? Visibility.Visible : Visibility.Collapsed;
        ReaderSymbol.Visibility = kind == ProjectReadingKind.Welcome ? Visibility.Collapsed : Visibility.Visible;
        ReaderSymbol.Symbol = kind == ProjectReadingKind.License ? WpfSymbol.Document24 : WpfSymbol.Megaphone24;
        var document = MarkdownDocument.Create(contents, openLink, ReadingStyle);
        document.PagePadding = new Thickness(0, 0, 8, 10);
        ReaderViewer.Document = document;
    }

    internal void FocusReader()
    {
        ReaderCloseButton.Focus();
        ReaderViewer.ApplyTemplate();
        if (ReaderViewer.Template.FindName("PART_ContentHost", ReaderViewer) is ScrollViewer scroller) {
            scroller.ScrollToTop();
        }
        if (SystemParameters.ClientAreaAnimation) {
            var duration = TimeSpan.FromMilliseconds(200);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            ReaderDialog.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
            var scale = new DoubleAnimation(0.95, 1, duration) { EasingFunction = easing };
            ReaderScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            ReaderScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, EventArgs.Empty);

    private void Reader_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ReaderDialog.MaxHeight = Math.Max(1, e.NewSize.Height * 0.8);
}
