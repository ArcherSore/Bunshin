using System.Text;
using System.Text.Json.Nodes;
using ProjectModel;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyOptionKindMigration(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "kind-migration"));
        var path = Path.Combine(directory, "config.json");
        var project = ProjectPlanModule.Open(directory, path);
        project.SetSelectedCase("Mode", "Minimal");
        project.SetSelectedCase("Nested", "Off");
        EditInterface(directory, root => {
            root["option"]!["Mode"]!["type"] = "switch";
            root["option"]!["Nested"] = JsonNode.Parse("""
                {"type":"input","inputs":[{"name":"field","default":"new"}]}
                """);
        });
        var logs = new List<string>();
        project = ProjectPlanModule.Open(directory, path, log: logs.Add);
        var mode = project.GetConfiguration("RealTask").TaskOptions.Single(item => item.Name == "Mode");
        Require(!mode.IsExplicit && mode.SelectedCase == "Default" && mode.ActiveChildren.Single().Inputs[0].Value
            == "new" && project.Configurations.Single().ExplicitOptions.Count == 0 && project.LoadWarning is null,
            "同名选项类型发生变化时应清除原类型的意图，即使同名 case 仍然存在。");
        var values = project.CreateRunStartAttempt().Plan.Items.Single().ResolvedOptions;
        Require(values.GetProperty("Mode").GetString() == "Default"
            && values.GetProperty("Nested").GetProperty("field").GetString() == "new"
            && logs.Any(message => message.Contains("Mode") && message.Contains("Nested")),
            "类型迁移应记录变化，计划使用新定义的默认值。");
        logs.Clear();
        ProjectPlanModule.Open(directory, path, log: logs.Add);
        Require(logs.Count == 0, "重新打开已迁移的类型不应再次记录回退。");
    }

    private static void VerifyInputMigration(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "input-migration"));
        var path = Path.Combine(directory, "config.json");
        EditInterface(directory, root => {
            root["task"]![0]!["option"] = new JsonArray("Account");
            root["option"]!["Account"] = JsonNode.Parse("""
                {"type":"input","inputs":[{"name":"token","default":""},{"name":"region","default":"east"},
                  {"name":"old","default":""}]}
                """);
        });
        var project = ProjectPlanModule.Open(directory, path);
        var secret = Guid.NewGuid().ToString("N");
        project.SetInputValue("Account", "token", secret);
        project.SetInputValue("Account", "region", "east");
        project.SetInputValue("Account", "old", "old-value");
        EditInterface(directory, root => {
            var definitions = root["option"]!["Account"]!["inputs"]!.AsArray();
            definitions[0]!["password"] = true;
            definitions[0]!.AsObject().Remove("default");
            definitions[1]!["default"] = "west";
            definitions[2]!["name"] = "renamed";
            definitions[2]!["default"] = "fresh";
        });
        var logs = new List<string>();
        project = ProjectPlanModule.Open(directory, path, log: logs.Add);
        ProjectInputEditor[] Fields() => project.GetConfiguration("RealTask").TaskOptions.Single().Inputs.ToArray();
        var fields = Fields();
        var stored = project.Configurations.Single().ExplicitOptions["Account"].GetProperty("Inputs");
        var ciphertext = stored.GetProperty("token").GetString()!;
        Require(fields[0].IsPassword && fields[0].Value == secret && fields[1].IsExplicit && fields[1].Value == "east"
            && !fields[2].IsExplicit && fields[2].Value == "fresh" && !stored.TryGetProperty("old", out _)
            && ciphertext != secret && project.CreateRunStartAttempt().RedactionValues.Contains(secret),
            "新密码应保留可运行的原值，旧字段删除后同名兄弟字段的显式意图仍应保留。");
        Require(logs.Any(message => message.Contains("token") && message.Contains("old"))
            && logs.All(message => !message.Contains(secret) && !message.Contains("old-value"))
            && Directory.GetFiles(directory, "config.json*").All(file =>
                !Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(secret)),
            "字段迁移应记录项目和字段，主文件、备份与日志都不能暴露新密码。");

        EditInterface(directory, root => {
            var token = root["option"]!["Account"]!["inputs"]![0]!;
            token["password"] = false;
            token["default"] = "public";
        });
        var legacyPath = Path.Combine(directory, "legacy.json");
        var legacy = JsonNode.Parse(File.ReadAllText(path))!;
        var account = legacy["Configurations"]![0]!["ExplicitOptions"]!["Account"]!.AsObject();
        account.Remove("PasswordInputs");
        File.WriteAllText(legacyPath, legacy.ToJsonString());

        foreach (var inputPath in new[] { path, legacyPath }) {
            project = ProjectPlanModule.Open(directory, inputPath, log: logs.Add);
            fields = Fields();
            Require(fields[0] is { IsExplicit: false, IsPassword: false, Value: "public" }
                && fields[1].Value == "east" && project.CreateRunStartAttempt().RedactionValues.Count == 0
                && !File.ReadAllText(inputPath).Contains(ciphertext) && logs.All(line => !line.Contains(secret)),
                "普通输入必须丢弃原密码；旧格式的 DPAPI 密文也不能进入普通输入或日志。");
        }
    }
}
