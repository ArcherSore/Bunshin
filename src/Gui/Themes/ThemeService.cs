using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Application = System.Windows.Application;

namespace Gui.Themes;

internal enum AppThemeMode { Light, Dark, FollowSystem }

internal interface IThemeWindowBackground
{
    void RestoreBackground();
}

internal sealed class ThemeService
{
    private readonly ResourceDictionary _resources;
    private readonly IReadOnlyDictionary<ApplicationTheme, ResourceDictionary> _palettes;
    private readonly Func<ApplicationTheme> _readSystemTheme;
    private AppThemeMode _requestedMode = AppThemeMode.Light;
    private ResourceDictionary _activePalette;

    internal ThemeService(ResourceDictionary resources,
        IReadOnlyDictionary<ApplicationTheme, ResourceDictionary>? palettes = null,
        Func<ApplicationTheme>? readSystemTheme = null)
    {
        _resources = resources;
        _readSystemTheme = readSystemTheme ?? ReadSystemTheme;
        _activePalette = FindPalette(resources);
        _palettes = palettes ?? new Dictionary<ApplicationTheme, ResourceDictionary> {
            [ApplicationTheme.Light] = _activePalette,
            [ApplicationTheme.Dark] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Colors/Dark.xaml") }
        };
    }

    internal event EventHandler? ThemeChanged;

    internal static ResourceDictionary FindPalette(ResourceDictionary resources) =>
        resources.MergedDictionaries.Single(dictionary => dictionary.Contains("Brush.Canvas"));

    private static ApplicationTheme ReadSystemTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0 ? ApplicationTheme.Dark : ApplicationTheme.Light;

    internal nint SystemThemeHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int wmSysColorChange = 0x0015, wmSettingChange = 0x001A, wmThemeChanged = 0x031A,
            wmDwmColorizationColorChanged = 0x0320;
        if (_requestedMode == AppThemeMode.FollowSystem
            && (message is wmSysColorChange or wmThemeChanged or wmDwmColorizationColorChanged
                || message == wmSettingChange && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")) {
            Apply(AppThemeMode.FollowSystem);
        }
        return 0;
    }

    internal void Apply(AppThemeMode requested)
    {
        _requestedMode = requested;
        var resolved = requested switch {
            AppThemeMode.FollowSystem => _readSystemTheme(),
            AppThemeMode.Dark => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light
        };
        var mode = _palettes.ContainsKey(resolved) ? resolved : ApplicationTheme.Light;
        var palette = _palettes[mode];
        if (!ReferenceEquals(palette, _activePalette)) {
            var dictionaries = _resources.MergedDictionaries;
            dictionaries[dictionaries.IndexOf(_activePalette)] = palette;
            _activePalette = palette;
        }

        ApplicationThemeManager.Apply(mode, WindowBackdropType.None, updateAccent: false);
        var primary = ((SolidColorBrush)palette["Brush.Primary"]).Color;
        ApplicationAccentColorManager.Apply(primary, primary, ((SolidColorBrush)palette["Brush.Primary.Hover"]).Color,
            ((SolidColorBrush)palette["Brush.Primary.Pressed"]).Color);

        var app = Application.Current;
        foreach (Window window in app.Windows) {
            // The library has already updated MainWindow, but misses ownerless secondary windows.
            if (window != app.MainWindow)
                WindowBackgroundManager.UpdateBackground(window, mode, WindowBackdropType.None);
            (window as IThemeWindowBackground)?.RestoreBackground();
        }
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static void InitializeWindow(Window window)
    {
        WindowBackgroundManager.UpdateBackground(window, ApplicationThemeManager.GetAppTheme(),
            WindowBackdropType.None);
        (window as IThemeWindowBackground)?.RestoreBackground();
    }
}
