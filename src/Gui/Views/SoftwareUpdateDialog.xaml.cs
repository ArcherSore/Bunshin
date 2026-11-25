using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Updates;

namespace Gui.Views;

internal enum UpdateDialogState { Idle, Checking, Available, Latest, CheckFailed, Preparing, Ready, Installing }

public partial class SoftwareUpdateDialog : System.Windows.Controls.UserControl
{
    private static readonly MarkdownStyle NotesStyle = new(14, 22, 18, 22, "Brush.Text.Muted", "Brush.Text.Primary",
        ParagraphSpacing: 5, ShowLinkToolTips: false);

    public SoftwareUpdateDialog() => InitializeComponent();

    internal void Present(
        UpdateDialogState state, string currentVersion, string? targetVersion, string checkStatus, string operationStatus,
        bool exiting)
    {
        var checking = state == UpdateDialogState.Checking;
        var available = state == UpdateDialogState.Available;
        var preparing = state == UpdateDialogState.Preparing;
        var ready = state == UpdateDialogState.Ready;
        var installing = state == UpdateDialogState.Installing;

        Width = available ? 440 : 400;
        CheckingPanel.Visibility = VisibleWhen(checking);
        VersionPanel.Visibility = VisibleWhen(!checking);
        AvailableVersion.Visibility = VisibleWhen(available);
        VersionSummary.Visibility = VisibleWhen(!available);
        if (!preparing) {
            StateTitle.Text = state switch {
                UpdateDialogState.Available => "发现新版本",
                UpdateDialogState.Latest => "已是最新版本",
                UpdateDialogState.CheckFailed => "检查更新失败",
                UpdateDialogState.Ready => "更新已就绪",
                UpdateDialogState.Installing => "正在安装更新…",
                _ => "检查软件更新"
            };
            VersionSummary.Text = $"版本 {(ready || installing ? targetVersion : currentVersion)}";
        }
        TargetVersion.Text = targetVersion ?? "";
        InstalledVersion.Text = $"当前 {currentVersion}";

        ResultMessage.Text = state switch {
            UpdateDialogState.Idle => "查看是否有可用的新版本。",
            UpdateDialogState.Latest => "当前已是最新版本，感谢使用！",
            UpdateDialogState.CheckFailed => checkStatus,
            UpdateDialogState.Ready => "安装将关闭桌面分身并重启，配置会保留。",
            UpdateDialogState.Installing => "安装完成后，程序将自动重启。",
            _ => ""
        };
        ResultMessage.Visibility = VisibleWhen(ResultMessage.Text.Length > 0);
        NotesPanel.Visibility = VisibleWhen(available);
        ProgressBar.Visibility = VisibleWhen(preparing);
        UpdateDownloadStatus.Text = operationStatus;
        UpdateDownloadStatus.Visibility = VisibleWhen(!preparing && operationStatus.Length > 0);

        CloseUpdateButton.IsEnabled = CheckingCloseButton.IsEnabled = !exiting;
        CheckingCloseButton.Visibility = VisibleWhen(checking);
        DialogCheckUpdateButton.Visibility = VisibleWhen(state is UpdateDialogState.Idle or UpdateDialogState.Latest
            or UpdateDialogState.CheckFailed);
        DialogCheckUpdateButton.Content = state == UpdateDialogState.CheckFailed ? "重试" : "检查更新";
        DialogCheckUpdateButton.IsEnabled = !exiting;
        ReleaseNotesButton.Visibility = DownloadUpdateButton.Visibility = VisibleWhen(available);
        DownloadUpdateButton.IsEnabled = !exiting;
        LaterUpdateButton.Visibility = VisibleWhen(available || ready || installing);
        LaterUpdateButton.IsEnabled = !exiting && !installing;
        CancelDownloadButton.Visibility = VisibleWhen(preparing);
        CancelDownloadButton.IsEnabled = !exiting;
        InstallUpdateButton.Visibility = VisibleWhen(preparing || ready || installing);
        InstallUpdateButton.IsEnabled = ready && !exiting;
    }

    internal void StartProgress(string version)
    {
        StateTitle.Text = $"正在准备 {version}";
        VersionSummary.Text = "正在准备更新…";
        ProgressBar.IsIndeterminate = true;
        ProgressBar.Value = 0;
    }

    internal void ShowProgress(string version, EngineProgress progress)
    {
        var downloading = progress.Phase == "download";
        StateTitle.Text = downloading ? $"正在下载 {version}" : $"正在校验 {version}";
        VersionSummary.Text = downloading
            ? $"已下载 {progress.Bytes / 1048576.0:F1} / {progress.Total / 1048576.0:F1} MB"
                + $" · {progress.BytesPerSecond / 1048576.0:F1} MB/s"
            : "正在校验并解压完整包…";
        ProgressBar.IsIndeterminate = !downloading || progress.Total <= 0;
        ProgressBar.Value = progress.Total > 0 ? 100.0 * progress.Bytes / progress.Total : 0;
    }

    internal void SetNotes(string notes, Action<Uri> openLink)
    {
        var document = MarkdownDocument.Create([notes], openLink, NotesStyle);
        document.PagePadding = new Thickness(0);
        if (document.Blocks.LastBlock is List list) {
            list.Margin = new Thickness(0);
        }
        NotesViewer.Document = document;
    }

    internal void FocusNotes()
    {
        NotesPanel.BringIntoView();
        NotesViewer.Focus();
    }

    private static Visibility VisibleWhen(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
}
