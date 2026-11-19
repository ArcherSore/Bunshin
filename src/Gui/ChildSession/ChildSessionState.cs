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

// PreviewVisible is true only while Connected and the viewer is shown.
// InputUnlocked is true only while the viewer is shown and lets mouse and keyboard reach the Child Session.
internal sealed record ChildSessionSnapshot(
    ChildSessionState State, uint? ChildSessionId, int RdpConnectedState, string Detail,
    bool PreviewVisible = false, bool InputUnlocked = false)
{
    internal static ChildSessionSnapshot Empty { get; } =
        new(ChildSessionState.NotRunning, null, 0, "未运行");
}
