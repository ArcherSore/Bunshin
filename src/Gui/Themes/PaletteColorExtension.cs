using System.Windows;

namespace Gui.Themes;

// WPF's BAML optimization for a top-level StaticResource can assign its key to the preceding entry.
// A distinct markup extension preserves the alias as a separate dictionary entry.
internal sealed class PaletteColorExtension : StaticResourceExtension { }
