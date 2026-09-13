namespace TeamHub.Team;

public interface ICustomTeamTabService
{
    Task<IReadOnlyList<CustomTeamTabDto>> ListTabsAsync(CancellationToken cancellationToken = default);
    Task<CustomTeamTabDto?> GetTabAsync(string slug, CancellationToken cancellationToken = default);
    Task<CustomTeamTabDto?> GetTabByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<CustomTeamTabDto> SaveTabAsync(SaveCustomTeamTabRequest request, CancellationToken cancellationToken = default);
    Task ArchiveTabAsync(string id, CancellationToken cancellationToken = default);
    Task DeleteTabAsync(string id, CancellationToken cancellationToken = default);
    Task<CustomTeamTableDto> SaveTableAsync(SaveCustomTeamTableRequest request, CancellationToken cancellationToken = default);
    Task ArchiveTableAsync(string id, CancellationToken cancellationToken = default);
    Task<CustomTeamColumnDto> SaveColumnAsync(SaveCustomTeamColumnRequest request, CancellationToken cancellationToken = default);
    Task ArchiveColumnAsync(string id, CancellationToken cancellationToken = default);
    Task<CustomTeamRowDto> SaveRowAsync(SaveCustomTeamRowRequest request, CancellationToken cancellationToken = default);
    Task RemoveRowAsync(string tableId, string rowId, int version, string actor, CancellationToken cancellationToken = default);
}
