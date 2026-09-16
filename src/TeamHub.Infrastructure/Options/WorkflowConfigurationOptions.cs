namespace TeamHub.Infrastructure.Options;

public sealed class WorkflowConfigurationOptions
{
    public const string SectionName = "WorkflowConfiguration";
    public int ReminderCheckIntervalMinutes { get; set; } = 5;
}
