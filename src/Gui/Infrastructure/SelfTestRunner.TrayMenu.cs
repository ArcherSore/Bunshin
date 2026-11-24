using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gui.Views;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyTrayMenu()
    {
        var app = (App)System.Windows.Application.Current;
        var entries = app.TrayMenuEntries();
        Require(entries.Select(entry => entry?.Header).SequenceEqual(
                ["显示主窗口", "显示子桌面", "结束桌面分身", null, "退出程序"]),
            "托盘菜单应依次为显示主窗口、显示子桌面、结束桌面分身、分隔线、退出程序。");
        Require(entries.Where(entry => entry is not null).Select(entry => entry!.Danger).SequenceEqual(
                [false, false, true, true]),
            "结束桌面分身和退出程序应使用危险色。");

        var invoked = new List<string>();
        var logged = new List<string>();
        var menu = TrayMenu.Build(
            [new("甲", () => invoked.Add("甲")), null, new("乙", () => invoked.Add("乙"), Danger: true)],
            logged.Add);
        Require(menu.Items.Count == 3 && menu.Items[1] is Separator, "分隔线应出现在对应位置。");
        var first = (MenuItem)menu.Items[0];
        var danger = (MenuItem)menu.Items[2];
        Require(!menu.StaysOpen, "点击菜单外部应关闭菜单。");

        var window = new Window { Content = new Border(), Width = 10, Height = 10, ShowInTaskbar = false };
        try {
            window.Show();
            menu.PlacementTarget = (UIElement)window.Content;
            menu.IsOpen = true;
            PumpUntil(() => menu.IsOpen && danger.IsLoaded, "托盘菜单应能打开。");
            var normalText = SettingsVisualDescendants(first).OfType<TextBlock>().Single();
            var dangerText = SettingsVisualDescendants(danger).OfType<TextBlock>().Single();
            Require(normalText.Foreground is SolidColorBrush normal
                    && dangerText.Foreground is SolidColorBrush red
                    && normal.Color == ((SolidColorBrush)app.Resources["Brush.Text.Primary"]).Color
                    && red.Color == ((SolidColorBrush)app.Resources["Brush.Error.Foreground"]).Color,
                "普通项和危险项实际显示的文字应使用主题里的对应颜色。");
            Require(normalText.FontSize == 14 && dangerText.FontSize == 14,
                "托盘菜单的普通项和危险项应与画板使用相同的 14 DIP 字号。");
            danger.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(invoked.SequenceEqual(["乙"]) && logged.SequenceEqual(["乙"]), "点击菜单项应执行对应动作并记录。");
        } finally {
            menu.IsOpen = false;
            window.Close();
        }
    }
}
