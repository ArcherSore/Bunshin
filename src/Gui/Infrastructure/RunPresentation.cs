using System.Windows;
using Gui.ChildSession;

namespace Gui.Infrastructure;

internal sealed record RunTerminalNotification(string Title, string Message, bool Failed, string? Operation = null, Exception? Error = null);

internal sealed class RunPresentation
{
    internal Func<bool> RequiresWindowsHello { get; init; } = ChildSessionEnvironment.RequiresWindowsHelloSignIn;
    internal Func<Window?, string, Exception, string, MessageBoxButton, MessageBoxResult> ShowError { get; init; }
        = (owner, title, _, message, buttons) => owner != null
            ? System.Windows.MessageBox.Show(owner, message, title, buttons, MessageBoxImage.Error)
            : System.Windows.MessageBox.Show(message, title, buttons, MessageBoxImage.Error,
                buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.OK);
    internal Func<RunTerminalNotification, bool>? ShowBalloon { get; init; }
}
