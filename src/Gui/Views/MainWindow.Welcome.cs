using System.IO;
using System.Windows;
using System.Windows.Input;
using ProjectModel;

namespace Gui.Views;

public partial class MainWindow
{
    private IInputElement? _readerPreviousFocus;
    private string? _welcomeHashToRecord;

    private string WelcomeHashPath => Path.Combine(_applicationDirectory, "config", "welcome-shown.txt");

    private ProjectWelcome? ResolveWelcome() => _projectPlan?.ResolveWelcome(message => _logger.Warn(message));

    private void ShowWelcomeIfChanged()
    {
        // Nobody is there to dismiss it after a launch by the system, and it would be recorded as read.
        if (!_launchCommand.AutoStart && ResolveWelcome() is { } welcome && welcome.Hash != ReadWelcomeHash()) {
            OpenReader(ProjectReadingKind.Welcome, $"欢迎使用 {welcome.Title}", welcome.Contents, welcome.Hash);
        }
    }

    // Viewing on request always shows the announcements and leaves what was last acknowledged untouched.
    private void ViewWelcome()
    {
        if (ResolveWelcome() is { } welcome) {
            OpenReader(ProjectReadingKind.Announcement, $"{welcome.Title} · 公告", welcome.Contents);
        }
    }

    private void ReadLicense(string license) => OpenReader(ProjectReadingKind.License, "许可证", [license]);

    private void OpenReader(ProjectReadingKind kind, string title, IReadOnlyList<string> contents, string? hashToRecord = null)
    {
        if (IsGlobalModalOpen || _exitInProgress || _onboardingActive) {
            return;
        }
        _welcomeHashToRecord = hashToRecord;
        _readerPreviousFocus = Keyboard.FocusedElement;
        ProjectReader.SetContent(kind, title, contents, OpenTaskDescriptionLink);
        ProjectReader.Visibility = Visibility.Visible;
        EnsureModalWindowHook();
        ProjectReader.FocusReader();
    }

    private void CloseProjectReader_Requested(object? sender, EventArgs e) => CloseProjectReader();

    private void CloseProjectReader()
    {
        if (_exitInProgress || ProjectReader.Visibility != Visibility.Visible) {
            return;
        }
        ProjectReader.Visibility = Visibility.Collapsed;
        RemoveModalWindowHook();
        if (_welcomeHashToRecord is not null) {
            WriteWelcomeHash(_welcomeHashToRecord);
            _welcomeHashToRecord = null;
        }
        if (_readerPreviousFocus is not null) {
            Keyboard.Focus(_readerPreviousFocus);
            _readerPreviousFocus = null;
        }
        ReevaluateOnboarding();
    }

    private string? ReadWelcomeHash()
    {
        try {
            return File.Exists(WelcomeHashPath) ? File.ReadAllText(WelcomeHashPath).Trim() : null;
        } catch (Exception exception) {
            _logger.Warn("读取公告已读记录失败。", exception);
            return null;
        }
    }

    private void WriteWelcomeHash(string hash)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(WelcomeHashPath)!);
            File.WriteAllText(WelcomeHashPath, hash);
        } catch (Exception exception) {
            _logger.Warn("保存公告已读记录失败。", exception);
        }
    }
}
