using System.IO;
using System.Text.Json.Nodes;
using ProjectModel;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyProjectImports(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "imports"));
        ProjectPlanModule Open() => ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        EditInterface(directory, root => {
            root["import"] = new JsonArray("parts/z.json", "parts/a.json");
            root["pretask"] = JsonNode.Parse("""{"exec":"main"}""");
            root["group"] = JsonNode.Parse("""[{"name":"shared","label":"Main group"}]""");
        });
        var parts = Directory.CreateDirectory(Path.Combine(directory, "parts")).FullName;
        File.WriteAllText(Path.Combine(parts, "z.json"), """
            {
              "task":[{"name":"First","entry":"First","option":["Later"]}],
              "option":{"Mode":{"type":"input","inputs":[{"name":"obsolete","default":"unused"}]}},
              "preset":[{"name":"First preset","task":[{"name":"Second"}]}],
              "group":[{"name":"shared"},{"name":"extra"}],
              "pretask":[{"exec":"first"},{"exec":"second"}],
              "global_option":["Later","ServerRange"],
              "setting":[{"name":"First section","option":["Later"]}]
            }
            """);
        var last = Path.Combine(parts, "a.json");
        File.WriteAllText(last, """
            {
              "task":[{"name":"Second","entry":"Second"}],
              "option":{"Later":{"cases":[{"name":"ok"}],"default_case":"ok"},
                        "Mode":{"cases":[{"name":"final"}],"default_case":"final"}},
              "preset":[{"name":"Second preset"}],
              "group":[{"name":"extra"},{"name":"last"}],
              "pretask":{"exec":"last"},
              "global_option":["Mode","Later"],
              "setting":[{"name":"Last section"}]
            }
            """);
        var project = Open();
        Require(project.Tasks.Select(t => t.Name).SequenceEqual(["RealTask", "First", "Second"])
            && project.Pretasks.Select(t => t.Name).SequenceEqual(["main", "first", "second", "last"])
            && project.Presets.Select(p => p.Name).SequenceEqual(["First preset", "Second preset"])
            && project.Settings.Select(s => s.Name).SequenceEqual(["First section", "Last section"]),
            "任务、预任务、预设和设置分区须保留声明顺序。");
        Require(project.Groups.Select(g => g.Name).SequenceEqual(["shared", "extra", "last"])
            && project.Groups[0].Label == "Main group", "重复分组须保留第一项。");
        var config = project.GetConfiguration("RealTask");
        Require(config.GlobalOptions.Select(o => o.Name).SequenceEqual(["ServerRange", "Later", "Mode"])
            && config.TaskOptions.Single().DefaultCase == "final", "全局选项须按首次出现去重，同名 option 须整体替换。");
        project.CreateConfigurationFromPreset("First preset");
        Require(project.SelectedItems.Single().Name == "Second", "预设须能引用后续片段中的任务。");

        File.WriteAllText(last, """{"import":["nested.json"]}""");
        try {
            Open();
            throw new InvalidOperationException("片段中的嵌套 import 应触发字段校验。");
        } catch (InvalidDataException ex) when (ex.Message.Contains("parts/a.json")
            && ex.Message.Contains(").import")) {
        }

        File.WriteAllText(last, "{}");
        try {
            Open();
            throw new InvalidOperationException("缺失的跨文件 option 引用应触发校验。");
        } catch (InvalidDataException ex) when (ex.Message.Contains("Later")) {
        }
        EditInterface(directory, root => root["import"] = new JsonArray("parts/z.json", "parts/missing.json"));
        try {
            Open();
            throw new InvalidOperationException("缺少导入片段应阻止加载。");
        } catch (FileNotFoundException ex) when (ex.FileName == Path.Combine(parts, "missing.json")
            && ex.Message.Contains("$.import[1]")) {
        }
    }
}
