using System.Windows;
using System.Windows.Media;
using Gui.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Application = System.Windows.Application;

namespace Gui.Themes;

internal sealed class ThemeService
{
    private readonly ResourceDictionary _resources;
    private readonly IReadOnlyDictionary<ApplicationTheme, ResourceDictionary> _palettes;
    private ResourceDictionary _activePalette;

    internal ThemeService(ResourceDictionary resources,
        IReadOnlyDictionary<ApplicationTheme, ResourceDictionary>? palettes = null)
    {
        _resources = resources;
        _activePalette = resources.MergedDictionaries.Single(dictionary => dictionary.Contains("Brush.Canvas"));
        _palettes = palettes ?? new Dictionary<ApplicationTheme, ResourceDictionary> {
            [ApplicationTheme.Light] = _activePalette
        };
    }

    internal event EventHandler? ThemeChanged;

    internal void Apply(ApplicationTheme requested)
    {
        var mode = _palettes.ContainsKey(requested) ? requested : ApplicationTheme.Light;
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
            if (window != app.MainWindow) WindowBackgroundManager.UpdateBackground(window, mode, WindowBackdropType.None);
            RestoreCanvas(window);
        }
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static void InitializeWindow(Window window)
    {
        WindowBackgroundManager.UpdateBackground(window, ApplicationThemeManager.GetAppTheme(), WindowBackdropType.None);
        RestoreCanvas(window);
    }

    private static void RestoreCanvas(Window window)
    {
        // WPF-UI removes the backdrop by assigning a local Background, including on HWND creation.
        if (window is MainWindow) window.SetResourceReference(Window.BackgroundProperty, "Brush.Canvas");
    }
}
