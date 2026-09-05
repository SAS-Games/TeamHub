namespace TeamHub.Infrastructure.Options;

public sealed class WorkflowConfigurationOptions
{
    public const string SectionName = "WorkflowConfiguration";
    public string ExcelPath { get; set; } = string.Empty;
    public int ReminderCheckIntervalMinutes { get; set; } = 5;
}
