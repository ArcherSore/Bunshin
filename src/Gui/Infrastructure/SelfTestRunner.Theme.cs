using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Gui.Themes;
using Wpf.Ui.Appearance;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyThemeSwitch(AppLogger logger, string directory)
    {
        var app = (App)System.Windows.Application.Current;
        var light = app.Resources.MergedDictionaries.Single(dictionary => dictionary.Contains("Brush.Canvas"));
        var alternate = CreateTestPalette(light);
        var service = new ThemeService(app.Resources, new Dictionary<ApplicationTheme, ResourceDictionary> {
            [ApplicationTheme.Light] = light, [ApplicationTheme.Dark] = alternate
        });
        RunOnboardingScenario(logger, directory, (window, _) => {
            var button = new Button { Style = (Style)window.FindResource("PrimaryButtonStyle") };
            ((System.Windows.Controls.Panel)window.Content).Children.Add(button);
            int notifications = 0;
            service.ThemeChanged += (_, _) => notifications++;

            void Check(ResourceDictionary palette, ApplicationTheme mode)
            {
                Color Expected(string key) => ((SolidColorBrush)palette[key]).Color;
                window.UpdateLayout();
                var face = (Border)VisualTreeHelper.GetChild(button, 0);
                var primary = Expected("Brush.Primary");
                Require(face.Background is SolidColorBrush fill && fill.Color == primary
                    && window.Background is SolidColorBrush canvas && canvas.Color == Expected("Brush.Canvas"),
                    "已显示窗口的背景和公共按钮模板应呈现当前配色。");
                Require(ApplicationThemeManager.GetAppTheme() == mode
                    && Equals(app.FindResource("SystemAccentColor"), primary)
                    && Equals(app.FindResource("SystemAccentColorPrimary"), primary)
                    && Equals(app.FindResource("SystemAccentColorSecondary"), Expected("Brush.Primary.Hover"))
                    && Equals(app.FindResource("SystemAccentColorTertiary"), Expected("Brush.Primary.Pressed")),
                    "WPF-UI 明暗模式和四档强调色应与实际配色一致。");
            }

            try {
                Check(light, ApplicationTheme.Light);
                service.Apply(ApplicationTheme.Dark);
                Check(alternate, ApplicationTheme.Dark);
                service.Apply(ApplicationTheme.Light);
                Check(light, ApplicationTheme.Light);
                Require(notifications == 2, "每次应用配色后应通知订阅者。");
                app.Themes.Apply(ApplicationTheme.Dark);
                Check(light, ApplicationTheme.Light);
            } finally {
                service.Apply(ApplicationTheme.Light);
            }
        });
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
