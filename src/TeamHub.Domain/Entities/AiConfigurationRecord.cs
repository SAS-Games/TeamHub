namespace TeamHub.Domain.Entities;

public sealed class AiConfigurationRecord
{
    public int Id { get; set; }
    public string SettingsJson { get; set; } = string.Empty;
    public string? CredentialProtected { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}
