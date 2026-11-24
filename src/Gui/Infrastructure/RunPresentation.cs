using Gui.ChildSession;

namespace Gui.Infrastructure;

internal sealed record RunTerminalNotification(string Title, string Message, bool Failed, string? Operation = null, Exception? Error = null);

internal sealed class RunPresentation
{
    internal Func<bool> RequiresWindowsHello { get; init; } = ChildSessionEnvironment.RequiresWindowsHelloSignIn;
    internal Action<string, Exception, string>? ShowError { get; init; }
    internal Func<RunTerminalNotification, bool>? ShowBalloon { get; init; }
}
