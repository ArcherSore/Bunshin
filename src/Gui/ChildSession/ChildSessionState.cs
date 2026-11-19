namespace Gui.ChildSession;

internal enum ChildSessionState
{
    NotRunning,
    Existing,
    Connecting,
    Connected,
    Disconnecting,
    Faulted
}

// PreviewVisible tracks the connected viewer; the login window has its own visibility handling.
// InputUnlocked is true only while the viewer is shown and lets mouse and keyboard reach the Child Session.
internal sealed record ChildSessionSnapshot(
    ChildSessionState State, uint? ChildSessionId, string Detail,
    bool PreviewVisible = false, bool InputUnlocked = false)
{
    internal static ChildSessionSnapshot Empty { get; } =
        new(ChildSessionState.NotRunning, null, "未运行");
}
