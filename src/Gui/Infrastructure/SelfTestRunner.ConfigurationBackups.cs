using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ProjectModel;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyConfigurationBackups(string testDirectory, string projectDirectory)
    {
        var directory = Directory.CreateDirectory(Path.Combine(testDirectory, "backups")).FullName;
        var path = Path.Combine(directory, "configurations.json");
        var newest = path + ".bak";
        var older = newest + ".2";
        ProjectPlanModule Open() => ProjectPlanModule.Open(projectDirectory, path);
        var project = Open();
        var id = project.ActiveConfigurationId;
        project.RenameConfiguration(id, "older");
        project.RenameConfiguration(id, "latest");
        const string damage = "{ lost original";
        File.WriteAllText(path, damage);
        project = Open();
        Require(!project.ConfigurationsUnreadable && project.ActiveConfigurationId == id
            && project.Configurations.Single().Name == "latest"
            && project.LoadWarning!.Contains(Path.GetFileName(newest))
            && Open().LoadWarning is null && Directory.GetFiles(directory, "*.invalid-*.json")
                .Select(File.ReadAllText).SequenceEqual([damage]),
            "同项目最新备份应恢复配置及其身份，说明来源并写回，且损坏原件仍可检查。");

        var latestJson = File.ReadAllText(path);
        var secondBytes = File.ReadAllBytes(older);
        foreach (var change in new Action<JsonNode>[] {
            json => json["SchemaVersion"] = 99,
            json => json.AsObject().Remove("SchemaVersion"),
            json => json["ProjectName"] = "Unrelated project",
            json => json["ActiveConfigurationId"] = Guid.Empty,
            json => json["Configurations"]![0]!["ExplicitOptions"] = new JsonObject { ["Broken"] = new JsonObject() }
        }) {
            var invalid = JsonNode.Parse(latestJson)!;
            change(invalid);
            var bytes = Encoding.UTF8.GetBytes(invalid.ToJsonString());
            File.WriteAllBytes(newest, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            File.WriteAllBytes(older, secondBytes);
            File.WriteAllText(path, damage);
            project = Open();
            Require(!project.ConfigurationsUnreadable && project.ActiveConfigurationId == id
                && project.Configurations.Single().Name == "older"
                && project.LoadWarning!.Contains(Path.GetFileName(older))
                && Open().Configurations.Single().Name == "older",
                "备份身份、schema 或配置结构无效时应跳到下一份可用备份，并明确指出真实来源。");
        }

        using (var held = File.OpenRead(newest)) {
            project.RenameConfiguration(id, "editing");
            Require(project.Configurations.Single().Name == "editing" && project.LoadWarning is not null
                && Open().Configurations.Single().Name == "editing", "备份写入失败时仍应承认主配置已保存并提示备份失败。");
        }
        project.RenameConfiguration(id, "retry");
        File.WriteAllText(path, damage);
        using (var held = File.OpenRead(path)) {
            project = Open();
            Require(project.Configurations.Single().Name == "retry" && project.ConfigurationsUnreadable
                && project.LoadWarning!.Contains(Path.GetFileName(newest)) && project.LoadException is IOException
                && File.ReadAllText(path) == damage, "恢复写回失败应保留原件并告知来源及未保存状态。");
        }
        project = Open();
        Require(!project.ConfigurationsUnreadable && Open().Configurations.Single().Name == "retry",
            "解除文件占用后应允许重新恢复并写回。");

        File.Delete(path);
        project = Open();
        Require(project.ActiveConfigurationId == id && project.Configurations.Single().Name == "retry"
            && Open().LoadWarning is null, "主文件缺失时应优先恢复备份，避免创建首次默认任务。");
        File.WriteAllText(path, damage);
        File.WriteAllText(newest, "bad snapshot");
        File.WriteAllText(older, "bad snapshot");
        project = Open();
        Require(project.ConfigurationsUnreadable && project.SelectedTaskNames.Count == 0
            && project.LoadWarning is not null && File.ReadAllText(path) == damage, "两份备份均不可用时，保留损坏的主文件并提示用户处理。");
    }
}
