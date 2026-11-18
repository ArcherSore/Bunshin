using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ProjectModel;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifySchedulePolicies(string testDirectory, string projectDirectory)
    {
        var path = Path.Combine(testDirectory, "schedule-policies.json");
        var a = Guid.NewGuid();
        File.WriteAllText(path, $$"""
            {"SchemaVersion":3,"ProjectName":"SelfTestProject","ActiveConfigurationId":"{{a}}",
             "Configurations":[{"Id":"{{a}}","Name":"A"}]}
            """);
        var project = ProjectPlanModule.Open(projectDirectory, path);
        if (project.ActiveConfigurationId != a || project.GetSchedulePolicies(a).Count != 0) {
            throw new InvalidOperationException("旧配置应保留身份，并从空策略列表开始。");
        }
        var b = project.CreateConfiguration();
        var added = project.AddSchedulePolicy(a, "早间");
        project.RenameConfiguration(a, "日常");
        var reopened = ProjectPlanModule.Open(projectDirectory, path);
        var policy = reopened.GetSchedulePolicies(a).Single();
        DayOfWeek[] workdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday];
        if (reopened.ActiveConfigurationId != b || reopened.GetSchedulePolicies(b).Count != 0
            || policy.Id != added.Id || policy.Name != "早间" || !policy.Enabled
            || !policy.Days.ToHashSet().SetEquals(workdays) || !policy.Times.SequenceEqual([new TimeOnly(8, 0)])) {
            throw new InvalidOperationException("默认策略应在工作日 08:00 启用，并在重命名和重开后仍归原配置。");
        }
        var weekend = reopened.AddSchedulePolicy(a, "周末");
        var morning = new TimeOnly(8, 0);
        var night = new TimeOnly(20, 0);
        reopened.UpdateSchedulePolicy(a, policy with { Name = "工作日早晚", Enabled = false,
            Times = [night, morning, new TimeOnly(8, 0, 30)] });
        reopened.UpdateSchedulePolicy(a, weekend with { Days = [DayOfWeek.Saturday, DayOfWeek.Sunday] });
        reopened = ProjectPlanModule.Open(projectDirectory, path);
        policy = reopened.GetSchedulePolicies(a).Single(item => item.Id == added.Id);
        var savedWeekend = reopened.GetSchedulePolicies(a).Single(item => item.Id == weekend.Id);
        if (policy.Enabled || policy.Name != "工作日早晚" || !policy.Days.ToHashSet().SetEquals(workdays)
            || !policy.Times.SequenceEqual([morning, night])
            || !savedWeekend.Days.ToHashSet().SetEquals([DayOfWeek.Saturday, DayOfWeek.Sunday])) {
            throw new InvalidOperationException("修改后应分别保留各策略的名称、开关和星期，并按分钟排序去重。");
        }
        reopened.DeleteSchedulePolicy(a, weekend.Id);
        reopened.UpdateSchedulePolicy(a, policy with { Enabled = true });
        var legacyBackup = JsonNode.Parse(File.ReadAllText(path))!;
        legacyBackup["SchemaVersion"] = 3;
        foreach (var configuration in legacyBackup["Configurations"]!.AsArray()) {
            configuration!.AsObject().Remove("DisabledItems");
        }
        var bytes = Encoding.UTF8.GetBytes(legacyBackup.ToJsonString());
        File.WriteAllBytes(path + ".bak", ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        File.WriteAllText(path, "{");
        reopened = ProjectPlanModule.Open(projectDirectory, path);
        var remaining = reopened.GetSchedulePolicies(a).Single();
        Require(reopened.ActiveConfigurationId == b && reopened.LoadWarning is not null
            && (int?)JsonNode.Parse(File.ReadAllText(path))!["SchemaVersion"] == ConfigurationSet.CurrentSchemaVersion
            && !reopened.ConfigurationsUnreadable && remaining.Name == "工作日早晚"
            && remaining.Days.ToHashSet().SetEquals(workdays) && remaining.Times.SequenceEqual([morning, night])
            && ProjectPlanModule.Open(projectDirectory, path).GetSchedulePolicies(a).Single().Id == added.Id,
            "配置 B 激活时，从备份恢复必须保留 A 的策略身份、星期和时间，并写回主配置。");
        if (!remaining.Enabled || remaining.Id != added.Id || reopened.GetSchedulePolicies(b).Count != 0) {
            throw new InvalidOperationException("删除策略和重新启用另一条策略后，应只留下配置 A 的工作日策略。");
        }
    }

    private static void VerifyInvalidSchedulePolicies(string testDirectory, string projectDirectory)
    {
        var path = Path.Combine(testDirectory, "invalid-schedule-policies.json");
        var id = Guid.NewGuid();
        var policy = $$"""
            {"Id":"{{Guid.NewGuid()}}","Name":"早班","Enabled":true,"Days":[1],"Times":["08:00:00"]}
            """;
        var invalidDay = policy.Replace("[1]", "[7]");
        foreach (var invalid in new[] { "null", "[null]", $"[{policy},{policy}]", $"[{invalidDay}]" }) {
            var json = $$"""
                {"SchemaVersion":3,"ProjectName":"SelfTestProject","ActiveConfigurationId":"{{id}}","Configurations":[
                    {"Id":"{{id}}","Name":"原配置","SchedulePolicies":{{invalid}}}]}
                """;
            File.WriteAllText(path, json);
            var project = ProjectPlanModule.Open(projectDirectory, path);
            if (!project.ConfigurationsUnreadable || File.ReadAllText(path) != json) {
                throw new InvalidOperationException("损坏的定时策略必须触发配置读取警告，并保留原文件。");
            }
        }
    }
}
