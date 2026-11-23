using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Gui.Themes;
using Gui.Views;
using Wpf.Ui.Appearance;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SendMessageW(nint hwnd, int message, nint wParam, string? lParam);

    private static void VerifyThemeSwitch(AppLogger logger, string directory)
    {
        var app = (App)System.Windows.Application.Current;
        var light = app.Resources.MergedDictionaries.Single(dictionary => dictionary.Contains("Brush.Canvas"));
        var alternate = CreateTestPalette(light);
        var systemTheme = ApplicationTheme.Dark;
        var service = new ThemeService(app.Resources, new Dictionary<ApplicationTheme, ResourceDictionary> {
            [ApplicationTheme.Light] = light, [ApplicationTheme.Dark] = alternate
        }, () => systemTheme);
        RunOnboardingScenario(logger, directory, (window, _) => {
            var button = new Button { Style = (Style)window.FindResource("PrimaryButtonStyle") };
            ((System.Windows.Controls.Panel)window.Content).Children.Add(button);
            var configurationBar = Named<Border>(window.TaskPlanEditor, "ConfigurationBar");
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
                Require(configurationBar.Background is SolidColorBrush bar
                    && bar.Color == Expected("Brush.Configuration.Bar"),
                    "任务计划编辑器中已显示的配置标签栏应跟随当前配色。");
                Require(window.Foreground is SolidColorBrush body && body.Color == Expected("Brush.Text.Body")
                    && window.UpdateNavigationBadge.Fill is SolidColorBrush badge
                    && badge.Color == Expected("Brush.Rail.UpdateBadge")
                    && window.UpdateDialog.Shadow?.Color == ((DropShadowEffect)palette["Effect.Dialog"]).Color,
                    "主窗口正文、更新提示点和对话框阴影应跟随配色切换。");
                Require(ApplicationThemeManager.GetAppTheme() == mode
                    && Equals(app.FindResource("SystemAccentColor"), primary)
                    && Equals(app.FindResource("SystemAccentColorPrimary"), primary)
                    && Equals(app.FindResource("SystemAccentColorSecondary"), Expected("Brush.Primary.Hover"))
                    && Equals(app.FindResource("SystemAccentColorTertiary"), Expected("Brush.Primary.Pressed")),
                    "WPF-UI 明暗模式和四档强调色应与实际配色一致。");
            }

            try {
                Check(light, ApplicationTheme.Light);
                var splitter = window.RuntimeSidebarSplitter;
                splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                var disc = (Ellipse)splitter.Template.FindName("DraggingDisc", splitter);
                var edge = (Border)splitter.Template.FindName("TetherStrong", splitter);
                PumpUntil(() => disc.Opacity == 1 && edge.Opacity == 1, "拖动时应显示分隔柄的按下色和渐变。");

                void CheckDragging(ResourceDictionary palette)
                {
                    var expectedEdge = (GradientBrush)palette["Brush.Splitter.Tether.Dragging"];
                    var expectedFill = (SolidColorBrush)palette["Brush.Primary.SurfaceHover"];
                    var expectedStroke = (SolidColorBrush)palette["Brush.Primary"];
                    Require(edge.Background is GradientBrush gradient && gradient.GradientStops.Select(stop => stop.Color)
                        .SequenceEqual(expectedEdge.GradientStops.Select(stop => stop.Color))
                        && disc.Opacity == 1 && disc.Fill is SolidColorBrush fill && fill.Color == expectedFill.Color
                        && disc.Stroke is SolidColorBrush stroke && stroke.Color == expectedStroke.Color
                        && ((UIElement)disc.Parent).Effect is DropShadowEffect shadow
                        && shadow.Color == (Color)palette["Color.Splitter.Shadow"],
                        "拖动中的分隔柄应持续显示当前配色的渐变、填充、描边和阴影。");
                }

                service.Apply(AppThemeMode.Dark);
                Check(alternate, ApplicationTheme.Dark);
                CheckDragging(alternate);
                service.Apply(AppThemeMode.Light);
                Check(light, ApplicationTheme.Light);
                CheckDragging(light);
                splitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Require(notifications == 2, "每次应用配色后应通知订阅者。");

                var hwnd = new WindowInteropHelper(window).Handle;
                const int wmSettingChange = 0x001A;
                const int wmThemeChanged = 0x031A;
                service.Apply(AppThemeMode.FollowSystem);
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
                foreach (var mode in Enum.GetValues<AppThemeMode>()) {
                    production.Apply(mode);
                    Check(light, ApplicationTheme.Light);
                }
            } finally {
                service.Apply(AppThemeMode.Light);
            }
        }, themeService: service, scheduleClock: new ScheduleTestClock());
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
        Require(shapes.Any(d => d.Geometry is RectangleGeometry && d.Brush is SolidColorBrush day && day.Color == category)
            && shapes.Any(d => d.Brush is SolidColorBrush track
                && track.Color == ((SolidColorBrush)palette["Brush.Timeline.Track"]).Color),
            "活动星期指示条和时间线轨道应使用当前配色。");
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
