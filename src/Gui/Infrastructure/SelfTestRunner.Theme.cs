using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Gui.ChildSession;
using Gui.Themes;
using Gui.Views;
using Gui.Worker;
using Protocol;
using Wpf.Ui.Appearance;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SendMessageW(nint hwnd, int message, nint wParam, string? lParam);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    private static void VerifyThemeSwitch(AppLogger logger, string directory)
    {
        var app = (App)System.Windows.Application.Current;
        var light = ThemeService.FindPalette(app.Resources);
        var alternate = CreateTestPalette(light);
        var systemTheme = ApplicationTheme.Dark;
        var service = new ThemeService(app.Resources, new Dictionary<ApplicationTheme, ResourceDictionary> {
            [ApplicationTheme.Light] = light, [ApplicationTheme.Dark] = alternate
        }, () => systemTheme);
        RunOnboardingScenario(logger, directory, (window, _) => {
            var button = new Button { Style = (Style)window.FindResource("PrimaryButtonStyle") };
            ((System.Windows.Controls.Panel)window.Content).Children.Add(button);
            var sidebarSurface = Named<Border>(window.Sidebar, "Frame");
            var project = ProjectOf(window);
            project.AddSchedulePolicy(project.ActiveConfigurationId, "早班");
            ClickOnboarding(window, "ScheduleNavigationItem");
            var schedule = window.ScheduleView;
            Require(schedule.Timeline.Pins.Count > 0, "切换前应已显示定时标记。");
            int notifications = 0;
            service.ThemeChanged += (_, _) => notifications++;

            void Check(ResourceDictionary palette, ApplicationTheme mode)
            {
                Color Expected(string key) => ((SolidColorBrush)palette[key]).Color;
                window.UpdateLayout();
                CheckScheduleColors(schedule, palette);
                var face = (Border)VisualTreeHelper.GetChild(button, 0);
                var primary = Expected("Brush.Primary");
                Require(face.Background is SolidColorBrush fill && fill.Color == primary
                    && window.Background is SolidColorBrush canvas && canvas.Color == Expected("Brush.Canvas"),
                    "已显示窗口的背景和公共按钮模板应呈现当前配色。");
                Require(sidebarSurface.Background is SolidColorBrush rail
                    && rail.Color == Expected("Brush.Surface.Subdued"),
                    "包含任务配置的侧栏背景应跟随当前配色。");
                Require(window.Foreground is SolidColorBrush body && body.Color == Expected("Brush.Text.Body")
                    && Named<System.Windows.Shapes.Ellipse>(window, "UpdateNavigationBadge").Fill is SolidColorBrush badge
                    && badge.Color == Expected("Brush.Rail.UpdateBadge")
                    && window.UpdateDialog.Card.Shadow?.Color == ((DropShadowEffect)palette["Effect.Dialog.Update"]).Color,
                    "主窗口正文、更新提示点和对话框阴影应跟随配色切换。");
                RequireThemeBrush(window.RuntimeSidebarSplitter.BorderBrush, palette, "Brush.Rail.Border", "运行面板边缘");
                Require(ApplicationThemeManager.GetAppTheme() == mode
                    && Equals(app.FindResource("SystemAccentColor"), primary)
                    && Equals(app.FindResource("SystemAccentColorPrimary"), primary)
                    && Equals(app.FindResource("SystemAccentColorSecondary"), Expected("Brush.Primary.Hover"))
                    && Equals(app.FindResource("SystemAccentColorTertiary"), Expected("Brush.Primary.Pressed")),
                    "WPF-UI 明暗模式和四档强调色应与实际配色一致。");
            }

            RdpPreviewWindow? preview = null;
            try {
                Check(light, ApplicationTheme.Light);
                service.Apply(AppThemeMode.Dark);
                preview = new RdpPreviewWindow();
                preview.ShowOffscreen();
                PumpUntil(() => preview.IsLoaded, "预览窗口应已加载标题栏。");
                CheckPreviewColors(preview, alternate, dark: true);
                Check(alternate, ApplicationTheme.Dark);
                service.Apply(AppThemeMode.Light);
                CheckPreviewColors(preview, light, dark: false);
                Check(light, ApplicationTheme.Light);
                Require(notifications == 2, "每次应用配色后应通知订阅者。");

                var hwnd = new WindowInteropHelper(window).Handle;
                const int wmSettingChange = 0x001A;
                const int wmThemeChanged = 0x031A;
                service.Apply(AppThemeMode.FollowSystem);
                CheckPreviewColors(preview, alternate, dark: true);
                Check(alternate, ApplicationTheme.Dark);
                systemTheme = ApplicationTheme.Light;
                SendMessageW(hwnd, wmThemeChanged, 0, null);
                Check(light, ApplicationTheme.Light);
                systemTheme = ApplicationTheme.Dark;
                SendMessageW(hwnd, wmSettingChange, 0, "ImmersiveColorSet");
                Check(alternate, ApplicationTheme.Dark);

                service.Apply(AppThemeMode.Light);
                var beforeMessage = notifications;
                SendMessageW(hwnd, wmSettingChange, 0, "ImmersiveColorSet");
                Check(light, ApplicationTheme.Light);
                Require(notifications == beforeMessage, "固定浅色时，系统主题消息不应重新应用配色。");

                var production = new ThemeService(app.Resources, readSystemTheme: () => systemTheme);
                // The shipped dark palette, not the generated one, must be what a real service switches to.
                var shipped = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Colors/Dark.xaml") };
                Require(shipped.Keys.Cast<object>().ToHashSet().SetEquals(light.Keys.Cast<object>()),
                    "深色与浅色配色字典的键集应完全一致。");
                foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.FollowSystem, AppThemeMode.Light }) {
                    production.Apply(mode);
                    var dark = mode != AppThemeMode.Light && systemTheme == ApplicationTheme.Dark
                        || mode == AppThemeMode.Dark;
                    Check(dark ? shipped : light, dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
                }
                ((System.Windows.Controls.Panel)window.Content).Children.Remove(button);
                CheckPageThemeSwitch(window, service, light, alternate);
            } finally {
                preview?.Close();
                service.Apply(AppThemeMode.Light);
            }
        }, beforeLoad: path => SetAbout(path, "项目简介", "联系作者", null),
            themeService: service, scheduleClock: new ScheduleTestClock());
    }

    private static void CheckPageThemeSwitch(MainWindow window, ThemeService service,
        ResourceDictionary light, ResourceDictionary alternate)
    {
        void RoundTrip(Action<ResourceDictionary> check)
        {
            service.Apply(AppThemeMode.Dark);
            window.UpdateLayout();
            check(alternate);
            service.Apply(AppThemeMode.Light);
            window.UpdateLayout();
            check(light);
        }

        ClickHome(window);
        var worker = ScenarioWorker("theme", active: RunState.Running);
        SetOnboardingField(window, "_workerSnapshot", new WorkerCoordinatorSnapshot(WorkerObservation.Connected,
            true, worker, ""));
        InvokeOnboarding(window, "RefreshRuntimeControls");
        window.UpdateLayout();
        RoundTrip(palette => {
            Require(window.RuntimeSidebar.RunProgressCaption.IsVisible, "首页应已显示运行摘要。");
            RequireThemeBrush(window.RuntimeSidebar.RunProgressCaption.Foreground, palette, "Brush.Text.Muted", "运行中摘要文字");
            RequireThemeBrush(window.RuntimeSidebar.RunProgressBar.Background, palette, "Brush.Progress.Track", "运行进度条轨道");
        });

        ClickOnboarding(window, "HelpNavigationItem");
        RoundTrip(palette => {
            var summary = SettingsVisualDescendants(window.HelpView).OfType<TextBlock>()
                .First(text => text.Name == "Summary" && text.IsVisible);
            RequireThemeBrush(summary.Foreground, palette, "Brush.Text.Secondary", "帮助页问题摘要");
        });

        var settings = OpenAboutSection(window);
        window.UpdateLayout();
        var navigation = settings.SectionNavigation;
        var about = SettingsVisualDescendants(settings).OfType<ProjectAboutView>().Single();
        PumpUntil(() => about.TranslatePoint(default, settings).Y is >= 0 and var top && top < settings.ActualHeight,
            "设置页应已滚动到关于项目。");
        RoundTrip(palette => {
            var selected = (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromItem(navigation.SelectedItem);
            var description = (FlowDocumentScrollViewer)about.Description.Content;
            Require(description.IsVisible, "关于页应已显示项目简介。");
            RequireThemeBrush(selected.Background, palette, "Brush.Primary", "设置页选中的目录项");
            RequireThemeBrush(description.Document.Foreground, palette, "Brush.Text.Secondary", "关于页项目简介");
        });
    }

    private static void CheckPreviewColors(RdpPreviewWindow preview, ResourceDictionary palette, bool dark)
    {
        RequireThemeBrush(preview.ViewerTitleBar.Background, palette, "Brush.Canvas", "预览窗口标题栏");
        RequireThemeBrush(preview.StatusDot.Fill, palette, "Brush.Primary", "预览窗口连接状态点");
        // DWMWA_USE_IMMERSIVE_DARK_MODE is publicly supported starting with Windows 11.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) {
            const int immersiveDarkMode = 20;
            int result = DwmGetWindowAttribute(new WindowInteropHelper(preview).Handle, immersiveDarkMode,
                out var value, sizeof(int));
            Require(result == 0 && (value != 0) == dark,
                $"预览窗口原生标题栏深色状态应为 {dark}，实际值为 {value} (HRESULT 0x{result:X8})。");
        }
    }

    private static void RequireThemeBrush(System.Windows.Media.Brush actual, ResourceDictionary palette,
        string key, string element)
    {
        var expected = ((SolidColorBrush)palette[key]).Color;
        Require(actual is SolidColorBrush brush && brush.Color == expected,
            $"{element} 应呈现 {key} 的颜色 {expected}，实际为 {actual}。");
    }

    private static void RequireReadableText(TextBlock text, Border card)
    {
        var foreground = ((SolidColorBrush)text.Foreground).Color;
        var background = ((SolidColorBrush)card.Background).Color;
        var backgroundLight = Luminance(background);
        var textLight = foreground.ScA * Luminance(foreground) + (1 - foreground.ScA) * backgroundLight;
        var contrast = (Math.Max(textLight, backgroundLight) + 0.05) / (Math.Min(textLight, backgroundLight) + 0.05);
        Require(contrast >= 4.5, $"{text.Name} 的文字对比度应至少为 4.5，实际为 {contrast:F2}。");

        static double Luminance(Color color) => 0.2126 * color.ScR + 0.7152 * color.ScG + 0.0722 * color.ScB;
    }

    private static void CheckScheduleColors(ScheduleView page, ResourceDictionary palette)
    {
        var category = ((SolidColorBrush)palette["Brush.Chart.Category1"]).Color;
        var drawing = VisualTreeHelper.GetDrawing(page.Timeline);
        var shapes = Shapes(drawing).ToArray();
        var pin = shapes.First(d => d.Geometry is EllipseGeometry && d.Brush is SolidColorBrush);
        var swatch = ConfigurationDescendants(page.ConfigurationList).OfType<System.Windows.Shapes.Rectangle>().First();
        Require(pin.Brush is SolidColorBrush point && point.Color == category
            && page.KeyCurrent.Fill is SolidColorBrush legend && legend.Color == category
            && swatch.Fill is SolidColorBrush fill && fill.Color == category,
            "已有时间线标记、图例和配置列表应同时显示当前配置色。");
        Require(shapes.Any(d => d.Geometry is RectangleGeometry && d.Brush is SolidColorBrush day
                && day.Color == category)
            && shapes.Any(d => d.Brush is SolidColorBrush track
                && track.Color == ((SolidColorBrush)palette["Brush.Timeline.Track"]).Color),
            "活动星期指示条和时间线轨道应使用当前配色。");
        Require(shapes.Any(d => d.Geometry is LineGeometry && d.Pen?.Brush is SolidColorBrush marker
            && marker.Color == ((SolidColorBrush)palette["Brush.Timeline.Now"]).Color),
            "时间线的当前时刻指示线应使用当前配色。");
        var expected = (LinearGradientBrush)palette["Brush.Schedule.NextRun"];
        Require(page.NextRunBanner.Background is LinearGradientBrush banner
            && banner.GradientStops.Select(s => s.Color).SequenceEqual(expected.GradientStops.Select(s => s.Color)),
            "已有下次运行横幅的渐变应随配色切换。");
        var chip = page.TimeChips.Children.OfType<Border>().First();
        Require(chip.Background is SolidColorBrush tint
            && tint.Color == Color.FromArgb(0x1A, category.R, category.G, category.B),
            "已有时间标签的半透明背景应从当前配置色派生。");

        static IEnumerable<GeometryDrawing> Shapes(DrawingGroup group)
        {
            foreach (var child in group.Children) {
                if (child is GeometryDrawing shape) yield return shape;
                if (child is DrawingGroup nested) {
                    foreach (var item in Shapes(nested)) yield return item;
                }
            }
        }
    }

    private static ResourceDictionary CreateTestPalette(ResourceDictionary light)
    {
        var alternate = new ResourceDictionary();
        byte index = 0;
        foreach (var key in light.Keys.Cast<string>().Order()) {
            var color = Color.FromRgb(0x52, ++index, 0x81);
            alternate[key] = light[key] switch {
                Color => color,
                SolidColorBrush => new SolidColorBrush(color),
                GradientBrush gradient => RecolorGradient(gradient, color),
                DropShadowEffect shadow => new DropShadowEffect { Color = color, BlurRadius = shadow.BlurRadius,
                    Direction = shadow.Direction, Opacity = shadow.Opacity, ShadowDepth = shadow.ShadowDepth },
                _ => throw new InvalidOperationException($"测试配色不支持资源 {key} 的类型。")
            };
        }
        return alternate;
    }

    private static GradientBrush RecolorGradient(GradientBrush gradient, Color color)
    {
        var copy = gradient.Clone();
        foreach (var stop in copy.GradientStops) stop.Color = color;
        return copy;
    }
}
