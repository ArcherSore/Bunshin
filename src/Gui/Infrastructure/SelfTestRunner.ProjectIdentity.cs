using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectModel;
using Gui.Views;
using System.Windows.Controls;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private const int FixtureIconSize = 40;

    private static void SetIdentity(string directory, JsonNode? title, JsonNode? icon, string? version = "1.0.0") =>
        EditInterface(directory, root => {
            root["title"] = title;
            root["icon"] = icon;
            if (version is null) { root.Remove("version"); } else { root["version"] = version; }
            root["label"] = "$项目显示名";
        });

    private static void WriteFixtureIcon(string path)
    {
        var bitmap = BitmapSource.Create(FixtureIconSize, FixtureIconSize, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat((byte)0x80, FixtureIconSize * FixtureIconSize * 4).ToArray(), FixtureIconSize * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void VerifyProjectIdentityContent(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "identity-content"));
        ProjectPlanModule Load() => ProjectPlanModule.Open(directory, Path.Combine(directory, "identity-config.json"));
        var source = File.ReadAllText(Path.Combine(directory, "interface.json"));
        foreach (var field in new[] { "title", "icon" }) {
            VerifyRejectedProjectInterface(testDirectory, source, $"identity-invalid-{field}",
                $"{field} 必须是 string 或 null", root => root[field] = 1);
        }

        SetIdentity(directory, null, null);
        Require(Load() is { Title: "SelfTestProject 1.0.0", About.Name: "项目显示名" } project
            && project.ResolveIconPath() is null,
            "未声明 title 时窗口标题为 name 与 version，不取 label；未声明 icon 时使用默认图标。");
        SetIdentity(directory, " ", "", version: null);
        Require(Load() is { Title: "SelfTestProject" } bare && bare.ResolveIconPath() is null,
            "空白 title/icon 视为未声明，没有 version 时标题只有 name。");
        SetIdentity(directory, "$app_title", "$assets/icon.png");
        Require(Load() is { Title: "app_title" } keyed && string.Equals(keyed.ResolveIconPath(),
            Path.GetFullPath(Path.Combine(directory, "assets", "icon.png")), StringComparison.OrdinalIgnoreCase),
            "翻译键显示为键名，icon 相对项目目录解析。");
        SetIdentity(directory, "自定义标题", "../outside.png");
        var outside = Load();
        Require(outside.Title == "自定义标题" && outside.Tasks.Count > 0, "越界的 icon 不能阻止项目加载。");
        try {
            _ = outside.ResolveIconPath();
            throw new InvalidOperationException("越界的 icon 必须被拒绝。");
        } catch (IOException) {
        }
    }

    private static void VerifyProjectIdentityWindow(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "identity-window"), (window, directory) => {
            var configurationTitle = Named<TextBlock>(window.Header, "ConfigurationTitle");
            var project = ProjectOf(window);
            var activeName = project.Configurations.Single(item => item.Id == project.ActiveConfigurationId).Name;
            Require(window.Title == "项目窗口标题" && configurationTitle.IsVisible && configurationTitle.Text == activeName,
                "窗口标题应显示 PI title，页眉应显示当前配置。");
            Require(window.Icon is BitmapSource { PixelWidth: FixtureIconSize }, "系统窗口须显示 PI icon。");

            var sidebarIcon = Named<System.Windows.Controls.Image>(window, "SidebarProjectIcon");
            Require(sidebarIcon.IsVisible && sidebarIcon.Source is BitmapSource { PixelWidth: FixtureIconSize },
                "展开的侧栏标题须显示 PI icon。");
            ClickOnboarding(window, "SidebarToggleButton");
            Require(!sidebarIcon.IsVisible, "收起侧栏时须隐藏标题图标。");
            ClickOnboarding(window, "SidebarToggleButton");

            ClickOnboarding(window, "UpdateNavigationItem");
            var updateIcon = Named<System.Windows.Controls.Image>(window.UpdateDialog, "UpdateProjectIcon");
            Require(updateIcon.IsVisible && updateIcon.Source is BitmapSource { PixelWidth: FixtureIconSize },
                "更新弹窗须显示 PI icon。");
            ClickOnboarding(window, "CloseUpdateButton");

            var settings = OpenAboutSection(window);
            System.Windows.Controls.Image AboutIcon() => SettingsVisualDescendants(settings)
                .OfType<System.Windows.Controls.Image>().Single(image => image.Name == "ProjectAboutIcon");
            PumpUntil(() => SettingsVisualDescendants(settings).OfType<System.Windows.Controls.TextBlock>()
                .Any(text => text is { Name: "ProjectAboutName", Text: "项目显示名" }), "关于页必须显示项目 label。");
            Require(AboutIcon().Source is BitmapSource { PixelWidth: FixtureIconSize }, "关于页必须显示 PI icon。");

            var identities = new List<ProjectIdentity>();
            window.ProjectIdentityChanged += (_, identity) => identities.Add(identity);
            File.WriteAllText(Path.Combine(directory, "assets", "icon.png"), "not an image");
            SetIdentity(directory, null, "assets/icon.png");
            InvokeOnboarding(window, "LoadProject");
            Require(window.Title == "SelfTestProject 1.0.0",
                "重新加载后系统窗口标题必须刷新为默认的 name 与 version。");
            Require(window.Icon is BitmapSource { PixelWidth: not FixtureIconSize }
                && identities.SingleOrDefault() is { Name: "SelfTestProject", IconPath: null },
                "无法加载的 icon 必须回到默认图标，托盘随之恢复。");
            Require(sidebarIcon.Source is BitmapSource { PixelWidth: not FixtureIconSize }
                && updateIcon.Source is BitmapSource { PixelWidth: not FixtureIconSize },
                "无法加载的 icon 必须使侧栏和更新弹窗恢复为应用图标。");
            PumpUntil(() => AboutIcon().Source is BitmapSource { PixelWidth: not FixtureIconSize },
                "关于页在 icon 无法加载时必须显示应用图标。");
        }, beforeLoad: directory => {
            WriteFixtureIcon(Path.Combine(directory, "assets", "icon.png"));
            SetIdentity(directory, "项目窗口标题", "./assets/icon.png");
        });
    }
}
