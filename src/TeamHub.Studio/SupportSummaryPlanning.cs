using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace TeamHub.Studio;

public static class MilestoneBuildReviewStatuses
{
    public const string InReview = "In Review";
    public const string InternalReviewMeeting = "Internal Review Meeting";
    public const string ReviewMeeting = "Review Meeting";

    public static IReadOnlyList<string> All { get; } =
        [InReview, InternalReviewMeeting, ReviewMeeting];
}

public sealed record ExpectedMilestoneDelivery(
    Guid Id,
    string ProjectName,
    string MilestoneDescription,
    DateOnly ExpectedDeliveryDate);

public sealed record MilestoneBuildReview(
    Guid Id,
    string ProjectName,
    string Status,
    DateOnly BuildReceiveDate,
    DateOnly Eta);

public interface ISupportSummaryPlanningService
{
    Task<IReadOnlyList<ExpectedMilestoneDelivery>> GetExpectedDeliveriesAsync(
        CancellationToken cancellationToken = default);

    Task<ExpectedMilestoneDelivery> SaveExpectedDeliveryAsync(
        ExpectedMilestoneDelivery delivery,
        CancellationToken cancellationToken = default);

    Task DeleteExpectedDeliveryAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MilestoneBuildReview>> GetBuildReviewsAsync(
        CancellationToken cancellationToken = default);

    Task<MilestoneBuildReview> SaveBuildReviewAsync(
        MilestoneBuildReview review,
        CancellationToken cancellationToken = default);

    Task DeleteBuildReviewAsync(Guid id, CancellationToken cancellationToken = default);
}

internal sealed class SqliteSupportSummaryPlanningService(IConfiguration configuration)
    : ISupportSummaryPlanningService
{
    public async Task<IReadOnlyList<ExpectedMilestoneDelivery>> GetExpectedDeliveriesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProjectName, MilestoneDescription, ExpectedDeliveryDate
            FROM SupportSummaryExpectedDeliveries
            ORDER BY ExpectedDeliveryDate, CreatedAtUtc, ProjectName;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var deliveries = new List<ExpectedMilestoneDelivery>();
        while (await reader.ReadAsync(cancellationToken))
        {
            deliveries.Add(new ExpectedMilestoneDelivery(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                ParseDate(reader.GetString(3))));
        }
        return deliveries;
    }

    public async Task<ExpectedMilestoneDelivery> SaveExpectedDeliveryAsync(
        ExpectedMilestoneDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        var normalized = delivery with
        {
            Id = delivery.Id == Guid.Empty ? Guid.NewGuid() : delivery.Id,
            ProjectName = Required(delivery.ProjectName, "Select a project."),
            MilestoneDescription = Required(delivery.MilestoneDescription, "Select a milestone description.")
        };
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SupportSummaryExpectedDeliveries
                (Id, ProjectName, MilestoneDescription, ExpectedDeliveryDate, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ($id, $projectName, $description, $date, $now, $now)
            ON CONFLICT(Id) DO UPDATE SET
                ProjectName = excluded.ProjectName,
                MilestoneDescription = excluded.MilestoneDescription,
                ExpectedDeliveryDate = excluded.ExpectedDeliveryDate,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$id", normalized.Id.ToString());
        command.Parameters.AddWithValue("$projectName", normalized.ProjectName);
        command.Parameters.AddWithValue("$description", normalized.MilestoneDescription);
        command.Parameters.AddWithValue("$date", FormatDate(normalized.ExpectedDeliveryDate));
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return normalized;
    }

    public async Task DeleteExpectedDeliveryAsync(Guid id, CancellationToken cancellationToken = default) =>
        await DeleteAsync("SupportSummaryExpectedDeliveries", id, cancellationToken);

    public async Task<IReadOnlyList<MilestoneBuildReview>> GetBuildReviewsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProjectName, Status, BuildReceiveDate, Eta
            FROM SupportSummaryBuildReviews
            ORDER BY Eta, CreatedAtUtc, ProjectName;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var reviews = new List<MilestoneBuildReview>();
        while (await reader.ReadAsync(cancellationToken))
        {
            reviews.Add(new MilestoneBuildReview(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                ParseDate(reader.GetString(3)),
                ParseDate(reader.GetString(4))));
        }
        return reviews;
    }

    public async Task<MilestoneBuildReview> SaveBuildReviewAsync(
        MilestoneBuildReview review,
        CancellationToken cancellationToken = default)
    {
        var status = MilestoneBuildReviewStatuses.All.FirstOrDefault(item =>
            string.Equals(item, review.Status?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Select a valid review status.");
        var normalized = review with
        {
            Id = review.Id == Guid.Empty ? Guid.NewGuid() : review.Id,
            ProjectName = Required(review.ProjectName, "Select a project."),
            Status = status
        };
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SupportSummaryBuildReviews
                (Id, ProjectName, Status, BuildReceiveDate, Eta, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ($id, $projectName, $status, $buildReceiveDate, $eta, $now, $now)
            ON CONFLICT(Id) DO UPDATE SET
                ProjectName = excluded.ProjectName,
                Status = excluded.Status,
                BuildReceiveDate = excluded.BuildReceiveDate,
                Eta = excluded.Eta,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$id", normalized.Id.ToString());
        command.Parameters.AddWithValue("$projectName", normalized.ProjectName);
        command.Parameters.AddWithValue("$status", normalized.Status);
        command.Parameters.AddWithValue("$buildReceiveDate", FormatDate(normalized.BuildReceiveDate));
        command.Parameters.AddWithValue("$eta", FormatDate(normalized.Eta));
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return normalized;
    }

    public async Task DeleteBuildReviewAsync(Guid id, CancellationToken cancellationToken = default) =>
        await DeleteAsync("SupportSummaryBuildReviews", id, cancellationToken);

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("StudioDb") ?? "Data Source=data/studio.db";
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureTablesAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task EnsureTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SupportSummaryExpectedDeliveries (
                Id TEXT NOT NULL CONSTRAINT PK_SupportSummaryExpectedDeliveries PRIMARY KEY,
                ProjectName TEXT NOT NULL,
                MilestoneDescription TEXT NOT NULL,
                ExpectedDeliveryDate TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SupportSummaryBuildReviews (
                Id TEXT NOT NULL CONSTRAINT PK_SupportSummaryBuildReviews PRIMARY KEY,
                ProjectName TEXT NOT NULL,
                Status TEXT NOT NULL,
                BuildReceiveDate TEXT NOT NULL,
                Eta TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var migration = connection.CreateCommand();
        migration.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('SupportSummaryBuildReviews')
            WHERE name = 'BuildReceiveDate';
            """;
        var hasBuildReceiveDate = Convert.ToInt32(
            await migration.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) > 0;
        if (hasBuildReceiveDate) return;

        migration.CommandText = """
            ALTER TABLE SupportSummaryBuildReviews ADD COLUMN BuildReceiveDate TEXT;
            UPDATE SupportSummaryBuildReviews
            SET BuildReceiveDate = substr(CreatedAtUtc, 1, 10)
            WHERE BuildReceiveDate IS NULL OR BuildReceiveDate = '';
            """;
        await migration.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteAsync(
        string tableName,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {tableName} WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Required(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(message) : value.Trim();

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDate(string date) =>
        DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
