using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TeamHub.Excel;

namespace TeamHub.Milestones;

public sealed class MilestoneSourceSettings
{
    public string SourceType { get; set; } = ExcelWorkbookSourceTypes.LocalFile;
    public string ExcelPath { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string SourceDriveId { get; set; } = string.Empty;
    public string SourceItemId { get; set; } = string.Empty;
    public string SourceDisplayName { get; set; } = string.Empty;
}

public interface IMilestoneConfigurationService
{
    Task<MilestoneSourceSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(MilestoneSourceSettings settings, CancellationToken cancellationToken = default);
}

internal sealed class SqliteMilestoneConfigurationService(
    IConfiguration configuration,
    IOptions<MilestoneConfigurationOptions> defaults) : IMilestoneConfigurationService
{
    public async Task<MilestoneSourceSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("TeamDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return FromDefaults(defaults.Value);
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SourceType, ExcelPath, SourceUrl, SourceDriveId, SourceItemId, SourceDisplayName
            FROM MilestoneSourceSettings
            WHERE Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return FromDefaults(defaults.Value);
        }

        return new MilestoneSourceSettings
        {
            SourceType = reader.GetString(0),
            ExcelPath = reader.GetString(1),
            SourceUrl = reader.GetString(2),
            SourceDriveId = reader.GetString(3),
            SourceItemId = reader.GetString(4),
            SourceDisplayName = reader.GetString(5)
        };
    }

    public async Task SaveSettingsAsync(
        MilestoneSourceSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = Validate(settings);
        var connectionString = configuration.GetConnectionString("TeamDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:TeamDb is not configured.");

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MilestoneSourceSettings
                (Id, SourceType, ExcelPath, SourceUrl, SourceDriveId, SourceItemId, SourceDisplayName, UpdatedAtUtc)
            VALUES
                (1, $sourceType, $excelPath, $sourceUrl, $sourceDriveId, $sourceItemId, $sourceDisplayName, $updatedAtUtc)
            ON CONFLICT(Id) DO UPDATE SET
                SourceType = excluded.SourceType,
                ExcelPath = excluded.ExcelPath,
                SourceUrl = excluded.SourceUrl,
                SourceDriveId = excluded.SourceDriveId,
                SourceItemId = excluded.SourceItemId,
                SourceDisplayName = excluded.SourceDisplayName,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$sourceType", normalized.SourceType);
        command.Parameters.AddWithValue("$excelPath", normalized.ExcelPath);
        command.Parameters.AddWithValue("$sourceUrl", normalized.SourceUrl);
        command.Parameters.AddWithValue("$sourceDriveId", normalized.SourceDriveId);
        command.Parameters.AddWithValue("$sourceItemId", normalized.SourceItemId);
        command.Parameters.AddWithValue("$sourceDisplayName", normalized.SourceDisplayName);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MilestoneSourceSettings Validate(MilestoneSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sourceType = settings.SourceType?.Trim() ?? string.Empty;
        if (sourceType is not (
            ExcelWorkbookSourceTypes.LocalFile
            or ExcelWorkbookSourceTypes.ExcelUrl
            or ExcelWorkbookSourceTypes.UploadedExcel
            or ExcelWorkbookSourceTypes.MicrosoftGraphExcel))
            throw new ArgumentException("Select a supported Milestone workbook source.");

        var excelPath = settings.ExcelPath?.Trim() ?? string.Empty;
        var sourceUrl = settings.SourceUrl?.Trim() ?? string.Empty;
        var driveId = settings.SourceDriveId?.Trim() ?? string.Empty;
        var itemId = settings.SourceItemId?.Trim() ?? string.Empty;
        var sourceDisplayName = settings.SourceDisplayName?.Trim() ?? string.Empty;

        if (sourceType == ExcelWorkbookSourceTypes.LocalFile && excelPath.Length == 0)
            throw new ArgumentException("Enter the Excel workbook path on the TeamHub server.");
        if (sourceType == ExcelWorkbookSourceTypes.ExcelUrl
            && (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Enter a valid HTTP or HTTPS Excel download link.");
        if (sourceType == ExcelWorkbookSourceTypes.MicrosoftGraphExcel)
        {
            if (sourceUrl.Length > 0)
                sourceUrl = ExcelWorkbookSourceValidation.ValidateMicrosoftSharingUrl(sourceUrl);
            else if (driveId.Length == 0 || itemId.Length == 0)
                throw new ArgumentException("Enter a SharePoint or OneDrive workbook link.");
        }
        if (sourceType == ExcelWorkbookSourceTypes.UploadedExcel
            && (sourceUrl.Length == 0
                || !string.Equals(Path.GetFileName(sourceUrl), sourceUrl, StringComparison.Ordinal)
                || !sourceUrl.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Browse for an .xlsx milestone workbook.");

        return new MilestoneSourceSettings
        {
            SourceType = sourceType,
            ExcelPath = excelPath,
            SourceUrl = sourceUrl,
            SourceDriveId = driveId,
            SourceItemId = itemId,
            SourceDisplayName = sourceDisplayName
        };
    }

    private static MilestoneSourceSettings FromDefaults(MilestoneConfigurationOptions options) => new()
    {
        SourceType = string.IsNullOrWhiteSpace(options.SourceType)
            ? ExcelWorkbookSourceTypes.LocalFile
            : options.SourceType.Trim(),
        ExcelPath = options.ExcelPath?.Trim() ?? string.Empty,
        SourceUrl = options.SourceUrl?.Trim() ?? string.Empty,
        SourceDriveId = options.SourceDriveId?.Trim() ?? string.Empty,
        SourceItemId = options.SourceItemId?.Trim() ?? string.Empty,
        SourceDisplayName = string.Empty
    };

    private static async Task EnsureTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MilestoneSourceSettings (
                Id INTEGER NOT NULL CONSTRAINT PK_MilestoneSourceSettings PRIMARY KEY,
                SourceType TEXT NOT NULL,
                ExcelPath TEXT NOT NULL,
                SourceUrl TEXT NOT NULL,
                SourceDriveId TEXT NOT NULL,
                SourceItemId TEXT NOT NULL,
                SourceDisplayName TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AddColumnIfMissingAsync(connection, "SourceDisplayName", "TEXT NOT NULL DEFAULT ''", cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var inspection = connection.CreateCommand();
        inspection.CommandText = "PRAGMA table_info(MilestoneSourceSettings);";
        await using var reader = await inspection.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }
        await reader.DisposeAsync();

        await using var migration = connection.CreateCommand();
        migration.CommandText = $"ALTER TABLE MilestoneSourceSettings ADD COLUMN {columnName} {definition};";
        await migration.ExecuteNonQueryAsync(cancellationToken);
    }
}
