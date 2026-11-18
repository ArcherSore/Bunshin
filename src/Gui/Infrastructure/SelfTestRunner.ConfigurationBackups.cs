using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ProjectModel;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static byte[] ReadProtectedOriginal(string path) =>
        ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);

    private static void VerifyConfigurationBackups(string testDirectory, string projectDirectory)
    {
        var directory = Directory.CreateDirectory(Path.Combine(testDirectory, "backups")).FullName;
        var path = Path.Combine(directory, "configurations.json");
        var newest = path + ".bak";
        var older = newest + ".2";
        ProjectPlanModule Open() => ProjectPlanModule.Open(projectDirectory, path);
        var project = Open();
        var id = project.ActiveConfigurationId;
        File.Delete(newest);
        var unchanged = File.ReadAllBytes(path);
        using (var held = File.OpenRead(path)) {
            Require(Open().LoadWarning is null && File.Exists(newest), "已有主配置首次加载时应生成可用备份。");
        }
        var firstSnapshot = File.ReadAllBytes(newest);
        Require(Open().ActiveConfigurationId == id && File.ReadAllBytes(newest).SequenceEqual(firstSnapshot)
            && File.ReadAllBytes(path).SequenceEqual(unchanged) && !File.Exists(older),
            "再次打开同一配置不得覆盖可用备份、轮换旧备份或改写主配置。");
        project.RenameConfiguration(id, "older");
        project.RenameConfiguration(id, "latest");
        const string damage = "{ lost original";
        File.WriteAllText(path, damage);
        project = Open();
        Require(!project.ConfigurationsUnreadable && project.ActiveConfigurationId == id
            && project.Configurations.Single().Name == "latest"
            && project.LoadWarning!.Contains(Path.GetFileName(newest))
            && Open().LoadWarning is null
            && ReadProtectedOriginal(Directory.GetFiles(directory, "*.invalid-*.dpapi").Single())
                .SequenceEqual(Encoding.UTF8.GetBytes(damage)),
            "同项目最新备份应恢复配置及其身份，说明来源并写回，且损坏原件仍可检查。");

        var latestJson = File.ReadAllText(path);
        var future = JsonNode.Parse(latestJson)!;
        future["SchemaVersion"] = ConfigurationSet.CurrentSchemaVersion + 1;
        var futureBytes = Encoding.UTF8.GetBytes(future.ToJsonString());
        File.WriteAllBytes(path, futureBytes);
        Require(Throws<UnsupportedConfigurationVersionException>(() => Open())
            && File.ReadAllBytes(path).SequenceEqual(futureBytes),
            "较新版本的主文件必须提示升级；即使有本项目备份，也不得回退或覆盖主文件。");
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

        var upgraded = CreateProjectFixture(Path.Combine(testDirectory, "damaged-password-upgrade"));
        AddSecretAndPathInputs(upgraded);
        var saved = Path.Combine(upgraded, "before-upgrade.json");
        EditInterface(upgraded, root => root["option"]!["Login"]!["inputs"]![0]!["password"] = false);
        project = ProjectPlanModule.Open(upgraded, saved);
        project.SetInputValue("Login", "password", SecretInputValue);
        var damaged = JsonNode.Parse(File.ReadAllText(saved))!;
        damaged["Configurations"]![0]!["Name"] = null;
        var damagedBytes = Encoding.UTF8.GetBytes(damaged.ToJsonString());
        File.WriteAllBytes(saved, damagedBytes);
        EditInterface(upgraded, root => root["option"]!["Login"]!["inputs"]![0]!["password"] = true);
        var recovered = ProjectPlanModule.Open(upgraded, saved);
        var archive = Directory.GetFiles(upgraded, "before-upgrade.invalid-*.dpapi").Single();
        var login = recovered.GetConfiguration("RealTask").TaskOptions.Single(option => option.Name == "Login");
        Require(login.Inputs.Single().Value == SecretInputValue && !recovered.ConfigurationsUnreadable
            && ReadProtectedOriginal(archive).SequenceEqual(damagedBytes)
            && Directory.GetFiles(upgraded, "before-upgrade*").All(file =>
                File.ReadAllBytes(file).AsSpan().IndexOf(Encoding.UTF8.GetBytes(SecretInputValue)) < 0),
            "字段改为密码且主文件损坏时，应恢复密码并加密保留全部原始字节，配置和所有备份均不得暴露原文。");
    }
}
