namespace ProjectModel;

public sealed record SchedulePolicy
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required bool Enabled { get; init; }
    public required IReadOnlyList<DayOfWeek> Days
    {
        get;
        init => field = value.Distinct().Order().ToList().AsReadOnly();
    }
    public required IReadOnlyList<TimeOnly> Times
    {
        get;
        init => field = value.Select(time => new TimeOnly(time.Hour, time.Minute)).Distinct().Order()
            .ToList().AsReadOnly();
    }

    public bool FiresAt(DayOfWeek day, TimeOnly time) => Enabled && Days.Contains(day) && Times.Contains(time);
}

public sealed partial class ProjectPlanModule
{
    public IReadOnlyList<SchedulePolicy> GetSchedulePolicies(Guid configurationId) =>
        FindConfiguration(configurationId).SchedulePolicies;

    public SchedulePolicy AddSchedulePolicy(Guid configurationId, string name)
    {
        var configuration = FindConfiguration(configurationId);
        var policy = new SchedulePolicy {
            Id = Guid.NewGuid(), Name = name, Enabled = true, Times = [new TimeOnly(8, 0)],
            Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]
        };
        SaveConfiguration(configuration with { SchedulePolicies = [.. configuration.SchedulePolicies, policy] });
        return policy;
    }

    public void UpdateSchedulePolicy(Guid configurationId, SchedulePolicy policy)
    {
        var configuration = FindConfiguration(configurationId);
        var policies = configuration.SchedulePolicies.ToList();
        var index = policies.FindIndex(item => item.Id == policy.Id);
        if (index < 0) {
            throw new InvalidDataException("该任务配置中没有此定时策略。");
        }
        policies[index] = policy;
        SaveConfiguration(configuration with { SchedulePolicies = policies });
    }

    public bool DeleteSchedulePolicy(Guid configurationId, Guid policyId)
    {
        var configuration = FindConfiguration(configurationId);
        var policies = configuration.SchedulePolicies.Where(item => item.Id != policyId).ToArray();
        if (policies.Length == configuration.SchedulePolicies.Count) {
            return false;
        }
        SaveConfiguration(configuration with { SchedulePolicies = policies });
        return true;
    }
}
